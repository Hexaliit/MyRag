using Microsoft.Extensions.Logging;
using OpenCvSharp;

// ReSharper disable TemplateIsNotCompileTimeConstantProblem

namespace Mostlylucid.DocSummarizer.Images.Services.Preprocessing;

/// <summary>
/// Unified OCR preprocessing pipeline with fast quality check and adaptive enhancement.
/// Only applies preprocessing when needed based on image quality metrics.
/// </summary>
public class OcrPreprocessor
{
    private readonly ImageQualityAssessor _assessor = new();
    private readonly SkewCorrector _skewCorrector = new();
    private readonly NoiseReducer _noiseReducer = new();
    private readonly InkExtractor _inkExtractor = new();
    private readonly OverCorrectionDetector _overCorrectionDetector = new();
    private readonly SuperResolutionService? _superResolution;
    private readonly ILogger<OcrPreprocessor>? _logger;
    private readonly OcrPreprocessorConfig _config;

    public enum PreprocessingLevel
    {
        None,
        Light,
        Moderate,
        Aggressive
    }

    public record PreprocessingResult
    {
        public Mat ProcessedImage { get; init; } = null!;
        public Mat? BinaryImage { get; init; }
        public ImageQualityAssessor.QualityReport QualityBefore { get; init; } = null!;
        public ImageQualityAssessor.QualityReport? QualityAfter { get; init; }
        public double SkewAngle { get; init; }
        public PreprocessingLevel Level { get; init; }
        public bool WasPreprocessed { get; init; }
        public bool UsedSuperResolution { get; init; }
        public bool OverCorrectionDetected { get; init; }
        public double Confidence { get; init; }
        public TimeSpan ProcessingTime { get; init; }
    }

    public OcrPreprocessor(OcrPreprocessorConfig config, ILogger<OcrPreprocessor>? logger = null)
    {
        _config = config;
        _logger = logger;

        // Initialize super-resolution if enabled and model path is available
        if (config.EnableSuperResolution && !string.IsNullOrEmpty(config.SuperResolutionModelPath))
        {
            _superResolution = new SuperResolutionService(
                config.SuperResolutionModelPath,
                logger != null ? new LoggerAdapter<SuperResolutionService>(logger) : null);
        }
    }

    /// <summary>
    /// Logger adapter to convert generic ILogger to typed ILogger{T}.
    /// </summary>
    private class LoggerAdapter<T> : ILogger<T>
    {
        private readonly ILogger _inner;
        public LoggerAdapter(ILogger inner) => _inner = inner;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => _inner.IsEnabled(logLevel);
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => _inner.Log(logLevel, eventId, state, exception, formatter);
    }

    /// <summary>
    /// Fast check to determine if preprocessing is needed.
    /// This is a lightweight assessment that runs in ~5-10ms.
    /// </summary>
    public (bool NeedsPreprocessing, ImageQualityAssessor.QualityReport Report) FastCheck(Mat image)
    {
        var report = _assessor.Analyze(image);

        var needsPreprocessing =
            report.BlurScore < _config.BlurThreshold ||
            Math.Abs(report.SkewAngle) > _config.SkewThreshold ||
            report.NoiseLevel > _config.NoiseThreshold ||
            report.ContrastScore < _config.ContrastThreshold ||
            report.BrightnessUniformity > _config.UniformityThreshold;

        return (needsPreprocessing, report);
    }

    /// <summary>
    /// Full preprocessing pipeline with adaptive enhancement.
    /// Only call this if FastCheck indicates preprocessing is needed.
    /// </summary>
    public PreprocessingResult Process(Mat image, int? currentDpi = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Step 1: Fast quality assessment
        var (needsPreprocessing, qualityBefore) = FastCheck(image);

        if (!needsPreprocessing && !_config.AlwaysProcess)
        {
            _logger.LogDebug("Image quality is good, skipping preprocessing");
            sw.Stop();
            return new PreprocessingResult
            {
                ProcessedImage = image.Clone(),
                BinaryImage = null,
                QualityBefore = qualityBefore,
                QualityAfter = null,
                SkewAngle = 0,
                Level = PreprocessingLevel.None,
                WasPreprocessed = false,
                UsedSuperResolution = false,
                OverCorrectionDetected = false,
                Confidence = 1.0,
                ProcessingTime = sw.Elapsed
            };
        }

        _logger.LogInformation(
            "Preprocessing needed: Blur={Blur:F1}, Skew={Skew:F1}, Noise={Noise:F1}, Contrast={Contrast:F2}",
            qualityBefore.BlurScore, qualityBefore.SkewAngle,
            qualityBefore.NoiseLevel, qualityBefore.ContrastScore);

        // Step 2: ML Super-Resolution for low-res images (before other processing)
        var workImage = image.Clone();
        var usedSuperResolution = false;

        if (_superResolution != null && _config.EnableSuperResolution)
        {
            var shouldUpscale = _superResolution.ShouldUpscale(workImage, currentDpi);

            if (shouldUpscale)
            {
                _logger?.LogInformation(
                    "Applying ML super-resolution: {W}x{H} @ {DPI} DPI",
                    workImage.Width, workImage.Height, currentDpi ?? 0);

                var upscaled = _superResolution.Upscale(workImage);
                workImage.Dispose();
                workImage = upscaled;
                usedSuperResolution = true;

                _logger?.LogInformation(
                    "Super-resolution complete: now {W}x{H}",
                    workImage.Width, workImage.Height);
            }
        }

        // Step 3: Classical rescale if needed (fallback when no ML super-res)
        if (!usedSuperResolution && currentDpi.HasValue && currentDpi.Value < _config.TargetDpi)
        {
            var scale = (double)_config.TargetDpi / currentDpi.Value;
            Cv2.Resize(workImage, workImage, new Size(), scale, scale, InterpolationFlags.Cubic);
        }

        // Step 3: Convert to grayscale
        using var gray = workImage.Channels() == 1
            ? workImage.Clone()
            : workImage.CvtColor(ColorConversionCodes.BGR2GRAY);

        var processed = gray.Clone();
        var skewAngle = 0.0;

        // Step 4: Deskew if needed
        if (Math.Abs(qualityBefore.SkewAngle) > _config.SkewThreshold)
        {
            var deskewResult = _skewCorrector.Deskew(processed);
            processed.Dispose();
            processed = deskewResult.Image;
            skewAngle = deskewResult.Angle;
            _logger.LogDebug("Deskewed by {Angle:F2} degrees", skewAngle);
        }

        // Step 5: Determine preprocessing level
        var level = DetermineLevel(qualityBefore);
        _logger.LogDebug("Preprocessing level: {Level}", level);

        // Step 6: Apply preprocessing based on level
        var enhanced = level switch
        {
            PreprocessingLevel.None => processed.Clone(),
            PreprocessingLevel.Light => ApplyLightPreprocessing(processed),
            PreprocessingLevel.Moderate => ApplyModeratePreprocessing(processed),
            PreprocessingLevel.Aggressive => ApplyAggressivePreprocessing(processed),
            _ => processed.Clone()
        };

        // Step 7: Binarize with over-correction check (optional)
        Mat? binary = null;
        var overCorrected = false;

        if (_config.GenerateBinary)
        {
            (binary, overCorrected) = SafeBinarize(gray, enhanced, level);
        }

        // Step 8: Final quality assessment
        var qualityAfter = _assessor.Analyze(enhanced);

        // Calculate confidence
        var confidence = CalculateConfidence(qualityBefore, qualityAfter, overCorrected);

        processed.Dispose();
        workImage.Dispose();
        sw.Stop();

        _logger.LogInformation(
            "Preprocessing complete in {Time}ms. Level={Level}, Confidence={Conf:P0}",
            sw.ElapsedMilliseconds, level, confidence);

        return new PreprocessingResult
        {
            ProcessedImage = enhanced,
            BinaryImage = binary,
            QualityBefore = qualityBefore,
            QualityAfter = qualityAfter,
            SkewAngle = skewAngle,
            Level = level,
            WasPreprocessed = true,
            UsedSuperResolution = usedSuperResolution,
            OverCorrectionDetected = overCorrected,
            Confidence = confidence,
            ProcessingTime = sw.Elapsed
        };
    }

    /// <summary>
    /// Convenience method: Process image from file path.
    /// </summary>
    public PreprocessingResult ProcessFile(string imagePath, int? dpi = null)
    {
        using var image = Cv2.ImRead(imagePath);
        if (image.Empty())
            throw new ArgumentException($"Failed to load image: {imagePath}");

        return Process(image, dpi);
    }

    /// <summary>
    /// Convenience method: Process and save to file.
    /// </summary>
    public PreprocessingResult ProcessAndSave(string inputPath, string outputPath, int? dpi = null)
    {
        var result = ProcessFile(inputPath, dpi);

        if (result.WasPreprocessed)
        {
            Cv2.ImWrite(outputPath, result.ProcessedImage);

            if (result.BinaryImage != null)
            {
                var binaryPath = Path.ChangeExtension(outputPath, ".binary.png");
                Cv2.ImWrite(binaryPath, result.BinaryImage);
            }
        }

        return result;
    }

    private PreprocessingLevel DetermineLevel(ImageQualityAssessor.QualityReport quality)
    {
        var score = 0;

        if (quality.BlurScore < 50) score += 2;
        else if (quality.BlurScore < 100) score += 1;

        if (quality.NoiseLevel > 15) score += 2;
        else if (quality.NoiseLevel > 8) score += 1;

        if (quality.ContrastScore < 0.3) score += 2;
        else if (quality.ContrastScore < 0.5) score += 1;

        if (quality.BrightnessUniformity > 0.25) score += 1;

        return score switch
        {
            0 => PreprocessingLevel.None,
            <= 2 => PreprocessingLevel.Light,
            <= 4 => PreprocessingLevel.Moderate,
            _ => PreprocessingLevel.Aggressive
        };
    }

    private Mat ApplyLightPreprocessing(Mat gray)
    {
        return _noiseReducer.Denoise(gray, NoiseReducer.DenoiseMethod.Gaussian);
    }

    private Mat ApplyModeratePreprocessing(Mat gray)
    {
        using var denoised = _noiseReducer.Denoise(gray, NoiseReducer.DenoiseMethod.Bilateral);

        using var clahe = Cv2.CreateCLAHE(2.0, new Size(8, 8));
        var enhanced = new Mat();
        clahe.Apply(denoised, enhanced);

        return enhanced;
    }

    private Mat ApplyAggressivePreprocessing(Mat gray)
    {
        using var denoised = _noiseReducer.Denoise(gray, NoiseReducer.DenoiseMethod.NonLocalMeans);

        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(30, 30));
        using var background = new Mat();
        Cv2.MorphologyEx(denoised, background, MorphTypes.Close, kernel);

        using var normalized = new Mat();
        Cv2.Subtract(background, denoised, normalized);
        Cv2.Normalize(normalized, normalized, 0, 255, NormTypes.MinMax);

        using var clahe = Cv2.CreateCLAHE(3.0, new Size(8, 8));
        var enhanced = new Mat();
        clahe.Apply(normalized, enhanced);

        return enhanced;
    }

    private (Mat binary, bool overCorrected) SafeBinarize(
        Mat originalGray, Mat processed, PreprocessingLevel level)
    {
        var method = level switch
        {
            PreprocessingLevel.None or PreprocessingLevel.Light
                => InkExtractor.BinarizationMethod.Otsu,
            PreprocessingLevel.Moderate
                => InkExtractor.BinarizationMethod.Adaptive,
            _ => InkExtractor.BinarizationMethod.Sauvola
        };

        var binary = _inkExtractor.Extract(processed, method);

        if (!_config.EnableOverCorrectionCheck)
            return (binary, false);

        var report = _overCorrectionDetector.Detect(originalGray, binary);

        if (report.IsOverCorrected)
        {
            _logger.LogWarning(
                "Over-correction detected: {Issues}. Falling back to gentler method.",
                string.Join(", ", report.Issues));

            binary.Dispose();

            var fallbackMethod = level == PreprocessingLevel.Aggressive
                ? InkExtractor.BinarizationMethod.Adaptive
                : InkExtractor.BinarizationMethod.Otsu;

            binary = _inkExtractor.Extract(processed, fallbackMethod);
            report = _overCorrectionDetector.Detect(originalGray, binary);
        }

        return (binary, report.IsOverCorrected);
    }

    private static double CalculateConfidence(
        ImageQualityAssessor.QualityReport before,
        ImageQualityAssessor.QualityReport after,
        bool overCorrected)
    {
        var confidence = 1.0;

        if (overCorrected)
            confidence -= 0.3;

        if (after.ContrastScore > before.ContrastScore)
            confidence += 0.1;

        if (after.BlurScore >= before.BlurScore * 0.9)
            confidence += 0.1;

        if (after.NoiseLevel > 10)
            confidence -= 0.1;

        return Math.Clamp(confidence, 0.0, 1.0);
    }
}
