using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
///     OCR Layout Validation Wave - Validates OCR output against layout-detected text regions.
///     Compares OCR coverage with detected text/title/caption regions to assess extraction quality.
///     Priority: 55 (after main OCR waves at 60, before benchmark at 45)
///     Emits:
///     - ocr.layout_validation.coverage: Percentage of text regions with OCR content
///     - ocr.layout_validation.quality: Overall OCR quality score
///     - ocr.layout_validation.missing_regions: Text regions without OCR output
/// </summary>
public class OcrLayoutValidationWave : IAnalysisWave
{
    private readonly ILogger<OcrLayoutValidationWave>? _logger;

    public OcrLayoutValidationWave(ILogger<OcrLayoutValidationWave>? logger = null)
    {
        _logger = logger;
    }

    public string Name => "OcrLayoutValidationWave";
    public int Priority => 55; // After OCR waves (60), before benchmark (45)
    public IReadOnlyList<string> Tags => new[] { "ocr", "validation", "layout", "quality" };

    public bool ShouldRun(string imagePath, AnalysisContext context)
    {
        // Need both layout text regions and OCR output to validate
        return context.HasSignal("layout.text_regions") &&
               (context.HasSignal("ocr.full_text") ||
                context.HasSignal("florence2.ocr_text") ||
                context.HasSignal("ocr.nanonets.text"));
    }

    public Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();
        var sw = Stopwatch.StartNew();

        // Get layout text regions
        var textRegions = context.GetCached<List<object>>("layout.text_region_bounds");
        if (textRegions == null || textRegions.Count == 0)
        {
            _logger?.LogDebug("No text regions cached for validation");
            return Task.FromResult<IEnumerable<Signal>>(signals);
        }

        // Get all available OCR outputs
        var ocrOutputs = new Dictionary<string, string?>();

        // Tesseract
        var tesseractText = context.GetValue<string>("ocr.full_text");
        if (!string.IsNullOrWhiteSpace(tesseractText))
            ocrOutputs["tesseract"] = tesseractText;

        // Florence-2
        var florence2Text = context.GetValue<string>("florence2.ocr_text");
        if (!string.IsNullOrWhiteSpace(florence2Text))
            ocrOutputs["florence2"] = florence2Text;

        // Nanonets
        var nanonetsText = context.GetValue<string>("ocr.nanonets.text");
        if (!string.IsNullOrWhiteSpace(nanonetsText))
            ocrOutputs["nanonets"] = nanonetsText;

        // DeepSeek
        var deepseekText = context.GetValue<string>("ocr.deepseek.text");
        if (!string.IsNullOrWhiteSpace(deepseekText))
            ocrOutputs["deepseek"] = deepseekText;

        // OlmOCR-2
        var olmocr2Text = context.GetValue<string>("ocr.olmocr2.text");
        if (!string.IsNullOrWhiteSpace(olmocr2Text))
            ocrOutputs["olmocr2"] = olmocr2Text;

        // Vision LLM
        var visionText = context.GetValue<string>("vision.llm.text");
        if (!string.IsNullOrWhiteSpace(visionText))
            ocrOutputs["vision_llm"] = visionText;

        if (ocrOutputs.Count == 0)
        {
            _logger?.LogDebug("No OCR output found for validation");
            signals.Add(new Signal
            {
                Key = "ocr.layout_validation.status",
                Value = "no_ocr_output",
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { "ocr", "validation", "status" }
            });
            return Task.FromResult<IEnumerable<Signal>>(signals);
        }

        // Parse text regions to extract types and areas
        var regionTypes = ParseRegionTypes(textRegions);
        var totalTextArea = CalculateTotalArea(textRegions);

        // Combine all OCR text for coverage analysis
        var combinedOcrText = string.Join("\n", ocrOutputs.Values.Where(v => v != null));
        var ocrWordCount = CountWords(combinedOcrText);

        // Estimate expected words based on text region area
        // Rough heuristic: ~50 words per 100,000 pixels for standard document text
        var estimatedWords = totalTextArea / 2000.0;
        var coverageRatio = estimatedWords > 0 ? Math.Min(1.0, ocrWordCount / estimatedWords) : 0;

        // Calculate quality score based on multiple factors
        var qualityScore = CalculateQualityScore(
            coverageRatio,
            ocrOutputs.Count,
            regionTypes,
            ocrWordCount);

        // Emit validation signals
        signals.Add(new Signal
        {
            Key = "ocr.layout_validation.coverage",
            Value = coverageRatio,
            Confidence = 0.8,
            Source = Name,
            Tags = new List<string> { "ocr", "validation", "coverage" },
            Metadata = new Dictionary<string, object>
            {
                ["ocr_word_count"] = ocrWordCount,
                ["estimated_words"] = Math.Round(estimatedWords),
                ["text_region_count"] = textRegions.Count,
                ["total_text_area"] = totalTextArea
            }
        });

        signals.Add(new Signal
        {
            Key = "ocr.layout_validation.quality",
            Value = qualityScore,
            Confidence = 0.85,
            Source = Name,
            Tags = new List<string> { "ocr", "validation", "quality" },
            Metadata = new Dictionary<string, object>
            {
                ["ocr_systems_used"] = ocrOutputs.Keys.ToList(),
                ["region_types"] = regionTypes
            }
        });

        // Region type breakdown
        signals.Add(new Signal
        {
            Key = "ocr.layout_validation.regions",
            Value = new
            {
                Total = textRegions.Count,
                Titles = regionTypes.GetValueOrDefault("Title", 0),
                Text = regionTypes.GetValueOrDefault("Text", 0),
                Captions = regionTypes.GetValueOrDefault("Caption", 0),
                ListItems = regionTypes.GetValueOrDefault("List-item", 0),
                Headers = regionTypes.GetValueOrDefault("Section-header", 0)
            },
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "ocr", "validation", "regions" }
        });

        // Check for potential issues
        var issues = new List<string>();

        if (coverageRatio < 0.3)
            issues.Add("Low OCR coverage - may have missed significant text");

        if (regionTypes.GetValueOrDefault("Title", 0) > 0 &&
            !ContainsLikelyTitle(combinedOcrText))
            issues.Add("Title region detected but no title-like text in OCR");

        if (ocrOutputs.Count == 1 && coverageRatio < 0.5)
            issues.Add("Single OCR system with low coverage - consider escalation");

        if (issues.Count > 0)
            signals.Add(new Signal
            {
                Key = "ocr.layout_validation.issues",
                Value = issues,
                Confidence = 0.7,
                Source = Name,
                Tags = new List<string> { "ocr", "validation", "issues" }
            });

        // Per-OCR-system metrics
        foreach (var (system, text) in ocrOutputs)
        {
            if (string.IsNullOrEmpty(text))
                continue;

            var wordCount = CountWords(text);
            var systemCoverage = estimatedWords > 0 ? Math.Min(1.0, wordCount / estimatedWords) : 0;

            signals.Add(new Signal
            {
                Key = $"ocr.layout_validation.{system}.coverage",
                Value = systemCoverage,
                Confidence = 0.8,
                Source = Name,
                Tags = new List<string> { "ocr", "validation", system },
                Metadata = new Dictionary<string, object>
                {
                    ["word_count"] = wordCount
                }
            });
        }

        sw.Stop();

        signals.Add(new Signal
        {
            Key = "ocr.layout_validation.duration_ms",
            Value = sw.ElapsedMilliseconds,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { "ocr", "validation", "timing" }
        });

        _logger?.LogInformation(
            "OCR layout validation: coverage={Coverage:P1}, quality={Quality:P1}, regions={Regions}, systems={Systems}",
            coverageRatio, qualityScore, textRegions.Count, ocrOutputs.Count);

        return Task.FromResult<IEnumerable<Signal>>(signals);
    }

    /// <summary>
    ///     Parse region types from cached text region bounds.
    /// </summary>
    private static Dictionary<string, int> ParseRegionTypes(List<object> regions)
    {
        var types = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var region in regions)
        {
            // Handle anonymous type from context
            var props = region.GetType().GetProperties();
            var classNameProp = props.FirstOrDefault(p => p.Name == "ClassName");
            if (classNameProp != null)
            {
                var className = classNameProp.GetValue(region)?.ToString();
                if (!string.IsNullOrEmpty(className)) types[className] = types.GetValueOrDefault(className, 0) + 1;
            }
        }

        return types;
    }

    /// <summary>
    ///     Calculate total area of text regions.
    /// </summary>
    private static long CalculateTotalArea(List<object> regions)
    {
        long totalArea = 0;

        foreach (var region in regions)
        {
            var props = region.GetType().GetProperties();
            var widthProp = props.FirstOrDefault(p => p.Name == "Width");
            var heightProp = props.FirstOrDefault(p => p.Name == "Height");

            if (widthProp != null && heightProp != null)
            {
                var width = Convert.ToInt32(widthProp.GetValue(region) ?? 0);
                var height = Convert.ToInt32(heightProp.GetValue(region) ?? 0);
                totalArea += width * height;
            }
        }

        return totalArea;
    }

    /// <summary>
    ///     Count words in text (simple whitespace split).
    /// </summary>
    private static int CountWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        return text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Count(w => w.Length >= 2); // Ignore single-char "words"
    }

    /// <summary>
    ///     Check if OCR text contains likely title content.
    /// </summary>
    private static bool ContainsLikelyTitle(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines.Take(5)) // Check first 5 lines
        {
            var trimmed = line.Trim();
            // Titles are usually short, capitalized, and don't end in common punctuation
            if (trimmed.Length >= 3 && trimmed.Length <= 100 &&
                char.IsUpper(trimmed[0]) &&
                !trimmed.EndsWith('.') &&
                !trimmed.EndsWith(','))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Calculate overall quality score based on multiple factors.
    /// </summary>
    private static double CalculateQualityScore(
        double coverageRatio,
        int ocrSystemCount,
        Dictionary<string, int> regionTypes,
        int ocrWordCount)
    {
        var score = 0.0;

        // Coverage contributes 40%
        score += coverageRatio * 0.4;

        // Multiple OCR systems provide validation (20%)
        var systemBonus = Math.Min(ocrSystemCount, 4) / 4.0;
        score += systemBonus * 0.2;

        // Word count sanity check (20%)
        // Documents with detected text regions should have some words
        var wordScore = ocrWordCount switch
        {
            > 500 => 1.0,
            > 100 => 0.8,
            > 20 => 0.5,
            > 0 => 0.3,
            _ => 0.0
        };
        score += wordScore * 0.2;

        // Region type diversity (20%)
        // Having multiple text element types suggests proper extraction
        var typeCount = regionTypes.Count;
        var typeScore = Math.Min(typeCount, 5) / 5.0;
        score += typeScore * 0.2;

        return Math.Clamp(score, 0, 1);
    }
}