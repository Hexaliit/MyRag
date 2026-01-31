using System.Reflection;
using DoomSummarizer.Models;
using DoomSummarizer.Plugins;
using Microsoft.Extensions.Logging;
using Mostlylucid.DoomSummarizer.Plugin.Books.Detection;
using Mostlylucid.DoomSummarizer.Plugin.Books.Splitters;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mostlylucid.DoomSummarizer.Plugin.Books;

/// <summary>
/// Book Summarizer plugin — provides hierarchical splitting and chapter-aware
/// summarization for novels, plays, anthologies, and long documents.
/// </summary>
public class BookProcessorPlugin : IProcessorPlugin
{
    private ILogger<BookProcessorPlugin>? _logger;
    private HierarchicalBookSplitter? _splitter;
    private readonly List<TemplateDefinition> _templates = [];

    public ProcessorPluginMetadata Metadata { get; } = new()
    {
        Name = "books",
        DisplayName = "Book Summarizer",
        Description = "Hierarchical splitting and chapter-aware summarization for books, plays, and long documents",
        Version = "1.0.0",
        SupportedExtensions = [".pdf", ".docx", ".txt", ".md", ".zip"],
        MinActivationWords = 5000,
        DocumentTypes = [
            BookTypeDetector.Fiction, BookTypeDetector.NonFiction,
            BookTypeDetector.Academic, BookTypeDetector.Technical,
            BookTypeDetector.Anthology, BookTypeDetector.Play,
            BookTypeDetector.Collection
        ]
    };

    public IReadOnlyList<IDocumentReader> Readers { get; } = [];
    public IReadOnlyList<IDocumentSplitter> Splitters => _splitter != null ? [_splitter] : [];
    public IReadOnlyList<TemplateDefinition> Templates => _templates;

    public async Task InitializeAsync(ProcessorPluginServices services, CancellationToken ct = default)
    {
        _logger = services.LoggerFactory.CreateLogger<BookProcessorPlugin>();
        _splitter = new HierarchicalBookSplitter(
            services.LoggerFactory.CreateLogger<HierarchicalBookSplitter>());

        // Load embedded templates
        LoadEmbeddedTemplates();

        _logger.LogInformation("Book Summarizer plugin initialized with {TemplateCount} templates",
            _templates.Count);

        await Task.CompletedTask;
    }

    public bool CanProcess(string markdown, ProcessingContext context)
    {
        // Quick length check
        if (context.WordCount < Metadata.MinActivationWords)
            return false;

        // Run the detector for a more nuanced decision
        var detection = BookTypeDetector.Detect(
            markdown,
            context.FileName,
            wordCount: context.WordCount);

        // Accept if confidence is reasonable and type matches our capabilities
        return detection.Confidence > 0.2 &&
               detection.Type != BookTypeDetector.Unknown;
    }

    public async Task<ProcessorResult> ProcessAsync(string markdown, ProcessorOptions options, CancellationToken ct = default)
    {
        if (_splitter == null)
            throw new InvalidOperationException("Plugin not initialized. Call InitializeAsync first.");

        // Pre-compute word count once to avoid repeated full-text splits
        var wordCount = markdown.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        // Detect type with all available signals
        long? fileSize = null;
        if (options.FilePath != null && File.Exists(options.FilePath))
            fileSize = new FileInfo(options.FilePath).Length;

        var detection = BookTypeDetector.Detect(
            markdown,
            options.FilePath != null ? Path.GetFileName(options.FilePath) : null,
            fileSize,
            wordCount);

        _logger?.LogInformation("Processing as {Type} (confidence: {Confidence:P0}, signals: {SignalCount})",
            detection.Type, detection.Confidence, detection.Signals.Count);

        // Select split options based on detection
        var patternName = options.SplitOptions.PatternName ?? SelectPattern(detection);
        var splitOpts = options.SplitOptions with { PatternName = patternName };

        // Perform hierarchical splitting — pass pattern directly, no re-detection needed
        var tree = await _splitter.SplitAsync(markdown, splitOpts, ct);

        // Compute structural weights
        var weights = ComputeStructuralWeights(tree, detection);

        return new ProcessorResult
        {
            DocumentTree = tree,
            DocumentType = detection.Type,
            StrategyUsed = SelectStrategy(tree, detection),
            Weights = weights
        };
    }

    /// <summary>
    /// Compute structural weights based on position within the document.
    /// Opening/closing chapters, abstracts, climax sections get boosted.
    /// </summary>
    private static StructuralWeights ComputeStructuralWeights(DocumentNode tree, BookTypeResult detection)
    {
        var nodeWeights = new Dictionary<string, double>();

        if (tree.Children.Count == 0)
            return new StructuralWeights { NodeWeights = nodeWeights };

        // Assign weights based on position
        for (var i = 0; i < tree.Children.Count; i++)
        {
            var child = tree.Children[i];
            var path = child.Title;

            // First and last chapters get structural importance
            if (i == 0)
                nodeWeights[path] = 1.3;
            else if (i == tree.Children.Count - 1)
                nodeWeights[path] = 1.3;
            // Climax heuristic: 70-80% through
            else if (tree.Children.Count >= 5)
            {
                var position = (double)i / tree.Children.Count;
                if (position is >= 0.65 and <= 0.85)
                    nodeWeights[path] = 1.1;
            }

            // Special titles get boosts
            var lower = child.Title.ToLowerInvariant();
            if (lower.Contains("abstract"))
                nodeWeights[path] = 1.4;
            else if (lower.Contains("conclusion") || lower.Contains("epilogue"))
                nodeWeights[path] = 1.3;
            else if (lower.Contains("introduction") || lower.Contains("prologue"))
                nodeWeights[path] = 1.2;

            // Recurse into children
            AssignChildWeights(child, nodeWeights);
        }

        return new StructuralWeights { NodeWeights = nodeWeights };
    }

    private static void AssignChildWeights(DocumentNode node, Dictionary<string, double> weights)
    {
        for (var i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            var path = $"{node.Title} > {child.Title}";

            if (i == 0)
                weights[path] = 1.15; // Opening scene/section
            else if (i == node.Children.Count - 1)
                weights[path] = 1.15; // Closing scene/section

            if (child.Children.Count > 0)
                AssignChildWeights(child, weights);
        }
    }

    private static string SelectStrategy(DocumentNode tree, BookTypeResult detection)
    {
        var totalWords = tree.TotalWordCount;

        if (totalWords < 20_000)
            return "whole-document";
        if (tree.Children.Any(c => c.Children.Count > 3))
            return "hierarchical";
        return "chapter-by-chapter";
    }

    private static string SelectPattern(BookTypeResult detection)
    {
        return detection.Type switch
        {
            BookTypeDetector.Play => "play",
            BookTypeDetector.Anthology => "anthology",
            BookTypeDetector.Collection => "anthology", // Collections use anthology-style splitting (work-level)
            BookTypeDetector.Academic => "academic",
            BookTypeDetector.Technical => "academic",
            _ => "novel"
        };
    }

    private void LoadEmbeddedTemplates()
    {
        var assembly = typeof(BookProcessorPlugin).Assembly;
        var prefix = "Mostlylucid.DoomSummarizer.Plugin.Books.Resources.templates.";

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(prefix, StringComparison.Ordinal)
                                 && n.EndsWith(".yaml", StringComparison.Ordinal)))
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null) continue;
                using var reader = new StreamReader(stream);
                var yaml = reader.ReadToEnd();

                var def = deserializer.Deserialize<TemplateDefinition>(yaml);
                if (def == null) continue;

                if (string.IsNullOrEmpty(def.Name))
                {
                    var fileName = resourceName[prefix.Length..];
                    def.Name = fileName.EndsWith(".yaml", StringComparison.Ordinal)
                        ? fileName[..^5]
                        : fileName;
                }

                _templates.Add(def);
            }
            catch
            {
                // Skip invalid template files
            }
        }
    }
}
