using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.DocSummarizer.Images.Config;
using Mostlylucid.DocSummarizer.Images.Models;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Ocr.PostProcessing;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// OCR Benchmark Wave - Compares multiple OCR systems and generates comparison reports.
/// Runs after all OCR waves to collect and compare their results.
/// Priority: 45 (after OCR waves at 50-65, before final synthesis).
/// </summary>
public class OcrBenchmarkWave : IAnalysisWave
{
    private readonly OcrConfig _config;
    private readonly OcrBenchmarkConfig _benchmarkConfig;
    private readonly SpellChecker _spellChecker;
    private readonly ILogger<OcrBenchmarkWave>? _logger;
    private readonly object _reportLock = new();

    /// <summary>
    /// OCR systems to benchmark with their signal keys.
    /// </summary>
    private static readonly (string SystemName, string SignalKey, string? TimingSignal)[] OcrSystems =
    {
        ("Tesseract", "ocr.full_text", "ocr.tesseract.duration_ms"),
        ("AdvancedOCR", "ocr.voting.consensus_text", null), // Only runs for animated images
        ("Florence2", "florence2.ocr_text", "florence2.duration_ms"),
        ("Nanonets", "ocr.nanonets.text", "ocr.nanonets.duration_ms"),
        ("DeepSeek", "ocr.deepseek.text", "ocr.deepseek.duration_ms"),
        ("OlmOCR-2", "ocr.olmocr2.text", "ocr.olmocr2.duration_ms"),
        ("VisionLLM", "vision.llm.text", "vision.llm.duration_ms"),
    };

    public string Name => "OcrBenchmarkWave";
    public int Priority => 45; // After all OCR waves (50-65), before synthesis
    public IReadOnlyList<string> Tags => new[] { "benchmark", "ocr", "quality" };

    public OcrBenchmarkWave(
        IOptions<ImageConfig> imageConfig,
        SpellChecker spellChecker,
        ILogger<OcrBenchmarkWave>? logger = null)
    {
        _config = imageConfig.Value.Ocr;
        _benchmarkConfig = _config.Benchmark;
        _spellChecker = spellChecker;
        _logger = logger;
    }

    public bool ShouldRun(string imagePath, AnalysisContext context)
    {
        // Only run if benchmarking is enabled
        if (!_benchmarkConfig.Enabled)
            return false;

        // Check if there's any OCR text to benchmark
        var hasAnyOcrText = OcrSystems.Any(sys => context.HasSignal(sys.SignalKey));
        if (!hasAnyOcrText)
        {
            _logger?.LogDebug("OcrBenchmarkWave skipped: no OCR signals found");
            return false;
        }

        return true;
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();
        var systemResults = new List<OcrSystemResult>();

        _logger?.LogInformation("Running OCR benchmark for {ImagePath}", imagePath);

        // Try to load ground truth file (image.txt or image.ext.txt)
        var groundTruth = await LoadGroundTruthAsync(imagePath, ct);
        var hasGroundTruth = !string.IsNullOrWhiteSpace(groundTruth);

        if (hasGroundTruth)
        {
            _logger?.LogInformation("Ground truth file found for {ImagePath} ({Chars} chars)",
                imagePath, groundTruth!.Length);
        }
        else
        {
            _logger?.LogInformation("No ground truth file found for {ImagePath}, using spell-check accuracy", imagePath);
        }

        // Load spell checker dictionary (fallback when no ground truth)
        var dictionaryLoaded = await _spellChecker.LoadDictionaryAsync(_benchmarkConfig.AccuracyLanguage, ct);
        if (!dictionaryLoaded && !hasGroundTruth)
        {
            _logger?.LogWarning("Dictionary not available for {Language}, accuracy will be estimated",
                _benchmarkConfig.AccuracyLanguage);
        }

        // Collect results from each OCR system
        foreach (var (systemName, signalKey, timingSignal) in OcrSystems)
        {
            var text = context.GetValue<string>(signalKey);
            var didRun = !string.IsNullOrWhiteSpace(text);

            if (!didRun)
            {
                systemResults.Add(new OcrSystemResult
                {
                    SystemName = systemName,
                    Text = null,
                    CharCount = 0,
                    WordCount = 0,
                    Accuracy = 0,
                    TimingMs = 0,
                    IsWinner = false,
                    SourceSignal = signalKey,
                    DidRun = false,
                    IsGarbled = false
                });
                continue;
            }

            // Get timing if available
            long timingMs = 0;
            if (!string.IsNullOrEmpty(timingSignal) && context.HasSignal(timingSignal))
            {
                timingMs = context.GetValue<long>(timingSignal);
            }

            // Calculate accuracy - prefer ground truth, fall back to spell checker
            double accuracy;
            bool isGarbled;

            if (hasGroundTruth)
            {
                accuracy = CalculateGroundTruthAccuracy(text!, groundTruth!);
                isGarbled = accuracy < 0.3; // Less than 30% match = garbled
            }
            else
            {
                var spellResult = dictionaryLoaded
                    ? _spellChecker.CheckTextQuality(text!, _benchmarkConfig.AccuracyLanguage)
                    : EstimateAccuracy(text!);
                accuracy = spellResult.CorrectWordsRatio;
                isGarbled = spellResult.IsGarbled;
            }

            var wordCount = text!.Split(new[] { ' ', '\n', '\r', '\t' },
                StringSplitOptions.RemoveEmptyEntries).Length;

            systemResults.Add(new OcrSystemResult
            {
                SystemName = systemName,
                Text = text,
                CharCount = text.Length,
                WordCount = wordCount,
                Accuracy = accuracy,
                TimingMs = timingMs,
                IsWinner = false, // Set below
                SourceSignal = signalKey,
                DidRun = true,
                IsGarbled = isGarbled
            });

            // Emit per-system signals
            signals.Add(new Signal
            {
                Key = $"benchmark.ocr.{systemName.ToLowerInvariant().Replace("-", "")}.accuracy",
                Value = accuracy,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "benchmark", "ocr", systemName.ToLowerInvariant() }
            });

            signals.Add(new Signal
            {
                Key = $"benchmark.ocr.{systemName.ToLowerInvariant().Replace("-", "")}.chars",
                Value = text.Length,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "benchmark", "ocr", systemName.ToLowerInvariant() }
            });

            if (timingMs > 0)
            {
                signals.Add(new Signal
                {
                    Key = $"benchmark.ocr.{systemName.ToLowerInvariant().Replace("-", "")}.timing",
                    Value = timingMs,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { "benchmark", "ocr", systemName.ToLowerInvariant() }
                });
            }
        }

        // Determine winner (highest accuracy among systems that ran)
        var runningSystems = systemResults.Where(r => r.DidRun).ToList();
        OcrSystemResult? winner = null;
        if (runningSystems.Count > 0)
        {
            winner = runningSystems.MaxBy(r => r.Accuracy);
            if (winner != null)
            {
                // Update winner flag in list
                systemResults = systemResults
                    .Select(r => r.SystemName == winner.SystemName ? r with { IsWinner = true } : r)
                    .ToList();
            }
        }

        // Create benchmark result
        var benchmarkResult = new OcrBenchmarkResult
        {
            ImagePath = imagePath,
            FileName = Path.GetFileName(imagePath),
            Timestamp = DateTime.UtcNow,
            SystemResults = systemResults,
            WinnerName = winner?.SystemName,
            WinnerAccuracy = winner?.Accuracy ?? 0,
            ConsensusText = FindConsensusText(runningSystems),
            TotalTimingMs = systemResults.Sum(r => r.TimingMs)
        };

        // Emit summary signals
        signals.Add(new Signal
        {
            Key = "benchmark.ocr.results",
            Value = benchmarkResult,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "benchmark", "ocr", "results" },
            Metadata = new Dictionary<string, object>
            {
                ["systems_ran"] = benchmarkResult.SystemsRan,
                ["winner"] = winner?.SystemName ?? "none",
                ["winner_accuracy"] = winner?.Accuracy ?? 0
            }
        });

        if (winner != null)
        {
            signals.Add(new Signal
            {
                Key = "benchmark.ocr.winner",
                Value = winner.SystemName,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "benchmark", "ocr", "winner" }
            });

            signals.Add(new Signal
            {
                Key = "benchmark.ocr.winner.accuracy",
                Value = winner.Accuracy,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "benchmark", "ocr", "winner" }
            });
        }

        // Save report to file
        await SaveReportAsync(benchmarkResult, ct);

        _logger?.LogInformation(
            "OCR benchmark complete: {SystemsRan} systems ran, winner: {Winner} ({Accuracy:P0})",
            benchmarkResult.SystemsRan,
            winner?.SystemName ?? "none",
            winner?.Accuracy ?? 0);

        return signals;
    }

    /// <summary>
    /// Load ground truth text file if it exists next to the image.
    /// Looks for: image.txt, image.ext.txt (e.g., photo.jpg.txt)
    /// </summary>
    private static async Task<string?> LoadGroundTruthAsync(string imagePath, CancellationToken ct)
    {
        // Try image.txt (without extension)
        var basePath = Path.ChangeExtension(imagePath, ".txt");
        if (File.Exists(basePath))
        {
            return await File.ReadAllTextAsync(basePath, ct);
        }

        // Try image.ext.txt (with extension)
        var extPath = imagePath + ".txt";
        if (File.Exists(extPath))
        {
            return await File.ReadAllTextAsync(extPath, ct);
        }

        return null;
    }

    /// <summary>
    /// Calculate accuracy by comparing OCR output to ground truth.
    /// Uses word-level overlap (Jaccard similarity) with normalization.
    /// </summary>
    private static double CalculateGroundTruthAccuracy(string ocrText, string groundTruth)
    {
        if (string.IsNullOrWhiteSpace(ocrText) || string.IsNullOrWhiteSpace(groundTruth))
            return 0;

        // Normalize: lowercase, remove extra whitespace, common OCR artifacts
        static string Normalize(string s) => string.Join(" ",
            s.ToLowerInvariant()
             .Replace('\n', ' ')
             .Replace('\r', ' ')
             .Replace('\t', ' ')
             .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));

        var normalizedOcr = Normalize(ocrText);
        var normalizedGt = Normalize(groundTruth);

        // Extract words (alphanumeric sequences)
        static HashSet<string> ExtractWords(string s) =>
            new(System.Text.RegularExpressions.Regex
                .Matches(s, @"[\w]+")
                .Select(m => m.Value.ToLowerInvariant())
                .Where(w => w.Length >= 2)); // Skip single chars

        var ocrWords = ExtractWords(normalizedOcr);
        var gtWords = ExtractWords(normalizedGt);

        if (gtWords.Count == 0)
            return ocrWords.Count == 0 ? 1.0 : 0;

        // Calculate word overlap (recall: how many GT words were found)
        var matchedWords = ocrWords.Intersect(gtWords).Count();
        var recall = (double)matchedWords / gtWords.Count;

        // Also consider precision to penalize garbage output
        var precision = ocrWords.Count > 0 ? (double)matchedWords / ocrWords.Count : 0;

        // F1 score balances precision and recall
        if (recall + precision == 0)
            return 0;

        var f1 = 2 * (precision * recall) / (precision + recall);

        return Math.Round(f1, 2);
    }

    /// <summary>
    /// Estimate accuracy when dictionary is not available.
    /// Uses character-level heuristics.
    /// </summary>
    private static SpellCheckResult EstimateAccuracy(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new SpellCheckResult { CorrectWordsRatio = 0, Language = "unknown" };

        // Simple heuristics:
        // - High ratio of letters to special characters = likely valid text
        // - Reasonable word lengths
        // - Not all caps or all numbers

        var words = text.Split(new[] { ' ', '\n', '\r', '\t' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (words.Length == 0)
            return new SpellCheckResult { CorrectWordsRatio = 0, Language = "unknown" };

        var validWords = 0;
        foreach (var word in words)
        {
            if (word.Length < 2) continue; // Skip single chars
            if (word.All(char.IsDigit)) continue; // Skip pure numbers

            // Check if mostly letters
            var letterRatio = (double)word.Count(char.IsLetter) / word.Length;
            if (letterRatio > 0.7)
                validWords++;
        }

        var ratio = words.Length > 0 ? (double)validWords / words.Length : 0;

        return new SpellCheckResult
        {
            CorrectWordsRatio = ratio,
            TotalWords = words.Length,
            CorrectWords = validWords,
            Language = "unknown",
            IsGarbled = ratio < 0.5
        };
    }

    /// <summary>
    /// Find text that multiple systems agree on (simple majority).
    /// </summary>
    private static string? FindConsensusText(List<OcrSystemResult> results)
    {
        var textsWithContent = results
            .Where(r => !string.IsNullOrWhiteSpace(r.Text) && !r.IsGarbled)
            .Select(r => r.Text!)
            .ToList();

        if (textsWithContent.Count == 0)
            return null;

        if (textsWithContent.Count == 1)
            return textsWithContent[0];

        // Use the text from the highest accuracy system
        var best = results
            .Where(r => !string.IsNullOrWhiteSpace(r.Text) && !r.IsGarbled)
            .MaxBy(r => r.Accuracy);

        return best?.Text;
    }

    /// <summary>
    /// Save benchmark results to Markdown report file.
    /// </summary>
    private async Task SaveReportAsync(OcrBenchmarkResult result, CancellationToken ct)
    {
        var reportPath = _benchmarkConfig.ReportOutputPath;

        try
        {
            var markdown = _benchmarkConfig.IncludeFullText
                ? result.ToMarkdownWithDetails()
                : result.ToMarkdownTable() + "\n---\n\n";

            // Thread-safe file writing
            lock (_reportLock)
            {
                var directory = Path.GetDirectoryName(reportPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (_benchmarkConfig.AppendToReport)
                {
                    // Add header if file doesn't exist or is empty
                    if (!File.Exists(reportPath) || new FileInfo(reportPath).Length == 0)
                    {
                        File.WriteAllText(reportPath, "# OCR Benchmark Results\n\n");
                    }
                    File.AppendAllText(reportPath, markdown);
                }
                else
                {
                    File.WriteAllText(reportPath, "# OCR Benchmark Results\n\n" + markdown);
                }
            }

            _logger?.LogInformation("Benchmark report saved to {ReportPath}", reportPath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save benchmark report to {ReportPath}", reportPath);
        }
    }
}
