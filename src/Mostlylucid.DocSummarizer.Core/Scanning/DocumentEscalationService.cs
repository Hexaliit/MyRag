using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mostlylucid.DocSummarizer.Scanning;

/// <summary>
/// Interface for document escalation decisions.
/// Determines when to escalate from one processing strategy to another.
/// </summary>
public interface IDocumentEscalationService
{
    /// <summary>
    /// Determine if processing should escalate after a wave completes.
    /// </summary>
    EscalationDecision ShouldEscalate(DocumentScanContext context, string completedWave);

    /// <summary>
    /// Get the recommended processing strategy for a document.
    /// </summary>
    ProcessingStrategy RecommendStrategy(string documentPath, DocumentScanContext context);
}

/// <summary>
/// Result of escalation decision.
/// </summary>
public class EscalationDecision
{
    public bool ShouldEscalate { get; init; }
    public string? TargetWave { get; init; }
    public string? Reason { get; init; }
    public double Confidence { get; init; }

    public static EscalationDecision NoEscalation() => new() { ShouldEscalate = false };

    public static EscalationDecision EscalateTo(string targetWave, string reason, double confidence = 0.8) =>
        new()
        {
            ShouldEscalate = true,
            TargetWave = targetWave,
            Reason = reason,
            Confidence = confidence
        };
}

/// <summary>
/// Recommended processing strategy.
/// </summary>
public class ProcessingStrategy
{
    public required string Pipeline { get; init; }
    public required List<string> Waves { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// Configuration for document escalation.
/// </summary>
public class DocumentEscalationConfig
{
    /// <summary>
    /// Enable automatic escalation.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Minimum text density (chars/page) before escalating to VLM OCR.
    /// </summary>
    public int MinTextDensity { get; set; } = 100;

    /// <summary>
    /// OCR confidence threshold below which to escalate.
    /// </summary>
    public double OcrConfidenceThreshold { get; set; } = 0.7;

    /// <summary>
    /// Skip VLM if native extraction confidence is above this.
    /// </summary>
    public double SkipVlmThreshold { get; set; } = 0.85;

    /// <summary>
    /// Escalate for complex table structures.
    /// </summary>
    public bool EscalateForComplexTables { get; set; } = true;

    /// <summary>
    /// Escalate when charts/figures are detected.
    /// </summary>
    public bool EscalateForCharts { get; set; } = true;

    /// <summary>
    /// Default VLM OCR wave to escalate to.
    /// </summary>
    public string DefaultVlmWave { get; set; } = "vlm-ocr";

    /// <summary>
    /// Preferred VLM model for escalation.
    /// </summary>
    public string PreferredVlmModel { get; set; } = "minicpm-v:8b";

    /// <summary>
    /// Model tiers for escalation (fast → general → quality).
    /// </summary>
    public ModelTiers Models { get; set; } = new();
}

/// <summary>
/// Model tiers for different quality/speed tradeoffs.
/// </summary>
public class ModelTiers
{
    /// <summary>
    /// Fast model for simple documents.
    /// </summary>
    public string Fast { get; set; } = "minicpm-v:8b";

    /// <summary>
    /// General purpose model.
    /// </summary>
    public string General { get; set; } = "qwen2-vl:7b";

    /// <summary>
    /// High quality model for complex documents.
    /// </summary>
    public string Quality { get; set; } = "olmocr-2";

    /// <summary>
    /// God-tier model for maximum accuracy.
    /// </summary>
    public string God { get; set; } = "anthropic:claude-3-5-sonnet";
}

/// <summary>
/// Default implementation of document escalation service.
/// </summary>
public class DocumentEscalationService : IDocumentEscalationService
{
    private readonly DocumentEscalationConfig _config;
    private readonly ILogger<DocumentEscalationService>? _logger;

    public DocumentEscalationService(
        IOptions<DocumentEscalationConfig> config,
        ILogger<DocumentEscalationService>? logger = null)
    {
        _config = config.Value;
        _logger = logger;
    }

    public EscalationDecision ShouldEscalate(DocumentScanContext context, string completedWave)
    {
        if (!_config.Enabled)
            return EscalationDecision.NoEscalation();

        // Check text density after native extraction
        if (completedWave == "native-extraction")
        {
            var textDensity = context.GetSignal<int>("extraction.text_density");
            if (textDensity < _config.MinTextDensity)
            {
                _logger?.LogDebug(
                    "Text density {Density} < {Threshold}, escalating to VLM OCR",
                    textDensity, _config.MinTextDensity);

                return EscalationDecision.EscalateTo(
                    _config.DefaultVlmWave,
                    $"Low text density ({textDensity} chars/page)",
                    0.9);
            }

            // Check native extraction confidence
            var confidence = context.GetConfidence("content.markdown");
            if (confidence < _config.OcrConfidenceThreshold)
            {
                return EscalationDecision.EscalateTo(
                    _config.DefaultVlmWave,
                    $"Low extraction confidence ({confidence:P0})",
                    0.8);
            }

            // High confidence - skip VLM
            if (confidence >= _config.SkipVlmThreshold)
            {
                _logger?.LogDebug("High confidence {Confidence:P0}, skipping VLM escalation", confidence);
                return EscalationDecision.NoEscalation();
            }
        }

        // Check for complex tables
        if (_config.EscalateForComplexTables && context.HasSignalPattern("table.*"))
        {
            var tableComplexity = context.GetSignal<double>("table.complexity");
            if (tableComplexity > 0.7)
            {
                return EscalationDecision.EscalateTo(
                    "table-understanding",
                    $"Complex table structure (complexity: {tableComplexity:P0})",
                    0.85);
            }
        }

        // Check for charts/figures
        if (_config.EscalateForCharts && context.HasSignal("content.has_charts"))
        {
            var hasCharts = context.GetSignal<bool>("content.has_charts");
            if (hasCharts)
            {
                return EscalationDecision.EscalateTo(
                    "chart-understanding",
                    "Document contains charts/figures",
                    0.8);
            }
        }

        return EscalationDecision.NoEscalation();
    }

    public ProcessingStrategy RecommendStrategy(string documentPath, DocumentScanContext context)
    {
        var extension = Path.GetExtension(documentPath).ToLowerInvariant();

        // For images, always use VLM OCR
        if (IsImageExtension(extension))
        {
            return new ProcessingStrategy
            {
                Pipeline = "image-ocr",
                Waves = ["vlm-ocr"],
                Reason = "Image file requires VLM OCR"
            };
        }

        // For PDFs, start with native extraction
        if (extension == ".pdf")
        {
            return new ProcessingStrategy
            {
                Pipeline = "auto",
                Waves = ["native-extraction", "table-extraction"],
                Reason = "PDF - try native extraction first with table support"
            };
        }

        // For DOCX, native extraction is usually sufficient
        if (extension == ".docx")
        {
            return new ProcessingStrategy
            {
                Pipeline = "native",
                Waves = ["native-extraction", "table-extraction"],
                Reason = "DOCX - native extraction with table support"
            };
        }

        // Default strategy
        return new ProcessingStrategy
        {
            Pipeline = "auto",
            Waves = ["native-extraction"],
            Reason = "Default processing strategy"
        };
    }

    /// <summary>
    /// Select the appropriate model tier based on document characteristics.
    /// </summary>
    public string SelectModel(DocumentScanContext context)
    {
        // Check for explicitly requested model
        if (!string.IsNullOrEmpty(context.Options.PreferredOcrModel))
            return context.Options.PreferredOcrModel;

        // Check document complexity signals
        var pageCount = context.GetSignal<int>("document.page_count");
        var hasComplexTables = context.GetSignal<bool>("content.has_complex_tables");
        var hasCharts = context.GetSignal<bool>("content.has_charts");

        // God tier for very complex documents
        if (hasCharts && hasComplexTables)
            return _config.Models.God;

        // Quality tier for complex tables or many pages
        if (hasComplexTables || pageCount > 50)
            return _config.Models.Quality;

        // General tier for moderate complexity
        if (pageCount > 10)
            return _config.Models.General;

        // Fast tier for simple documents
        return _config.Models.Fast;
    }

    private static bool IsImageExtension(string extension)
    {
        return extension is ".png" or ".jpg" or ".jpeg" or ".webp"
            or ".gif" or ".bmp" or ".tiff" or ".tif";
    }
}
