using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using Mostlylucid.DocSummarizer.Images.Services.Ocr.Models;
using Mostlylucid.DocSummarizer.Images.Services.Preprocessing;
using OpenCvSharp;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
///     Preprocessing wave that runs before OCR to enhance image quality.
///     Uses fast quality assessment to determine if preprocessing is needed,
///     then applies adaptive enhancement (deskew, denoise, contrast correction).
///     Caches preprocessed images for downstream OCR waves to use.
/// </summary>
public class OcrPreprocessingWave : IAnalysisWave
{
    private readonly OcrPreprocessorConfig _config;
    private readonly object _initLock = new();
    private readonly ILogger<OcrPreprocessingWave>? _logger;
    private readonly ModelDownloader? _modelDownloader;
    private bool _initialized;
    private OcrPreprocessor? _preprocessor;

    public OcrPreprocessingWave(
        OcrPreprocessorConfig? config = null,
        ModelDownloader? modelDownloader = null,
        ILogger<OcrPreprocessingWave>? logger = null)
    {
        _config = config ?? OcrPreprocessorConfig.Default;
        _modelDownloader = modelDownloader;
        _logger = logger;
    }

    public string Name => "OcrPreprocessingWave";

    /// <summary>
    ///     High priority - runs before other OCR waves (OcrWave is 60).
    /// </summary>
    public int Priority => 65;

    public IReadOnlyList<string> Tags => ["preprocessing", "ocr", "quality"];

    /// <summary>
    ///     Check if preprocessing should run based on configuration and routing.
    /// </summary>
    public bool ShouldRun(string imagePath, AnalysisContext context)
    {
        if (!_config.Enabled)
        {
            _logger?.LogDebug("OcrPreprocessingWave disabled by configuration");
            return false;
        }

        // Skip if auto-routing says to skip OCR entirely
        if (context.IsWaveSkippedByRouting("OcrWave") &&
            context.IsWaveSkippedByRouting("AdvancedOcrWave"))
        {
            _logger?.LogDebug("OcrPreprocessingWave skipped: OCR waves are being skipped");
            return false;
        }

        // Skip if this is clearly not a document/text image
        var textLikeliness = context.GetValue<double>("content.text_likeliness");
        if (textLikeliness is > 0 and < 0.1)
        {
            _logger?.LogDebug("OcrPreprocessingWave skipped: low text likeliness ({Score:P0})", textLikeliness);
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

        try
        {
            // Ensure model is downloaded and preprocessor is ready
            await EnsureInitializedAsync(ct);

            // Load image
            using var image = Cv2.ImRead(imagePath);
            if (image.Empty())
            {
                signals.Add(CreateSignal("preprocessing.error", "Failed to load image", 1.0,
                    new Dictionary<string, object> { ["path"] = imagePath }));
                return signals;
            }

            // Step 1: Fast quality check (~5-10ms)
            var (needsPreprocessing, qualityReport) = _preprocessor!.FastCheck(image);

            // Emit quality assessment signals
            signals.Add(CreateSignal("preprocessing.quality.blur", qualityReport.BlurScore, 1.0,
                new Dictionary<string, object>
                {
                    ["threshold"] = _config.BlurThreshold,
                    ["needs_correction"] = qualityReport.BlurScore < _config.BlurThreshold
                }));

            signals.Add(CreateSignal("preprocessing.quality.skew", qualityReport.SkewAngle, 1.0,
                new Dictionary<string, object>
                {
                    ["threshold"] = _config.SkewThreshold,
                    ["needs_correction"] = Math.Abs(qualityReport.SkewAngle) > _config.SkewThreshold
                }));

            signals.Add(CreateSignal("preprocessing.quality.noise", qualityReport.NoiseLevel, 1.0,
                new Dictionary<string, object>
                {
                    ["threshold"] = _config.NoiseThreshold,
                    ["needs_correction"] = qualityReport.NoiseLevel > _config.NoiseThreshold
                }));

            signals.Add(CreateSignal("preprocessing.quality.contrast", qualityReport.ContrastScore, 1.0,
                new Dictionary<string, object>
                {
                    ["threshold"] = _config.ContrastThreshold,
                    ["needs_correction"] = qualityReport.ContrastScore < _config.ContrastThreshold
                }));

            signals.Add(CreateSignal("preprocessing.quality.uniformity", qualityReport.BrightnessUniformity, 1.0,
                new Dictionary<string, object>
                {
                    ["threshold"] = _config.UniformityThreshold,
                    ["needs_correction"] = qualityReport.BrightnessUniformity > _config.UniformityThreshold
                }));

            // Emit overall quality assessment
            signals.Add(CreateSignal("preprocessing.needs_processing", needsPreprocessing, 1.0,
                new Dictionary<string, object>
                {
                    ["recommendations"] = qualityReport.Recommendations,
                    ["text_density"] = qualityReport.TextDensity
                }));

            // Step 2: If preprocessing is not needed, we're done
            if (!needsPreprocessing && !_config.AlwaysProcess)
            {
                _logger?.LogDebug("Image quality is good, skipping preprocessing for {Path}", imagePath);

                signals.Add(CreateSignal("preprocessing.skipped", true, 1.0,
                    new Dictionary<string, object>
                    {
                        ["reason"] = "Image quality meets all thresholds"
                    }));

                return signals;
            }

            // Step 3: Run full preprocessing pipeline
            _logger?.LogInformation(
                "Running preprocessing on {Path}: Blur={Blur:F1}, Skew={Skew:F1}°, Noise={Noise:F1}",
                imagePath, qualityReport.BlurScore, qualityReport.SkewAngle, qualityReport.NoiseLevel);

            var result = await Task.Run(() => _preprocessor!.Process(image), ct);

            // Emit preprocessing result signals
            signals.Add(CreateSignal("preprocessing.completed", true, result.Confidence,
                new Dictionary<string, object>
                {
                    ["level"] = result.Level.ToString(),
                    ["skew_corrected"] = result.SkewAngle,
                    ["over_correction_detected"] = result.OverCorrectionDetected,
                    ["processing_time_ms"] = result.ProcessingTime.TotalMilliseconds
                }));

            signals.Add(CreateSignal("preprocessing.level", result.Level.ToString(), 1.0,
                new Dictionary<string, object>
                {
                    ["level_int"] = (int)result.Level
                }));

            if (result.SkewAngle != 0)
                signals.Add(CreateSignal("preprocessing.deskew_angle", result.SkewAngle, 1.0,
                    new Dictionary<string, object>
                    {
                        ["corrected"] = true
                    }));

            if (result.OverCorrectionDetected)
                signals.Add(CreateSignal("preprocessing.over_correction", true, 0.7,
                    new Dictionary<string, object>
                    {
                        ["warning"] = "Preprocessing may have degraded some text"
                    }));

            // Quality improvement metrics
            if (result.QualityAfter != null)
                signals.Add(CreateSignal("preprocessing.improvement", new
                {
                    BlurBefore = result.QualityBefore.BlurScore,
                    BlurAfter = result.QualityAfter.BlurScore,
                    ContrastBefore = result.QualityBefore.ContrastScore,
                    ContrastAfter = result.QualityAfter.ContrastScore,
                    NoiseBefore = result.QualityBefore.NoiseLevel,
                    NoiseAfter = result.QualityAfter.NoiseLevel
                }, result.Confidence));

            // Step 4: Save preprocessed image to temp file for downstream waves
            var preprocessedPath = Path.Combine(
                Path.GetTempPath(),
                $"ocr_preprocessed_{Guid.NewGuid()}{Path.GetExtension(imagePath)}");

            Cv2.ImWrite(preprocessedPath, result.ProcessedImage);
            result.ProcessedImage.Dispose();

            // Cache the preprocessed path for downstream OCR waves
            context.SetCached("preprocessing.enhanced_image_path", preprocessedPath);

            signals.Add(CreateSignal("preprocessing.output_path", preprocessedPath, 1.0,
                new Dictionary<string, object>
                {
                    ["is_temporary"] = true
                }));

            // Also cache binary image if generated
            if (result.BinaryImage != null)
            {
                var binaryPath = Path.Combine(
                    Path.GetTempPath(),
                    $"ocr_binary_{Guid.NewGuid()}.png");

                Cv2.ImWrite(binaryPath, result.BinaryImage);
                result.BinaryImage.Dispose();

                context.SetCached("preprocessing.binary_image_path", binaryPath);

                signals.Add(CreateSignal("preprocessing.binary_output_path", binaryPath, 1.0,
                    new Dictionary<string, object>
                    {
                        ["is_temporary"] = true
                    }));
            }

            _logger?.LogInformation(
                "Preprocessing complete: Level={Level}, Time={Time}ms, Confidence={Conf:P0}",
                result.Level, result.ProcessingTime.TotalMilliseconds, result.Confidence);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Preprocessing failed for {Path}", imagePath);

            signals.Add(CreateSignal("preprocessing.error", ex.Message, 1.0,
                new Dictionary<string, object>
                {
                    ["exception_type"] = ex.GetType().Name
                }));
        }

        return signals;
    }

    /// <summary>
    ///     Ensures the preprocessor is initialized with model downloaded if needed.
    /// </summary>
    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        lock (_initLock)
        {
            if (_initialized) return;

            // Download super-resolution model if enabled and not yet available
            if (_config.EnableSuperResolution && _modelDownloader != null)
            {
                var modelPath = _modelDownloader.GetModelPathAsync(ModelType.RealESRGAN, ct)
                    .GetAwaiter().GetResult();

                if (!string.IsNullOrEmpty(modelPath))
                {
                    _config.SuperResolutionModelPath = modelPath;
                    _logger?.LogInformation("Super-resolution model available at {Path}", modelPath);
                }
                else
                {
                    _logger?.LogWarning("Super-resolution model download failed, disabling feature");
                    _config.EnableSuperResolution = false;
                }
            }

            _preprocessor = new OcrPreprocessor(
                _config,
                _logger != null ? new LoggerAdapter<OcrPreprocessor>(_logger) : null);

            _initialized = true;
        }
    }

    private Signal CreateSignal(string key, object value, double confidence,
        Dictionary<string, object>? metadata = null)
    {
        return new Signal
        {
            Key = key,
            Value = value,
            Confidence = confidence,
            Source = Name,
            Tags = new List<string>(Tags),
            Metadata = metadata ?? new Dictionary<string, object>()
        };
    }

    /// <summary>
    ///     Adapter to convert generic logger to typed logger.
    /// </summary>
    private class LoggerAdapter<T> : ILogger<T>
    {
        private readonly ILogger _inner;

        public LoggerAdapter(ILogger inner)
        {
            _inner = inner;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return _inner.BeginScope(state);
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return _inner.IsEnabled(logLevel);
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}