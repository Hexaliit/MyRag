namespace Mostlylucid.DocSummarizer.Services.Preprocessing;

/// <summary>
/// Configuration for the OCR preprocessing pipeline.
/// Controls quality thresholds and processing behavior.
/// </summary>
public class OcrPreprocessorConfig
{
    /// <summary>
    /// Blur score threshold below which preprocessing is triggered.
    /// Lower scores indicate blurrier images. Default: 100.
    /// Range: 0-500+ (higher = sharper)
    /// </summary>
    public double BlurThreshold { get; set; } = 100.0;

    /// <summary>
    /// Skew angle threshold in degrees above which deskewing is applied.
    /// Default: 1.0 degrees.
    /// </summary>
    public double SkewThreshold { get; set; } = 1.0;

    /// <summary>
    /// Noise level threshold above which denoising is applied.
    /// Higher values indicate more noise. Default: 8.0.
    /// Range: 0-50+ (lower = cleaner)
    /// </summary>
    public double NoiseThreshold { get; set; } = 8.0;

    /// <summary>
    /// Contrast score threshold below which contrast enhancement is applied.
    /// Uses Michelson contrast (0-1). Default: 0.5.
    /// </summary>
    public double ContrastThreshold { get; set; } = 0.5;

    /// <summary>
    /// Brightness uniformity threshold above which lighting correction is applied.
    /// Measures coefficient of variation across image regions. Default: 0.25.
    /// </summary>
    public double UniformityThreshold { get; set; } = 0.25;

    /// <summary>
    /// Target DPI for rescaling low-resolution images.
    /// Images below this DPI will be upscaled. Default: 300.
    /// </summary>
    public int TargetDpi { get; set; } = 300;

    /// <summary>
    /// Whether to generate a binary (black/white) image in addition to grayscale.
    /// Useful for some OCR engines. Default: true.
    /// </summary>
    public bool GenerateBinary { get; set; } = true;

    /// <summary>
    /// Whether to enable over-correction detection.
    /// Compares connected components before/after to prevent text degradation.
    /// Default: true.
    /// </summary>
    public bool EnableOverCorrectionCheck { get; set; } = true;

    /// <summary>
    /// Whether to always run preprocessing regardless of quality assessment.
    /// Useful for debugging or when quality assessment is unreliable.
    /// Default: false.
    /// </summary>
    public bool AlwaysProcess { get; set; } = false;

    /// <summary>
    /// Maximum time in milliseconds to spend on preprocessing a single image.
    /// Operations exceeding this will be cancelled. Default: 5000ms.
    /// </summary>
    public int TimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Whether preprocessing is enabled at all.
    /// Set to false to bypass all preprocessing. Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether to enable ML-based super-resolution for low-resolution images.
    /// Uses Real-ESRGAN ONNX model for 4x upscaling.
    /// Default: true.
    /// </summary>
    public bool EnableSuperResolution { get; set; } = true;

    /// <summary>
    /// Minimum resolution (max dimension) below which super-resolution is triggered.
    /// Images smaller than this in both dimensions will be upscaled.
    /// Default: 1000 pixels.
    /// </summary>
    public int MinResolutionForSuperResolution { get; set; } = 1000;

    /// <summary>
    /// Minimum estimated DPI below which super-resolution is triggered.
    /// Standard document scanning is 300 DPI; below 150 DPI text becomes blurry.
    /// Default: 150 DPI.
    /// </summary>
    public int MinDpiForSuperResolution { get; set; } = 150;

    /// <summary>
    /// Path to the Real-ESRGAN ONNX model for super-resolution.
    /// If null, will attempt to use ModelDownloader to auto-download.
    /// </summary>
    public string? SuperResolutionModelPath { get; set; }

    /// <summary>
    /// Creates a configuration optimized for fast processing.
    /// Uses higher thresholds to trigger preprocessing less often.
    /// </summary>
    public static OcrPreprocessorConfig Fast => new()
    {
        BlurThreshold = 50.0,
        SkewThreshold = 2.0,
        NoiseThreshold = 15.0,
        ContrastThreshold = 0.3,
        UniformityThreshold = 0.35,
        GenerateBinary = false,
        EnableOverCorrectionCheck = false,
        TimeoutMs = 2000
    };

    /// <summary>
    /// Creates a configuration optimized for quality.
    /// Uses lower thresholds to trigger preprocessing more aggressively.
    /// </summary>
    public static OcrPreprocessorConfig Quality => new()
    {
        BlurThreshold = 150.0,
        SkewThreshold = 0.5,
        NoiseThreshold = 5.0,
        ContrastThreshold = 0.6,
        UniformityThreshold = 0.2,
        GenerateBinary = true,
        EnableOverCorrectionCheck = true,
        TimeoutMs = 10000
    };

    /// <summary>
    /// Creates the default balanced configuration.
    /// </summary>
    public static OcrPreprocessorConfig Default => new();
}
