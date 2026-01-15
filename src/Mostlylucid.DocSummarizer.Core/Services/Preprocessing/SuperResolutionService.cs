using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace Mostlylucid.DocSummarizer.Services.Preprocessing;

/// <summary>
/// ML-based super-resolution using Real-ESRGAN for OCR enhancement.
/// Upscales low-resolution images by 4x while preserving text clarity.
/// </summary>
public class SuperResolutionService : IDisposable
{
    private readonly ILogger<SuperResolutionService>? _logger;
    private readonly string _modelPath;
    private InferenceSession? _session;
    private readonly object _sessionLock = new();
    private bool _modelLoadAttempted;
    private bool _modelAvailable;

    /// <summary>
    /// Minimum DPI below which super-resolution is recommended.
    /// Standard document scanning is 300 DPI; below 150 DPI text becomes blurry.
    /// </summary>
    public const int MinRecommendedDpi = 150;

    /// <summary>
    /// Maximum input dimension (width or height) for the model.
    /// Larger images are processed in tiles.
    /// </summary>
    public const int MaxInputDimension = 512;

    /// <summary>
    /// Upscale factor (Real-ESRGAN x4 model).
    /// </summary>
    public const int UpscaleFactor = 4;

    public SuperResolutionService(string modelPath, ILogger<SuperResolutionService>? logger = null)
    {
        _modelPath = modelPath;
        _logger = logger;
    }

    /// <summary>
    /// Check if super-resolution is recommended based on image dimensions and estimated DPI.
    /// </summary>
    public bool ShouldUpscale(Mat image, int? estimatedDpi = null)
    {
        // If DPI is known and below threshold
        if (estimatedDpi.HasValue && estimatedDpi.Value < MinRecommendedDpi)
            return true;

        // If image is small (likely low-res scan or screenshot)
        // A4 at 150 DPI = ~1240 x 1754 pixels
        // If both dimensions are below 1000, likely needs upscaling
        if (image.Width < 1000 && image.Height < 1000)
            return true;

        // Check text region density - small text in low-res images
        // This is a heuristic: if the image is document-sized but low resolution
        var aspectRatio = (double)image.Width / image.Height;
        var isDocumentAspect = aspectRatio > 0.5 && aspectRatio < 2.0;
        var isLowRes = Math.Max(image.Width, image.Height) < 1500;

        return isDocumentAspect && isLowRes;
    }

    /// <summary>
    /// Upscale image using Real-ESRGAN model.
    /// Returns the upscaled image or the original if upscaling fails/unavailable.
    /// </summary>
    public Mat Upscale(Mat image)
    {
        if (!EnsureModelLoaded())
        {
            _logger?.LogWarning("Super-resolution model not available, returning original image");
            return image.Clone();
        }

        try
        {
            // For large images, process in tiles
            if (image.Width > MaxInputDimension || image.Height > MaxInputDimension)
            {
                return UpscaleTiled(image);
            }

            return UpscaleSingle(image);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Super-resolution failed, returning original image");
            return image.Clone();
        }
    }

    /// <summary>
    /// Upscale a single image (fits within model input size).
    /// </summary>
    private Mat UpscaleSingle(Mat image)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Convert to RGB if needed
        using var rgb = image.Channels() == 3
            ? image.Clone()
            : image.CvtColor(ColorConversionCodes.GRAY2BGR);

        // Normalize to [0, 1] float
        using var floatImage = new Mat();
        rgb.ConvertTo(floatImage, MatType.CV_32FC3, 1.0 / 255.0);

        // Create input tensor [1, 3, H, W] - NCHW format
        var height = floatImage.Rows;
        var width = floatImage.Cols;
        var inputTensor = new DenseTensor<float>(new[] { 1, 3, height, width });

        // Copy data to tensor (BGR to RGB, HWC to CHW)
        unsafe
        {
            var ptr = (float*)floatImage.Data;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var idx = (y * width + x) * 3;
                    inputTensor[0, 2, y, x] = ptr[idx + 0]; // B -> R channel
                    inputTensor[0, 1, y, x] = ptr[idx + 1]; // G -> G channel
                    inputTensor[0, 0, y, x] = ptr[idx + 2]; // R -> B channel
                }
            }
        }

        // Run inference
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor)
        };

        using var results = _session!.Run(inputs);
        var outputTensor = results.First().AsTensor<float>();

        // Output is [1, 3, H*4, W*4]
        var outHeight = height * UpscaleFactor;
        var outWidth = width * UpscaleFactor;

        // Create output Mat
        var output = new Mat(outHeight, outWidth, MatType.CV_32FC3);

        // Copy from tensor to Mat (CHW to HWC, RGB to BGR)
        unsafe
        {
            var ptr = (float*)output.Data;
            for (var y = 0; y < outHeight; y++)
            {
                for (var x = 0; x < outWidth; x++)
                {
                    var idx = (y * outWidth + x) * 3;
                    ptr[idx + 0] = Math.Clamp(outputTensor[0, 2, y, x], 0f, 1f); // R -> B
                    ptr[idx + 1] = Math.Clamp(outputTensor[0, 1, y, x], 0f, 1f); // G -> G
                    ptr[idx + 2] = Math.Clamp(outputTensor[0, 0, y, x], 0f, 1f); // B -> R
                }
            }
        }

        // Convert back to 8-bit
        var result = new Mat();
        output.ConvertTo(result, MatType.CV_8UC3, 255.0);
        output.Dispose();

        sw.Stop();
        _logger?.LogInformation(
            "Super-resolution complete: {InW}x{InH} -> {OutW}x{OutH} in {Time}ms",
            width, height, outWidth, outHeight, sw.ElapsedMilliseconds);

        return result;
    }

    /// <summary>
    /// Upscale large images by processing in overlapping tiles.
    /// </summary>
    private Mat UpscaleTiled(Mat image)
    {
        _logger?.LogInformation(
            "Image too large ({W}x{H}), processing in tiles",
            image.Width, image.Height);

        var tileSize = MaxInputDimension;
        var overlap = 32; // Overlap to avoid seams
        var scaledOverlap = overlap * UpscaleFactor;

        // Calculate output size
        var outWidth = image.Width * UpscaleFactor;
        var outHeight = image.Height * UpscaleFactor;

        // Create output image
        var output = new Mat(outHeight, outWidth, MatType.CV_8UC3, Scalar.All(0));
        using var weightMap = new Mat(outHeight, outWidth, MatType.CV_32FC1, Scalar.All(0));

        // Process tiles
        for (var y = 0; y < image.Height; y += tileSize - overlap)
        {
            for (var x = 0; x < image.Width; x += tileSize - overlap)
            {
                // Calculate tile bounds
                var tileX = Math.Min(x, image.Width - tileSize);
                var tileY = Math.Min(y, image.Height - tileSize);
                var tileW = Math.Min(tileSize, image.Width - tileX);
                var tileH = Math.Min(tileSize, image.Height - tileY);

                if (tileW <= 0 || tileH <= 0) continue;

                // Extract tile
                using var tile = new Mat(image, new Rect(tileX, tileY, tileW, tileH));

                // Upscale tile
                using var upscaledTile = UpscaleSingle(tile);

                // Calculate output position
                var outX = tileX * UpscaleFactor;
                var outY = tileY * UpscaleFactor;
                var outTileW = tileW * UpscaleFactor;
                var outTileH = tileH * UpscaleFactor;

                // Blend into output (simple overwrite for now)
                var roi = new Rect(outX, outY, outTileW, outTileH);
                upscaledTile.CopyTo(new Mat(output, roi));
            }
        }

        return output;
    }

    /// <summary>
    /// Ensure the ONNX model is loaded.
    /// </summary>
    private bool EnsureModelLoaded()
    {
        if (_session != null)
            return true;

        lock (_sessionLock)
        {
            if (_session != null)
                return true;

            if (_modelLoadAttempted)
                return _modelAvailable;

            _modelLoadAttempted = true;

            if (!File.Exists(_modelPath))
            {
                _logger?.LogWarning("Super-resolution model not found at {Path}", _modelPath);
                _modelAvailable = false;
                return false;
            }

            try
            {
                var options = new SessionOptions();
                options.EnableMemoryPattern = true;
                options.EnableCpuMemArena = true;

                // Try to use GPU if available
                try
                {
                    options.AppendExecutionProvider_CUDA();
                    _logger?.LogInformation("Super-resolution using CUDA GPU acceleration");
                }
                catch
                {
                    _logger?.LogInformation("CUDA not available, using CPU for super-resolution");
                }

                _session = new InferenceSession(_modelPath, options);
                _modelAvailable = true;

                _logger?.LogInformation("Super-resolution model loaded from {Path}", _modelPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to load super-resolution model from {Path}", _modelPath);
                _modelAvailable = false;
                return false;
            }
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}
