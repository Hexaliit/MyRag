using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;
using Mostlylucid.GraphRag.Extraction;
using Xunit.Abstractions;

namespace LucidRAG.Tests;

/// <summary>
///     Diagnostic tests for NER services (ONNX and TorchSharp).
///     Run these to compare approaches and identify issues.
/// </summary>
public class NerDiagnosticTests
{
    private readonly ITestOutputHelper _output;

    public NerDiagnosticTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // =============================================================================
    // TorchSharp NER Tests (SKIPPED - ML.NET pipeline schema issues)
    // ML.NET's NER trainer requires Labels during training, but CreatePredictionEngine
    // expects a different schema for inference. This is a known ML.NET limitation.
    // ONNX NER is the recommended approach instead.
    // =============================================================================

    [Fact(Skip = "TorchSharp NER has ML.NET pipeline schema issues - use ONNX NER instead")]
    public async Task TorchSharp_Initialize()
    {
        _output.WriteLine("Testing TorchSharp NER initialization...");

        using var nerService = new TorchSharpNerService();

        var sw = Stopwatch.StartNew();
        await nerService.InitializeAsync();
        sw.Stop();

        _output.WriteLine($"TorchSharp NER initialized in {sw.ElapsedMilliseconds}ms");
    }

    [Theory(Skip = "TorchSharp NER has ML.NET pipeline schema issues - use ONNX NER instead")]
    [InlineData("Microsoft announced a new partnership with OpenAI in San Francisco.")]
    [InlineData("John Smith works at Google in New York City.")]
    [InlineData("The Eiffel Tower is located in Paris, France.")]
    [InlineData("Apple released the new iPhone yesterday.")]
    [InlineData("Scott Galloway wrote this blog post about Entity Framework and PostgreSQL.")]
    public async Task TorchSharp_ExtractEntities(string testText)
    {
        using var nerService = new TorchSharpNerService();
        await nerService.InitializeAsync();

        _output.WriteLine($"Input: \"{testText}\"");
        _output.WriteLine("---");

        var entities = await nerService.ExtractAsync(testText);

        _output.WriteLine($"Found {entities.Count} entities:");
        foreach (var entity in entities)
            _output.WriteLine($"  - \"{entity.Text}\" ({entity.EntityType}) confidence={entity.Confidence:F2}");

        // We expect at least some entities from these test sentences
        Assert.True(entities.Count > 0, $"Expected entities in: {testText}");
    }

    [Fact(Skip = "TorchSharp NER has ML.NET pipeline schema issues - use ONNX NER instead")]
    public async Task TorchSharp_CompareWithOnnx()
    {
        var testText = @"
            Scott Galloway demonstrates how to use Entity Framework Core with PostgreSQL
            for building a RAG system. The LucidRAG project uses CLIP embeddings from OpenAI
            and Florence-2 for image captioning. Microsoft's ML.NET framework provides
            ONNX runtime support. The system is deployed on Azure using Docker containers.
        ";

        _output.WriteLine("=== TorchSharp NER ===");
        using var torchNer = new TorchSharpNerService();
        await torchNer.InitializeAsync();

        var sw = Stopwatch.StartNew();
        var torchEntities = await torchNer.ExtractAsync(testText);
        var torchTime = sw.ElapsedMilliseconds;

        _output.WriteLine($"Found {torchEntities.Count} entities in {torchTime}ms:");
        foreach (var entity in torchEntities) _output.WriteLine($"  - \"{entity.Text}\" ({entity.EntityType})");

        _output.WriteLine("\n=== ONNX NER ===");
        var modelsDir = Path.Combine(Path.GetTempPath(), "lucidrag-ner-test");
        var downloaded = await NerModelRegistry.EnsureModelDownloadedAsync(modelsDir, NerModelRegistry.BertBaseNer);

        if (downloaded)
        {
            using var onnxNer = new OnnxNerService(modelsDir, NerModelRegistry.BertBaseNer);
            await onnxNer.InitializeAsync();

            sw.Restart();
            var onnxEntities = await onnxNer.ExtractSpansAsync(testText);
            var onnxTime = sw.ElapsedMilliseconds;

            _output.WriteLine($"Found {onnxEntities.Count} entities in {onnxTime}ms:");
            foreach (var entity in onnxEntities) _output.WriteLine($"  - \"{entity.Text}\" ({entity.EntityType})");

            _output.WriteLine("\n=== Comparison ===");
            _output.WriteLine($"TorchSharp: {torchEntities.Count} entities in {torchTime}ms");
            _output.WriteLine($"ONNX: {onnxEntities.Count} entities in {onnxTime}ms");

            var torchNames = torchEntities.Select(e => e.Text.ToLower()).ToHashSet();
            var onnxNames = onnxEntities.Select(e => e.Text.ToLower()).ToHashSet();
            var overlap = torchNames.Intersect(onnxNames).Count();

            _output.WriteLine($"Overlap: {overlap} entities found by both");
            _output.WriteLine($"TorchSharp unique: {torchNames.Except(onnxNames).Count()}");
            _output.WriteLine($"ONNX unique: {onnxNames.Except(torchNames).Count()}");
        }
        else
        {
            _output.WriteLine("ONNX model not available - skipping comparison");
        }
    }

    // =============================================================================
    // ONNX NER Tests (Legacy/Fallback)
    // =============================================================================

    [Fact]
    public async Task DiagnoseNerModelDownload()
    {
        // Check if model can be downloaded
        var modelsDir = Path.Combine(Path.GetTempPath(), "lucidrag-ner-test");
        Directory.CreateDirectory(modelsDir);

        _output.WriteLine($"Models directory: {modelsDir}");
        _output.WriteLine($"Model info: {NerModelRegistry.BertBaseNer.Name}");
        _output.WriteLine($"Model file: {NerModelRegistry.BertBaseNer.ModelFile}");
        _output.WriteLine($"Tokenizer file: {NerModelRegistry.BertBaseNer.TokenizerFile}");
        _output.WriteLine($"HuggingFace repo: {NerModelRegistry.BertBaseNer.HuggingFaceRepo}");

        var progress = new Progress<string>(msg => _output.WriteLine($"  {msg}"));

        var downloaded = await NerModelRegistry.EnsureModelDownloadedAsync(
            modelsDir,
            NerModelRegistry.BertBaseNer,
            progress);

        _output.WriteLine($"Download result: {downloaded}");

        // Check what files exist
        var modelPath = Path.Combine(modelsDir, NerModelRegistry.BertBaseNer.ModelFile);
        var tokenizerPath = Path.Combine(modelsDir, NerModelRegistry.BertBaseNer.TokenizerFile);
        var configPath = Path.Combine(modelsDir, "config.json");

        _output.WriteLine($"Model exists: {File.Exists(modelPath)} ({modelPath})");
        _output.WriteLine($"Tokenizer exists: {File.Exists(tokenizerPath)} ({tokenizerPath})");
        _output.WriteLine($"Config exists: {File.Exists(configPath)} ({configPath})");

        if (File.Exists(modelPath))
            _output.WriteLine($"Model size: {new FileInfo(modelPath).Length / 1024 / 1024} MB");

        Assert.True(downloaded, "Model download should succeed");
    }

    [Fact]
    public async Task DiagnoseNerInitialization()
    {
        var modelsDir = Path.Combine(Path.GetTempPath(), "lucidrag-ner-test");

        // Ensure model is downloaded first
        var progress = new Progress<string>(msg => _output.WriteLine($"  {msg}"));
        await NerModelRegistry.EnsureModelDownloadedAsync(modelsDir, NerModelRegistry.BertBaseNer, progress);

        _output.WriteLine("Creating NER service...");

        using var nerService = new OnnxNerService(modelsDir, NerModelRegistry.BertBaseNer);

        _output.WriteLine("Initializing NER service...");

        try
        {
            await nerService.InitializeAsync();
            _output.WriteLine("NER service initialized successfully!");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"NER initialization FAILED: {ex.GetType().Name}");
            _output.WriteLine($"Message: {ex.Message}");
            _output.WriteLine($"Stack: {ex.StackTrace}");
            throw;
        }
    }

    [Theory]
    [InlineData("Microsoft announced a new partnership with OpenAI in San Francisco.")]
    [InlineData("John Smith works at Google in New York City.")]
    [InlineData("The Eiffel Tower is located in Paris, France.")]
    [InlineData("Apple released the new iPhone yesterday.")]
    [InlineData("Scott Galloway wrote this blog post about Entity Framework and PostgreSQL.")]
    public async Task DiagnoseNerExtraction(string testText)
    {
        var modelsDir = Path.Combine(Path.GetTempPath(), "lucidrag-ner-test");

        // Ensure model is downloaded
        await NerModelRegistry.EnsureModelDownloadedAsync(modelsDir, NerModelRegistry.BertBaseNer);

        using var nerService = new OnnxNerService(modelsDir, NerModelRegistry.BertBaseNer);
        await nerService.InitializeAsync();

        _output.WriteLine($"Input: \"{testText}\"");
        _output.WriteLine("---");

        var spans = await nerService.ExtractSpansAsync(testText);

        _output.WriteLine($"Found {spans.Count} entities:");
        foreach (var span in spans)
            _output.WriteLine($"  - \"{span.Text}\" ({span.EntityType}) confidence={span.Confidence:F2}");

        if (spans.Count == 0) _output.WriteLine("WARNING: No entities found! This suggests a problem.");

        // We expect at least some entities from these test sentences
        Assert.True(spans.Count > 0, $"Expected entities in: {testText}");
    }

    [Fact]
    public async Task DiagnoseTokenization()
    {
        // Test that tokenization produces expected results
        var modelsDir = Path.Combine(Path.GetTempPath(), "lucidrag-ner-test");
        await NerModelRegistry.EnsureModelDownloadedAsync(modelsDir, NerModelRegistry.BertBaseNer);

        var tokenizerPath = Path.Combine(modelsDir, NerModelRegistry.BertBaseNer.TokenizerFile);
        _output.WriteLine($"Loading tokenizer from: {tokenizerPath}");

        using var stream = File.OpenRead(tokenizerPath);
        var tokenizer = BertTokenizer.Create(stream);

        var testText = "John Smith works at Microsoft in Seattle.";
        _output.WriteLine($"Test text: \"{testText}\"");

        var encoded = tokenizer.EncodeToTokens(testText, out var normalizedText);

        _output.WriteLine($"Normalized: \"{normalizedText}\"");
        _output.WriteLine($"Token count: {encoded.Count}");
        _output.WriteLine("Tokens:");

        foreach (var token in encoded) _output.WriteLine($"  [{token.Id}] \"{token.Value}\" (offset={token.Offset})");
    }

    [Fact]
    public async Task CompareWithHeuristicExtraction()
    {
        var testText = @"
            Scott Galloway demonstrates how to use Entity Framework Core with PostgreSQL
            for building a RAG (Retrieval-Augmented Generation) system. The LucidRAG project
            uses CLIP embeddings from OpenAI and Florence-2 for image captioning.
            Microsoft's ML.NET framework provides ONNX runtime support for running
            transformer models locally. The system is deployed on Azure using Docker containers.
        ";

        _output.WriteLine("=== Heuristic Extraction (current fallback) ===");
        var heuristicEntities = ExtractHeuristic(testText);
        _output.WriteLine($"Found {heuristicEntities.Count} entities:");
        foreach (var (name, type) in heuristicEntities.Take(20)) _output.WriteLine($"  - \"{name}\" ({type})");

        _output.WriteLine("\n=== ONNX NER Extraction ===");
        var modelsDir = Path.Combine(Path.GetTempPath(), "lucidrag-ner-test");
        await NerModelRegistry.EnsureModelDownloadedAsync(modelsDir, NerModelRegistry.BertBaseNer);

        using var nerService = new OnnxNerService(modelsDir, NerModelRegistry.BertBaseNer);
        await nerService.InitializeAsync();

        var nerEntities = await nerService.ExtractSpansAsync(testText);
        _output.WriteLine($"Found {nerEntities.Count} entities:");
        foreach (var span in nerEntities)
            _output.WriteLine($"  - \"{span.Text}\" ({span.EntityType}) conf={span.Confidence:F2}");

        _output.WriteLine("\n=== Analysis ===");
        _output.WriteLine($"Heuristic found: {heuristicEntities.Count} entities");
        _output.WriteLine($"ONNX NER found: {nerEntities.Count} entities");

        // Check overlap
        var nerNames = nerEntities.Select(e => e.Text.ToLower()).ToHashSet();
        var heuristicNames = heuristicEntities.Select(e => e.name.ToLower()).ToHashSet();
        var overlap = nerNames.Intersect(heuristicNames).Count();
        _output.WriteLine($"Overlap: {overlap} entities found by both");
    }

    private List<(string name, string type)> ExtractHeuristic(string text)
    {
        var entities = new List<(string, string)>();

        // Simple pattern-based extraction
        var patterns = new Dictionary<string, string[]>
        {
            ["PERSON"] = new[] { "Scott Galloway", "John Smith" },
            ["ORG"] = new[] { "Microsoft", "OpenAI", "Google", "Apple", "Azure" },
            ["PRODUCT"] = new[]
                { "Entity Framework", "PostgreSQL", "CLIP", "Florence-2", "ML.NET", "ONNX", "Docker", "LucidRAG" },
            ["TECH"] = new[] { "RAG", "Retrieval-Augmented Generation", "transformer" }
        };

        foreach (var (type, names) in patterns)
        foreach (var name in names)
            if (text.Contains(name, StringComparison.OrdinalIgnoreCase))
                entities.Add((name, type));

        // Also extract capitalized words as potential entities
        var capitalizedPattern = new Regex(@"\b[A-Z][a-z]+(?:\s+[A-Z][a-z]+)*\b");
        foreach (Match match in capitalizedPattern.Matches(text))
        {
            var name = match.Value;
            if (name.Length >= 3 && !entities.Any(e => e.Item1.Equals(name, StringComparison.OrdinalIgnoreCase)))
                entities.Add((name, "UNKNOWN"));
        }

        return entities.DistinctBy(e => e.Item1.ToLower()).ToList();
    }
}