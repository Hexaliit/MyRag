# OCR Preprocessing: Image Enhancement for Maximum Text Extraction Fidelity

**Date:** January 2026
**Version:** 2.0 (C# Implementation)

---

## Executive Summary

This paper examines preprocessing techniques to maximize OCR accuracy:

1. **Quality Assessment** - Detecting blur, skew, noise before processing
2. **Ink Extraction** - Separating text from background with maximum fidelity
3. **Over-Correction Detection** - Using bounding boxes to detect fidelity loss
4. **Skew Correction** - Multiple deskewing approaches using OpenCvSharp

**Key Finding:** Rigorous preprocessing can reduce Character Error Rate (CER) by up to 50%. The key is detecting issues first, then applying targeted corrections without over-processing.

---

## 1. The Over-Correction Problem

### 1.1 What is Over-Correction?

Over-correction occurs when preprocessing is too aggressive:
- **Thin stroke loss** - Fine text disappears during binarization
- **Character merging** - Adjacent characters fuse together
- **Broken characters** - Letters fragment into pieces
- **Noise amplification** - Background becomes foreground

### 1.2 Detecting Over-Correction with Bounding Boxes

Compare connected component metrics before and after preprocessing:

```csharp
using OpenCvSharp;

namespace DocSummarizer.Preprocessing;

/// <summary>
/// Detects over-correction by comparing bounding box metrics.
/// </summary>
public class OverCorrectionDetector
{
    public record ComponentStats
    {
        public int NumComponents { get; init; }
        public int TotalInkArea { get; init; }
        public double MeanComponentArea { get; init; }
        public Rect[] BoundingBoxes { get; init; } = Array.Empty<Rect>();
    }

    public record OverCorrectionReport
    {
        public double ComponentRatio { get; init; }
        public double InkRatio { get; init; }
        public double AreaRatio { get; init; }
        public bool IsOverCorrected { get; init; }
        public string[] Issues { get; init; } = Array.Empty<string>();
        public double Severity { get; init; }
    }

    /// <summary>
    /// Detect over-correction by comparing original and processed images.
    /// </summary>
    public OverCorrectionReport Detect(Mat original, Mat processed)
    {
        var origStats = GetComponentStats(original);
        var procStats = GetComponentStats(processed);

        var componentRatio = (double)procStats.NumComponents /
                            Math.Max(origStats.NumComponents, 1);
        var inkRatio = (double)procStats.TotalInkArea /
                      Math.Max(origStats.TotalInkArea, 1);
        var areaRatio = procStats.MeanComponentArea /
                       Math.Max(origStats.MeanComponentArea, 1);

        var issues = new List<string>();

        // Detect fragmentation (characters breaking apart)
        if (componentRatio > 1.5)
            issues.Add("character_fragmentation");
        else if (componentRatio < 0.7)
            issues.Add("character_merging");

        // Detect ink loss or noise amplification
        if (inkRatio < 0.7)
            issues.Add("ink_loss");
        else if (inkRatio > 1.3)
            issues.Add("noise_amplification");

        // Detect thin stroke loss
        if (areaRatio < 0.5)
            issues.Add("thin_stroke_loss");

        return new OverCorrectionReport
        {
            ComponentRatio = componentRatio,
            InkRatio = inkRatio,
            AreaRatio = areaRatio,
            IsOverCorrected = issues.Count > 0,
            Issues = issues.ToArray(),
            Severity = issues.Count / 4.0
        };
    }

    private ComponentStats GetComponentStats(Mat image)
    {
        // Convert to grayscale if needed
        var gray = image.Channels() == 1
            ? image
            : image.CvtColor(ColorConversionCodes.BGR2GRAY);

        // Binarize with Otsu
        var binary = new Mat();
        Cv2.Threshold(gray, binary, 0, 255,
            ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

        // Find connected components
        var labels = new Mat();
        var stats = new Mat();
        var centroids = new Mat();
        var numLabels = Cv2.ConnectedComponentsWithStats(
            binary, labels, stats, centroids, PixelConnectivity.Connectivity8);

        // Extract stats (skip background label 0)
        var areas = new List<int>();
        var boxes = new List<Rect>();

        for (int i = 1; i < numLabels; i++)
        {
            var area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);

            // Filter tiny noise
            if (area > 10)
            {
                areas.Add(area);
                boxes.Add(new Rect(
                    stats.At<int>(i, (int)ConnectedComponentsTypes.Left),
                    stats.At<int>(i, (int)ConnectedComponentsTypes.Top),
                    stats.At<int>(i, (int)ConnectedComponentsTypes.Width),
                    stats.At<int>(i, (int)ConnectedComponentsTypes.Height)
                ));
            }
        }

        if (gray != image)
            gray.Dispose();
        binary.Dispose();
        labels.Dispose();
        stats.Dispose();
        centroids.Dispose();

        return new ComponentStats
        {
            NumComponents = areas.Count,
            TotalInkArea = areas.Sum(),
            MeanComponentArea = areas.Count > 0 ? areas.Average() : 0,
            BoundingBoxes = boxes.ToArray()
        };
    }
}
```

### 1.3 Fidelity Metrics

| Metric | Healthy Range | Over-Correction Signal |
|--------|---------------|------------------------|
| Component Ratio | 0.85 - 1.15 | < 0.7 (merging) or > 1.5 (fragmentation) |
| Ink Ratio | 0.80 - 1.20 | < 0.7 (loss) or > 1.3 (noise) |
| Area Ratio | 0.70 - 1.30 | < 0.5 (thin stroke loss) |

---

## 2. Image Quality Assessment

### 2.1 When Does an Image Need Preprocessing?

Not all images benefit from preprocessing. Assess quality first:

```csharp
using OpenCvSharp;

namespace DocSummarizer.Preprocessing;

/// <summary>
/// Assesses document image quality to determine preprocessing needs.
/// </summary>
public class ImageQualityAssessor
{
    public record QualityReport
    {
        public double BlurScore { get; init; }
        public double SkewAngle { get; init; }
        public double NoiseLevel { get; init; }
        public double ContrastScore { get; init; }
        public double BrightnessUniformity { get; init; }
        public double TextDensity { get; init; }
        public bool NeedsPreprocessing { get; init; }
        public string[] Recommendations { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Analyze image quality and return recommendations.
    /// </summary>
    public QualityReport Analyze(Mat image)
    {
        var gray = image.Channels() == 1
            ? image
            : image.CvtColor(ColorConversionCodes.BGR2GRAY);

        var blur = EstimateBlur(gray);
        var skew = EstimateSkew(gray);
        var noise = EstimateNoise(gray);
        var contrast = EstimateContrast(gray);
        var uniformity = EstimateUniformity(gray);
        var density = EstimateTextDensity(gray);

        var recommendations = new List<string>();

        if (blur < 50)
            recommendations.Add("Image is blurry - consider sharpening or rejection");
        if (Math.Abs(skew) > 2.0)
            recommendations.Add($"Skew detected ({skew:F1} deg) - apply deskewing");
        if (noise > 15)
            recommendations.Add("High noise - apply denoising");
        if (contrast < 0.3)
            recommendations.Add("Low contrast - apply CLAHE");
        if (uniformity > 0.25)
            recommendations.Add("Uneven illumination - normalize background");

        if (gray != image)
            gray.Dispose();

        return new QualityReport
        {
            BlurScore = blur,
            SkewAngle = skew,
            NoiseLevel = noise,
            ContrastScore = contrast,
            BrightnessUniformity = uniformity,
            TextDensity = density,
            NeedsPreprocessing = recommendations.Count > 0,
            Recommendations = recommendations.ToArray()
        };
    }

    /// <summary>
    /// Estimate blur using Laplacian variance.
    /// Higher = sharper, Lower = blurrier.
    /// </summary>
    private double EstimateBlur(Mat gray)
    {
        var laplacian = new Mat();
        Cv2.Laplacian(gray, laplacian, MatType.CV_64F);

        Cv2.MeanStdDev(laplacian, out _, out var stddev);
        laplacian.Dispose();

        return stddev.Val0 * stddev.Val0; // Variance
    }

    /// <summary>
    /// Estimate skew angle using Hough lines.
    /// </summary>
    private double EstimateSkew(Mat gray)
    {
        var edges = new Mat();
        Cv2.Canny(gray, edges, 50, 150);

        var lines = Cv2.HoughLinesP(
            edges, 1, Math.PI / 180, 100,
            minLineLength: 100, maxLineGap: 10);

        edges.Dispose();

        if (lines.Length == 0)
            return 0.0;

        var angles = new List<double>();
        foreach (var line in lines)
        {
            var angle = Math.Atan2(
                line.P2.Y - line.P1.Y,
                line.P2.X - line.P1.X
            ) * 180 / Math.PI;

            // Only near-horizontal lines
            if (Math.Abs(angle) < 45)
                angles.Add(angle);
        }

        if (angles.Count == 0)
            return 0.0;

        // Return median (robust to outliers)
        angles.Sort();
        return angles[angles.Count / 2];
    }

    /// <summary>
    /// Estimate noise using median absolute deviation.
    /// </summary>
    private double EstimateNoise(Mat gray)
    {
        var blur = new Mat();
        Cv2.GaussianBlur(gray, blur, new Size(5, 5), 0);

        var noise = new Mat();
        Cv2.Subtract(gray, blur, noise);

        // Calculate MAD
        var noiseArray = new byte[noise.Rows * noise.Cols];
        noise.GetArray(out noiseArray);

        var median = noiseArray.OrderBy(x => x).ElementAt(noiseArray.Length / 2);
        var mad = noiseArray.Select(x => Math.Abs(x - median))
                           .OrderBy(x => x)
                           .ElementAt(noiseArray.Length / 2);

        blur.Dispose();
        noise.Dispose();

        return mad;
    }

    /// <summary>
    /// Estimate contrast using Michelson contrast.
    /// </summary>
    private double EstimateContrast(Mat gray)
    {
        var data = new byte[gray.Rows * gray.Cols];
        gray.GetArray(out data);

        Array.Sort(data);
        var minVal = data[(int)(data.Length * 0.05)];
        var maxVal = data[(int)(data.Length * 0.95)];

        if (maxVal + minVal == 0)
            return 0.0;

        return (double)(maxVal - minVal) / (maxVal + minVal);
    }

    /// <summary>
    /// Check for uneven illumination using grid analysis.
    /// Returns coefficient of variation (lower = more uniform).
    /// </summary>
    private double EstimateUniformity(Mat gray)
    {
        var gridH = gray.Rows / 4;
        var gridW = gray.Cols / 4;
        var means = new List<double>();

        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                var roi = new Rect(j * gridW, i * gridH, gridW, gridH);
                var region = new Mat(gray, roi);
                means.Add(region.Mean().Val0);
                region.Dispose();
            }
        }

        var mean = means.Average();
        var std = Math.Sqrt(means.Average(x => Math.Pow(x - mean, 2)));

        return mean > 0 ? std / mean : 0;
    }

    /// <summary>
    /// Estimate text density (percentage of image with text).
    /// </summary>
    private double EstimateTextDensity(Mat gray)
    {
        var binary = new Mat();
        Cv2.Threshold(gray, binary, 0, 255,
            ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

        var nonZero = Cv2.CountNonZero(binary);
        var total = binary.Rows * binary.Cols;

        binary.Dispose();

        return (double)nonZero / total;
    }
}
```

### 2.2 Quality Thresholds

| Metric | Good | Needs Work | Action |
|--------|------|------------|--------|
| Blur Score | > 100 | < 50 | Sharpen or reject |
| Skew Angle | < 1 deg | > 2 deg | Deskew |
| Noise Level | < 5 | > 15 | Denoise |
| Contrast | > 0.5 | < 0.3 | CLAHE |
| Uniformity | < 0.15 | > 0.25 | Background normalization |

---

## 3. Ink Extraction (Binarization)

### 3.1 Method Selection

| Method | Best For | C# Method |
|--------|----------|-----------|
| Otsu | Clean scans | `ThresholdTypes.Otsu` |
| Adaptive | Uneven lighting | `AdaptiveThresholdTypes.GaussianC` |
| Sauvola | Historical/degraded | Custom implementation |
| CLAHE + Otsu | Low contrast | Two-step process |

### 3.2 C# Implementation

```csharp
using OpenCvSharp;

namespace DocSummarizer.Preprocessing;

/// <summary>
/// Ink extraction (binarization) with multiple methods.
/// </summary>
public class InkExtractor
{
    public enum BinarizationMethod
    {
        Otsu,
        Adaptive,
        Sauvola,
        ClaheOtsu,
        Morphological
    }

    /// <summary>
    /// Extract ink using the specified method.
    /// </summary>
    public Mat Extract(Mat gray, BinarizationMethod method)
    {
        return method switch
        {
            BinarizationMethod.Otsu => OtsuBinarize(gray),
            BinarizationMethod.Adaptive => AdaptiveBinarize(gray),
            BinarizationMethod.Sauvola => SauvolaBinarize(gray),
            BinarizationMethod.ClaheOtsu => ClaheOtsuBinarize(gray),
            BinarizationMethod.Morphological => MorphologicalBinarize(gray),
            _ => OtsuBinarize(gray)
        };
    }

    /// <summary>
    /// Simple Otsu binarization - best for clean scans.
    /// </summary>
    private Mat OtsuBinarize(Mat gray)
    {
        var binary = new Mat();
        Cv2.Threshold(gray, binary, 0, 255,
            ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        return binary;
    }

    /// <summary>
    /// Adaptive thresholding - best for uneven illumination.
    /// </summary>
    private Mat AdaptiveBinarize(Mat gray, int blockSize = 31, double c = 10)
    {
        var binary = new Mat();
        Cv2.AdaptiveThreshold(
            gray, binary, 255,
            AdaptiveThresholdTypes.GaussianC,
            ThresholdTypes.BinaryInv,
            blockSize, c);
        return binary;
    }

    /// <summary>
    /// Sauvola binarization - best for historical/degraded documents.
    /// T(x,y) = mean(x,y) * (1 + k * (std(x,y) / r - 1))
    /// </summary>
    private Mat SauvolaBinarize(Mat gray, int windowSize = 25,
                                 double k = 0.2, double r = 128)
    {
        var binary = new Mat(gray.Size(), MatType.CV_8UC1);

        // Calculate local mean
        var mean = new Mat();
        Cv2.Blur(gray, mean, new Size(windowSize, windowSize));

        // Calculate local mean of squares
        var graySq = new Mat();
        Cv2.Multiply(gray, gray, graySq, 1.0 / 255.0);
        var meanSq = new Mat();
        Cv2.Blur(graySq, meanSq, new Size(windowSize, windowSize));

        // Calculate local standard deviation
        // std = sqrt(meanSq - mean^2)
        var meanF = new Mat();
        var meanSqF = new Mat();
        mean.ConvertTo(meanF, MatType.CV_32F);
        meanSq.ConvertTo(meanSqF, MatType.CV_32F);

        var variance = new Mat();
        Cv2.Multiply(meanF, meanF, variance, 1.0 / 255.0);
        Cv2.Subtract(meanSqF, variance, variance);
        Cv2.Max(variance, 0, variance); // Ensure non-negative

        var std = new Mat();
        Cv2.Sqrt(variance, std);

        // Sauvola threshold: T = mean * (1 + k * (std / r - 1))
        var threshold = new Mat();
        Cv2.Divide(std, r, threshold);
        Cv2.Subtract(threshold, 1, threshold);
        Cv2.Multiply(threshold, k, threshold);
        Cv2.Add(threshold, 1, threshold);
        Cv2.Multiply(meanF, threshold, threshold);

        // Apply threshold
        var grayF = new Mat();
        gray.ConvertTo(grayF, MatType.CV_32F);

        Cv2.Compare(grayF, threshold, binary, CmpType.LT);

        // Cleanup
        mean.Dispose(); graySq.Dispose(); meanSq.Dispose();
        meanF.Dispose(); meanSqF.Dispose(); variance.Dispose();
        std.Dispose(); threshold.Dispose(); grayF.Dispose();

        return binary;
    }

    /// <summary>
    /// CLAHE + Otsu - best for low contrast documents.
    /// </summary>
    private Mat ClaheOtsuBinarize(Mat gray, double clipLimit = 2.0)
    {
        var clahe = Cv2.CreateCLAHE(clipLimit, new Size(8, 8));
        var enhanced = new Mat();
        clahe.Apply(gray, enhanced);

        var binary = OtsuBinarize(enhanced);
        enhanced.Dispose();

        return binary;
    }

    /// <summary>
    /// Morphological background removal - best for complex backgrounds.
    /// </summary>
    private Mat MorphologicalBinarize(Mat gray, int kernelSize = 15)
    {
        // Estimate background using morphological closing
        var kernel = Cv2.GetStructuringElement(
            MorphShapes.Ellipse, new Size(kernelSize, kernelSize));
        var background = new Mat();
        Cv2.MorphologyEx(gray, background, MorphTypes.Close, kernel);

        // Subtract background
        var foreground = new Mat();
        Cv2.Subtract(background, gray, foreground);

        // Normalize and threshold
        Cv2.Normalize(foreground, foreground, 0, 255, NormTypes.MinMax);
        var binary = OtsuBinarize(foreground);

        background.Dispose();
        foreground.Dispose();

        return binary;
    }
}
```

---

## 4. Skew Correction

### 4.1 Multiple Methods

```csharp
using OpenCvSharp;

namespace DocSummarizer.Preprocessing;

/// <summary>
/// Document skew correction with multiple methods.
/// </summary>
public class SkewCorrector
{
    public enum DeskewMethod
    {
        MinAreaRect,
        HoughTransform,
        ProjectionProfile
    }

    public record DeskewResult
    {
        public Mat Image { get; init; } = null!;
        public double Angle { get; init; }
        public DeskewMethod Method { get; init; }
    }

    /// <summary>
    /// Deskew image using specified method.
    /// </summary>
    public DeskewResult Deskew(Mat image, DeskewMethod method = DeskewMethod.HoughTransform)
    {
        var (result, angle) = method switch
        {
            DeskewMethod.MinAreaRect => DeskewMinArea(image),
            DeskewMethod.HoughTransform => DeskewHough(image),
            DeskewMethod.ProjectionProfile => DeskewProjection(image),
            _ => DeskewHough(image)
        };

        return new DeskewResult
        {
            Image = result,
            Angle = angle,
            Method = method
        };
    }

    /// <summary>
    /// Deskew using minimum area bounding rectangle.
    /// Fast and effective for most documents.
    /// </summary>
    private (Mat result, double angle) DeskewMinArea(Mat image)
    {
        var gray = image.Channels() == 1
            ? image
            : image.CvtColor(ColorConversionCodes.BGR2GRAY);

        var binary = new Mat();
        Cv2.Threshold(gray, binary, 0, 255,
            ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

        // Find all text pixels
        Cv2.FindNonZero(binary, out var coords);

        if (coords.Length < 10)
        {
            binary.Dispose();
            if (gray != image) gray.Dispose();
            return (image.Clone(), 0.0);
        }

        // Get minimum area rectangle
        var rect = Cv2.MinAreaRect(coords);
        var angle = rect.Angle;

        // Adjust angle
        if (angle < -45)
            angle = 90 + angle;
        else if (angle > 45)
            angle = angle - 90;

        // Rotate
        var center = new Point2f(image.Width / 2f, image.Height / 2f);
        var rotMat = Cv2.GetRotationMatrix2D(center, angle, 1.0);
        var rotated = new Mat();
        Cv2.WarpAffine(image, rotated, rotMat,
            image.Size(), InterpolationFlags.Cubic,
            BorderTypes.Replicate);

        binary.Dispose();
        if (gray != image) gray.Dispose();

        return (rotated, angle);
    }

    /// <summary>
    /// Deskew using Hough line detection.
    /// More accurate for documents with clear horizontal lines.
    /// </summary>
    private (Mat result, double angle) DeskewHough(Mat image)
    {
        var gray = image.Channels() == 1
            ? image
            : image.CvtColor(ColorConversionCodes.BGR2GRAY);

        var edges = new Mat();
        Cv2.Canny(gray, edges, 50, 150);

        // Dilate to connect broken edges
        var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.Dilate(edges, edges, kernel);

        // Detect lines
        var lines = Cv2.HoughLinesP(edges, 1, Math.PI / 180, 100,
            minLineLength: 100, maxLineGap: 10);

        edges.Dispose();
        if (gray != image) gray.Dispose();

        if (lines.Length == 0)
            return (image.Clone(), 0.0);

        // Calculate angles of near-horizontal lines
        var angles = new List<double>();
        foreach (var line in lines)
        {
            if (line.P2.X == line.P1.X) continue;

            var angle = Math.Atan2(
                line.P2.Y - line.P1.Y,
                line.P2.X - line.P1.X
            ) * 180 / Math.PI;

            if (Math.Abs(angle) < 30)
                angles.Add(angle);
        }

        if (angles.Count == 0)
            return (image.Clone(), 0.0);

        // Use median angle
        angles.Sort();
        var medianAngle = angles[angles.Count / 2];

        // Rotate
        var center = new Point2f(image.Width / 2f, image.Height / 2f);
        var rotMat = Cv2.GetRotationMatrix2D(center, medianAngle, 1.0);
        var rotated = new Mat();
        Cv2.WarpAffine(image, rotated, rotMat,
            image.Size(), InterpolationFlags.Cubic,
            BorderTypes.Replicate);

        return (rotated, medianAngle);
    }

    /// <summary>
    /// Deskew using projection profile variance.
    /// Best for dense text documents.
    /// </summary>
    private (Mat result, double angle) DeskewProjection(Mat image,
        double angleRange = 15.0, double angleStep = 0.5)
    {
        var gray = image.Channels() == 1
            ? image
            : image.CvtColor(ColorConversionCodes.BGR2GRAY);

        var binary = new Mat();
        Cv2.Threshold(gray, binary, 0, 255,
            ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

        var center = new Point2f(binary.Width / 2f, binary.Height / 2f);
        var bestAngle = 0.0;
        var bestVariance = 0.0;

        // Search for angle that maximizes projection variance
        for (var angle = -angleRange; angle <= angleRange; angle += angleStep)
        {
            var rotMat = Cv2.GetRotationMatrix2D(center, angle, 1.0);
            var rotated = new Mat();
            Cv2.WarpAffine(binary, rotated, rotMat,
                binary.Size(), InterpolationFlags.Nearest);

            // Calculate horizontal projection
            var projection = new Mat();
            Cv2.Reduce(rotated, projection, ReduceDimension.Column, ReduceTypes.Sum);

            Cv2.MeanStdDev(projection, out _, out var stddev);
            var variance = stddev.Val0 * stddev.Val0;

            if (variance > bestVariance)
            {
                bestVariance = variance;
                bestAngle = angle;
            }

            rotated.Dispose();
            projection.Dispose();
        }

        binary.Dispose();
        if (gray != image) gray.Dispose();

        // Apply best rotation
        var finalRotMat = Cv2.GetRotationMatrix2D(center, bestAngle, 1.0);
        var result = new Mat();
        Cv2.WarpAffine(image, result, finalRotMat,
            image.Size(), InterpolationFlags.Cubic,
            BorderTypes.Replicate);

        return (result, bestAngle);
    }
}
```

---

## 5. Noise Reduction

```csharp
using OpenCvSharp;

namespace DocSummarizer.Preprocessing;

/// <summary>
/// Noise reduction methods for document images.
/// </summary>
public class NoiseReducer
{
    public enum DenoiseMethod
    {
        Gaussian,
        Bilateral,
        NonLocalMeans,
        Morphological
    }

    public Mat Denoise(Mat gray, DenoiseMethod method)
    {
        return method switch
        {
            DenoiseMethod.Gaussian => GaussianDenoise(gray),
            DenoiseMethod.Bilateral => BilateralDenoise(gray),
            DenoiseMethod.NonLocalMeans => NlmDenoise(gray),
            DenoiseMethod.Morphological => MorphologicalDenoise(gray),
            _ => gray.Clone()
        };
    }

    /// <summary>
    /// Simple Gaussian blur for minor noise.
    /// </summary>
    private Mat GaussianDenoise(Mat gray, int kernelSize = 3)
    {
        var result = new Mat();
        Cv2.GaussianBlur(gray, result, new Size(kernelSize, kernelSize), 0);
        return result;
    }

    /// <summary>
    /// Bilateral filter - preserves edges while smoothing.
    /// </summary>
    private Mat BilateralDenoise(Mat gray, int d = 9,
        double sigmaColor = 75, double sigmaSpace = 75)
    {
        var result = new Mat();
        Cv2.BilateralFilter(gray, result, d, sigmaColor, sigmaSpace);
        return result;
    }

    /// <summary>
    /// Non-local means - best quality but slower.
    /// </summary>
    private Mat NlmDenoise(Mat gray, float h = 10,
        int templateWindowSize = 7, int searchWindowSize = 21)
    {
        var result = new Mat();
        Cv2.FastNlMeansDenoising(gray, result, h,
            templateWindowSize, searchWindowSize);
        return result;
    }

    /// <summary>
    /// Morphological denoising for binary images.
    /// </summary>
    private Mat MorphologicalDenoise(Mat binary, int noiseSize = 2)
    {
        var kernel = Cv2.GetStructuringElement(
            MorphShapes.Rect, new Size(noiseSize, noiseSize));

        var result = new Mat();
        // Opening removes small white noise
        Cv2.MorphologyEx(binary, result, MorphTypes.Open, kernel);
        // Closing fills small holes
        Cv2.MorphologyEx(result, result, MorphTypes.Close, kernel);

        return result;
    }
}
```

---

## 6. Complete Pipeline

### 6.1 Unified Preprocessor

```csharp
using OpenCvSharp;
using Microsoft.Extensions.Logging;

namespace DocSummarizer.Preprocessing;

/// <summary>
/// Unified OCR preprocessing pipeline with over-correction detection.
/// </summary>
public class OcrPreprocessor
{
    private readonly ImageQualityAssessor _assessor;
    private readonly SkewCorrector _skewCorrector;
    private readonly NoiseReducer _noiseReducer;
    private readonly InkExtractor _inkExtractor;
    private readonly OverCorrectionDetector _overCorrectionDetector;
    private readonly ILogger<OcrPreprocessor> _logger;
    private readonly OcrPreprocessorConfig _config;

    public record OcrPreprocessorConfig
    {
        public int TargetDpi { get; init; } = 300;
        public double BlurThreshold { get; init; } = 50;
        public double SkewThreshold { get; init; } = 2.0;
        public double NoiseThreshold { get; init; } = 15;
        public double ContrastThreshold { get; init; } = 0.3;
        public bool EnableOverCorrectionCheck { get; init; } = true;
    }

    public record PreprocessingResult
    {
        public Mat ProcessedImage { get; init; } = null!;
        public Mat BinaryImage { get; init; } = null!;
        public ImageQualityAssessor.QualityReport QualityBefore { get; init; } = null!;
        public ImageQualityAssessor.QualityReport QualityAfter { get; init; } = null!;
        public double SkewAngle { get; init; }
        public PreprocessingLevel Level { get; init; }
        public bool OverCorrectionDetected { get; init; }
        public double Confidence { get; init; }
    }

    public enum PreprocessingLevel
    {
        None,
        Light,
        Moderate,
        Aggressive
    }

    public OcrPreprocessor(
        OcrPreprocessorConfig config,
        ILogger<OcrPreprocessor> logger)
    {
        _config = config;
        _logger = logger;
        _assessor = new ImageQualityAssessor();
        _skewCorrector = new SkewCorrector();
        _noiseReducer = new NoiseReducer();
        _inkExtractor = new InkExtractor();
        _overCorrectionDetector = new OverCorrectionDetector();
    }

    /// <summary>
    /// Full preprocessing pipeline with adaptive enhancement.
    /// </summary>
    public PreprocessingResult Process(Mat image, int? currentDpi = null)
    {
        var original = image.Clone();

        // Step 1: Assess quality
        var qualityBefore = _assessor.Analyze(image);
        _logger.LogInformation(
            "Quality assessment: Blur={Blur:F1}, Skew={Skew:F1}, " +
            "Noise={Noise:F1}, Contrast={Contrast:F2}",
            qualityBefore.BlurScore, qualityBefore.SkewAngle,
            qualityBefore.NoiseLevel, qualityBefore.ContrastScore);

        // Step 2: Rescale if needed
        if (currentDpi.HasValue && currentDpi.Value < _config.TargetDpi)
        {
            var scale = (double)_config.TargetDpi / currentDpi.Value;
            Cv2.Resize(image, image, new Size(), scale, scale, InterpolationFlags.Cubic);
        }

        // Step 3: Convert to grayscale
        var gray = image.Channels() == 1
            ? image.Clone()
            : image.CvtColor(ColorConversionCodes.BGR2GRAY);

        // Step 4: Deskew if needed
        var skewAngle = 0.0;
        if (Math.Abs(qualityBefore.SkewAngle) > _config.SkewThreshold)
        {
            var deskewResult = _skewCorrector.Deskew(gray);
            gray.Dispose();
            gray = deskewResult.Image;
            skewAngle = deskewResult.Angle;
            _logger.LogInformation("Deskewed by {Angle:F2} degrees", skewAngle);
        }

        // Step 5: Determine preprocessing level
        var level = DetermineLevel(qualityBefore);
        _logger.LogInformation("Preprocessing level: {Level}", level);

        // Step 6: Apply preprocessing
        var processed = level switch
        {
            PreprocessingLevel.None => gray.Clone(),
            PreprocessingLevel.Light => ApplyLightPreprocessing(gray),
            PreprocessingLevel.Moderate => ApplyModeratePreprocessing(gray),
            PreprocessingLevel.Aggressive => ApplyAggressivePreprocessing(gray),
            _ => gray.Clone()
        };

        // Step 7: Binarize with over-correction check
        var (binary, overCorrected) = SafeBinarize(gray, processed, level);

        // Step 8: Final quality assessment
        var qualityAfter = _assessor.Analyze(processed);

        // Calculate confidence
        var confidence = CalculateConfidence(qualityBefore, qualityAfter, overCorrected);

        original.Dispose();

        return new PreprocessingResult
        {
            ProcessedImage = processed,
            BinaryImage = binary,
            QualityBefore = qualityBefore,
            QualityAfter = qualityAfter,
            SkewAngle = skewAngle,
            Level = level,
            OverCorrectionDetected = overCorrected,
            Confidence = confidence
        };
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
        var denoised = _noiseReducer.Denoise(gray, NoiseReducer.DenoiseMethod.Bilateral);

        // Light contrast enhancement
        var clahe = Cv2.CreateCLAHE(2.0, new Size(8, 8));
        var enhanced = new Mat();
        clahe.Apply(denoised, enhanced);
        denoised.Dispose();

        return enhanced;
    }

    private Mat ApplyAggressivePreprocessing(Mat gray)
    {
        // Strong denoising
        var denoised = _noiseReducer.Denoise(gray, NoiseReducer.DenoiseMethod.NonLocalMeans);

        // Background normalization
        var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(30, 30));
        var background = new Mat();
        Cv2.MorphologyEx(denoised, background, MorphTypes.Close, kernel);

        var normalized = new Mat();
        Cv2.Subtract(background, denoised, normalized);
        Cv2.Normalize(normalized, normalized, 0, 255, NormTypes.MinMax);

        // Strong contrast enhancement
        var clahe = Cv2.CreateCLAHE(3.0, new Size(8, 8));
        var enhanced = new Mat();
        clahe.Apply(normalized, enhanced);

        denoised.Dispose();
        background.Dispose();
        normalized.Dispose();

        return enhanced;
    }

    private (Mat binary, bool overCorrected) SafeBinarize(
        Mat originalGray, Mat processed, PreprocessingLevel level)
    {
        // Choose binarization method
        var method = level switch
        {
            PreprocessingLevel.None or PreprocessingLevel.Light
                => InkExtractor.BinarizationMethod.Otsu,
            PreprocessingLevel.Moderate
                => InkExtractor.BinarizationMethod.Adaptive,
            _ => InkExtractor.BinarizationMethod.Sauvola
        };

        var binary = _inkExtractor.Extract(processed, method);

        // Check for over-correction
        if (!_config.EnableOverCorrectionCheck)
            return (binary, false);

        var report = _overCorrectionDetector.Detect(originalGray, binary);

        if (report.IsOverCorrected)
        {
            _logger.LogWarning(
                "Over-correction detected: {Issues}. Falling back to gentler method.",
                string.Join(", ", report.Issues));

            binary.Dispose();

            // Fall back to gentler method
            var fallbackMethod = level == PreprocessingLevel.Aggressive
                ? InkExtractor.BinarizationMethod.Adaptive
                : InkExtractor.BinarizationMethod.Otsu;

            binary = _inkExtractor.Extract(processed, fallbackMethod);

            // Re-check
            report = _overCorrectionDetector.Detect(originalGray, binary);
        }

        return (binary, report.IsOverCorrected);
    }

    private double CalculateConfidence(
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
```

### 6.2 Service Registration

```csharp
namespace DocSummarizer.Preprocessing;

public static class PreprocessingServiceExtensions
{
    public static IServiceCollection AddOcrPreprocessing(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var config = configuration
            .GetSection("OcrPreprocessing")
            .Get<OcrPreprocessor.OcrPreprocessorConfig>()
            ?? new OcrPreprocessor.OcrPreprocessorConfig();

        services.AddSingleton(config);
        services.AddSingleton<ImageQualityAssessor>();
        services.AddSingleton<SkewCorrector>();
        services.AddSingleton<NoiseReducer>();
        services.AddSingleton<InkExtractor>();
        services.AddSingleton<OverCorrectionDetector>();
        services.AddSingleton<OcrPreprocessor>();

        return services;
    }
}
```

### 6.3 Configuration

```json
{
  "OcrPreprocessing": {
    "TargetDpi": 300,
    "BlurThreshold": 50,
    "SkewThreshold": 2.0,
    "NoiseThreshold": 15,
    "ContrastThreshold": 0.3,
    "EnableOverCorrectionCheck": true
  }
}
```

---

## 7. Integration as a Wave

```csharp
using Mostlylucid.Summarizer.Core;
using OpenCvSharp;

namespace Mostlylucid.DocSummarizer.Core.Waves;

/// <summary>
/// Wave that preprocesses images before OCR.
/// </summary>
public class OcrPreprocessingWave : WaveBase<OcrPreprocessingResult>
{
    private readonly OcrPreprocessor _preprocessor;
    private readonly ILogger<OcrPreprocessingWave> _logger;

    public override string Name => "OcrPreprocessing";
    public override int Order => 10; // Early in pipeline

    public OcrPreprocessingWave(
        OcrPreprocessor preprocessor,
        ILogger<OcrPreprocessingWave> logger)
    {
        _preprocessor = preprocessor;
        _logger = logger;
    }

    public override async Task<OcrPreprocessingResult> ProcessAsync(
        WaveContext context,
        CancellationToken ct = default)
    {
        var imagePath = context.Get<string>("imagePath");
        if (string.IsNullOrEmpty(imagePath))
        {
            return new OcrPreprocessingResult { Skipped = true };
        }

        using var image = Cv2.ImRead(imagePath);
        var result = _preprocessor.Process(image);

        _logger.LogInformation(
            "Preprocessing: Level={Level}, Skew={Skew:F1}, " +
            "OverCorrected={OverCorrected}, Confidence={Conf:P0}",
            result.Level, result.SkewAngle,
            result.OverCorrectionDetected, result.Confidence);

        // Save processed image for OCR
        var processedPath = Path.ChangeExtension(imagePath, ".processed.png");
        Cv2.ImWrite(processedPath, result.BinaryImage);

        context.Set("processedImagePath", processedPath);
        context.Set("preprocessingConfidence", result.Confidence);

        return new OcrPreprocessingResult
        {
            ProcessedImagePath = processedPath,
            SkewAngle = result.SkewAngle,
            Level = result.Level.ToString(),
            OverCorrectionDetected = result.OverCorrectionDetected,
            Confidence = result.Confidence
        };
    }
}

public record OcrPreprocessingResult
{
    public bool Skipped { get; init; }
    public string? ProcessedImagePath { get; init; }
    public double SkewAngle { get; init; }
    public string Level { get; init; } = "None";
    public bool OverCorrectionDetected { get; init; }
    public double Confidence { get; init; }
}
```

---

## 8. OCR Result Validation

Post-OCR validation is critical for assessing output quality. This section covers:
1. **Language Detection** - Identifying the language of extracted text
2. **Word Validation** - Detecting valid words vs. gibberish
3. **Technical Term Handling** - Preserving abbreviations, acronyms, and domain terms

### 8.1 Language Detection

#### Libraries for .NET

| Library | Accuracy | Speed | Languages | Memory |
|---------|----------|-------|-----------|--------|
| [Panlingo](https://github.com/gluschenko/panlingo) | High | Fast | 170+ | Low |
| [Lingua.NET](https://github.com/searchpioneer/lingua-dotnet) | Very High | Slow | 75 | High |
| [NLangDetect](https://github.com/marek-stoj/NLangDetect) | Good | Fast | 50+ | Low |
| FastText (via Panlingo) | Very High | Very Fast | 176 | ~1MB |

#### C# Implementation

```csharp
using Panlingo.LanguageIdentification.FastText;

namespace DocSummarizer.Validation;

/// <summary>
/// Detects language of OCR output using FastText.
/// </summary>
public class LanguageDetector : IDisposable
{
    private readonly FastTextDetector _detector;

    public record LanguageResult
    {
        public string Language { get; init; } = "unknown";
        public string LanguageCode { get; init; } = "und";
        public double Confidence { get; init; }
        public bool IsReliable => Confidence > 0.8;
    }

    public LanguageDetector()
    {
        _detector = new FastTextDetector();
        // Load the pre-trained model (lid.176.ftz - ~1MB)
        _detector.LoadModel("models/lid.176.ftz");
    }

    /// <summary>
    /// Detect language of text.
    /// </summary>
    public LanguageResult Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 10)
        {
            return new LanguageResult
            {
                Language = "unknown",
                LanguageCode = "und",
                Confidence = 0
            };
        }

        var predictions = _detector.Predict(text, count: 1);
        var top = predictions.FirstOrDefault();

        if (top == null)
        {
            return new LanguageResult
            {
                Language = "unknown",
                LanguageCode = "und",
                Confidence = 0
            };
        }

        return new LanguageResult
        {
            Language = GetLanguageName(top.Label),
            LanguageCode = top.Label.Replace("__label__", ""),
            Confidence = top.Probability
        };
    }

    /// <summary>
    /// Detect multiple possible languages (for mixed-language documents).
    /// </summary>
    public IEnumerable<LanguageResult> DetectMultiple(string text, int maxResults = 3)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        var predictions = _detector.Predict(text, count: maxResults);

        foreach (var pred in predictions)
        {
            yield return new LanguageResult
            {
                Language = GetLanguageName(pred.Label),
                LanguageCode = pred.Label.Replace("__label__", ""),
                Confidence = pred.Probability
            };
        }
    }

    private string GetLanguageName(string label)
    {
        var code = label.Replace("__label__", "");
        return code switch
        {
            "en" => "English",
            "de" => "German",
            "fr" => "French",
            "es" => "Spanish",
            "it" => "Italian",
            "pt" => "Portuguese",
            "nl" => "Dutch",
            "pl" => "Polish",
            "ru" => "Russian",
            "ja" => "Japanese",
            "zh" => "Chinese",
            "ko" => "Korean",
            "ar" => "Arabic",
            _ => code.ToUpperInvariant()
        };
    }

    public void Dispose()
    {
        _detector?.Dispose();
    }
}
```

### 8.2 Word Validation with SymSpell

[SymSpell](https://github.com/wolfgarbe/SymSpell) is 1 million times faster than traditional spell checkers.

```csharp
using SymSpell;

namespace DocSummarizer.Validation;

/// <summary>
/// Validates OCR words using SymSpell dictionary lookup.
/// </summary>
public class WordValidator
{
    private readonly SymSpell.SymSpell _symSpell;
    private readonly HashSet<string> _technicalTerms;
    private readonly HashSet<string> _abbreviations;

    public record WordValidationResult
    {
        public string Original { get; init; } = "";
        public bool IsValid { get; init; }
        public bool IsTechnicalTerm { get; init; }
        public bool IsAbbreviation { get; init; }
        public string? Suggestion { get; init; }
        public int EditDistance { get; init; }
        public double Confidence { get; init; }
    }

    public record TextValidationReport
    {
        public int TotalWords { get; init; }
        public int ValidWords { get; init; }
        public int InvalidWords { get; init; }
        public int TechnicalTerms { get; init; }
        public int Abbreviations { get; init; }
        public double ValidWordRatio { get; init; }
        public double QualityScore { get; init; }
        public List<WordValidationResult> InvalidWordDetails { get; init; } = new();
    }

    public WordValidator(
        string dictionaryPath,
        string? technicalTermsPath = null,
        string? abbreviationsPath = null)
    {
        // Initialize SymSpell with edit distance 2
        _symSpell = new SymSpell.SymSpell(maxDictionaryEditDistance: 2);

        // Load main dictionary (frequency dictionary for better suggestions)
        _symSpell.LoadDictionary(dictionaryPath,
            termIndex: 0, countIndex: 1,
            separatorChars: ' ');

        // Load technical terms (domain-specific vocabulary)
        _technicalTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(technicalTermsPath) && File.Exists(technicalTermsPath))
        {
            foreach (var line in File.ReadLines(technicalTermsPath))
            {
                var term = line.Trim();
                if (!string.IsNullOrEmpty(term))
                    _technicalTerms.Add(term);
            }
        }

        // Load abbreviations
        _abbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(abbreviationsPath) && File.Exists(abbreviationsPath))
        {
            foreach (var line in File.ReadLines(abbreviationsPath))
            {
                var abbr = line.Trim();
                if (!string.IsNullOrEmpty(abbr))
                    _abbreviations.Add(abbr);
            }
        }

        // Add common technical abbreviations
        AddCommonAbbreviations();
    }

    /// <summary>
    /// Validate a single word.
    /// </summary>
    public WordValidationResult ValidateWord(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return new WordValidationResult
            {
                Original = word,
                IsValid = false,
                Confidence = 0
            };
        }

        // Clean the word
        var cleanWord = CleanWord(word);
        if (string.IsNullOrEmpty(cleanWord))
        {
            return new WordValidationResult
            {
                Original = word,
                IsValid = true, // Punctuation only
                Confidence = 1.0
            };
        }

        // Check if it's a number
        if (IsNumeric(cleanWord))
        {
            return new WordValidationResult
            {
                Original = word,
                IsValid = true,
                Confidence = 1.0
            };
        }

        // Check abbreviations first (case-sensitive for acronyms)
        if (_abbreviations.Contains(cleanWord) ||
            _abbreviations.Contains(cleanWord.ToUpperInvariant()))
        {
            return new WordValidationResult
            {
                Original = word,
                IsValid = true,
                IsAbbreviation = true,
                Confidence = 1.0
            };
        }

        // Check technical terms
        if (_technicalTerms.Contains(cleanWord))
        {
            return new WordValidationResult
            {
                Original = word,
                IsValid = true,
                IsTechnicalTerm = true,
                Confidence = 1.0
            };
        }

        // SymSpell lookup
        var suggestions = _symSpell.Lookup(cleanWord.ToLowerInvariant(),
            SymSpell.SymSpell.Verbosity.Closest, maxEditDistance: 2);

        if (suggestions.Count == 0)
        {
            // No suggestions - likely gibberish or very unusual word
            return new WordValidationResult
            {
                Original = word,
                IsValid = false,
                Confidence = 0
            };
        }

        var best = suggestions[0];

        // Exact match
        if (best.distance == 0)
        {
            return new WordValidationResult
            {
                Original = word,
                IsValid = true,
                Confidence = 1.0
            };
        }

        // Close match (edit distance 1-2)
        return new WordValidationResult
        {
            Original = word,
            IsValid = false,
            Suggestion = best.term,
            EditDistance = best.distance,
            Confidence = 1.0 - (best.distance * 0.3) // 0.7 for dist=1, 0.4 for dist=2
        };
    }

    /// <summary>
    /// Validate entire text and generate quality report.
    /// </summary>
    public TextValidationReport ValidateText(string text)
    {
        var words = TokenizeWords(text);
        var results = words.Select(ValidateWord).ToList();

        var validCount = results.Count(r => r.IsValid);
        var invalidCount = results.Count(r => !r.IsValid);
        var technicalCount = results.Count(r => r.IsTechnicalTerm);
        var abbreviationCount = results.Count(r => r.IsAbbreviation);

        var validRatio = results.Count > 0
            ? (double)validCount / results.Count
            : 0;

        // Quality score considers:
        // - Valid word ratio (primary)
        // - Presence of technical terms (bonus)
        // - Low invalid word count (bonus)
        var qualityScore = validRatio;
        if (technicalCount > 0 && validRatio > 0.7)
            qualityScore = Math.Min(1.0, qualityScore + 0.05);

        return new TextValidationReport
        {
            TotalWords = results.Count,
            ValidWords = validCount,
            InvalidWords = invalidCount,
            TechnicalTerms = technicalCount,
            Abbreviations = abbreviationCount,
            ValidWordRatio = validRatio,
            QualityScore = qualityScore,
            InvalidWordDetails = results.Where(r => !r.IsValid).ToList()
        };
    }

    private string CleanWord(string word)
    {
        // Remove leading/trailing punctuation
        return word.Trim(
            '.', ',', '!', '?', ';', ':', '"', '\'',
            '(', ')', '[', ']', '{', '}', '-', '_'
        );
    }

    private bool IsNumeric(string word)
    {
        // Check for numbers, decimals, percentages, currencies
        return word.All(c =>
            char.IsDigit(c) || c == '.' || c == ',' ||
            c == '%' || c == '$' || c == '€' || c == '£'
        );
    }

    private IEnumerable<string> TokenizeWords(string text)
    {
        // Simple whitespace tokenization
        return text.Split(
            new[] { ' ', '\t', '\n', '\r' },
            StringSplitOptions.RemoveEmptyEntries
        );
    }

    private void AddCommonAbbreviations()
    {
        // Technical abbreviations
        var common = new[]
        {
            // Computing
            "API", "SDK", "CLI", "GUI", "UI", "UX", "HTTP", "HTTPS", "HTML",
            "CSS", "JSON", "XML", "SQL", "NoSQL", "REST", "GraphQL", "TCP",
            "UDP", "IP", "DNS", "SSL", "TLS", "SSH", "FTP", "SMTP", "IMAP",
            "CPU", "GPU", "RAM", "ROM", "SSD", "HDD", "BIOS", "UEFI", "OS",
            "VM", "AWS", "GCP", "Azure", "Docker", "K8s", "CI", "CD", "DevOps",

            // File formats
            "PDF", "DOCX", "XLSX", "PPTX", "PNG", "JPG", "JPEG", "GIF", "SVG",
            "MP3", "MP4", "WAV", "AVI", "MOV", "ZIP", "RAR", "TAR", "GZ",

            // Business
            "CEO", "CTO", "CFO", "COO", "HR", "PR", "ROI", "KPI", "B2B", "B2C",
            "SaaS", "PaaS", "IaaS", "CRM", "ERP", "MVP", "POC", "NDA", "SLA",

            // General
            "etc", "eg", "ie", "vs", "Inc", "Ltd", "LLC", "Corp", "Dept",
            "Mr", "Mrs", "Ms", "Dr", "Prof", "Jr", "Sr", "St", "Ave", "Blvd",

            // Units
            "kg", "km", "cm", "mm", "ml", "MHz", "GHz", "TB", "GB", "MB", "KB",

            // Science
            "DNA", "RNA", "HIV", "AIDS", "MRI", "CT", "ECG", "EKG", "pH",
            "AI", "ML", "DL", "NLP", "OCR", "NER", "LLM", "GPT", "BERT"
        };

        foreach (var abbr in common)
        {
            _abbreviations.Add(abbr);
            _abbreviations.Add(abbr.ToLowerInvariant());
        }
    }
}
```

### 8.3 Gibberish Detection

Detect OCR garbage using character n-gram analysis and entropy.

```csharp
namespace DocSummarizer.Validation;

/// <summary>
/// Detects gibberish/garbage text using statistical analysis.
/// </summary>
public class GibberishDetector
{
    private readonly Dictionary<string, double> _bigramFrequencies;
    private readonly double _entropyThreshold;
    private readonly double _bigramThreshold;

    public record GibberishReport
    {
        public bool IsGibberish { get; init; }
        public double GibberishScore { get; init; } // 0 = valid, 1 = gibberish
        public double Entropy { get; init; }
        public double BigramScore { get; init; }
        public double ConsonantRatio { get; init; }
        public string[] Reasons { get; init; } = Array.Empty<string>();
    }

    public GibberishDetector(
        string? bigramModelPath = null,
        double entropyThreshold = 4.5,
        double bigramThreshold = 0.3)
    {
        _entropyThreshold = entropyThreshold;
        _bigramThreshold = bigramThreshold;

        // Load or build bigram frequency model
        _bigramFrequencies = bigramModelPath != null && File.Exists(bigramModelPath)
            ? LoadBigramModel(bigramModelPath)
            : BuildDefaultBigramModel();
    }

    /// <summary>
    /// Analyze text for gibberish characteristics.
    /// </summary>
    public GibberishReport Analyze(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new GibberishReport
            {
                IsGibberish = true,
                GibberishScore = 1.0,
                Reasons = new[] { "Empty text" }
            };
        }

        var cleanText = text.ToLowerInvariant();
        var reasons = new List<string>();

        // 1. Shannon entropy
        var entropy = CalculateEntropy(cleanText);
        if (entropy > _entropyThreshold)
            reasons.Add($"High entropy ({entropy:F2} > {_entropyThreshold})");

        // 2. Bigram score
        var bigramScore = CalculateBigramScore(cleanText);
        if (bigramScore < _bigramThreshold)
            reasons.Add($"Low bigram score ({bigramScore:F2} < {_bigramThreshold})");

        // 3. Consonant clustering
        var consonantRatio = CalculateConsonantClusterRatio(cleanText);
        if (consonantRatio > 0.6)
            reasons.Add($"Excessive consonant clusters ({consonantRatio:F2})");

        // 4. Repeated characters
        if (HasExcessiveRepeats(cleanText))
            reasons.Add("Excessive character repetition");

        // 5. Invalid character patterns
        if (HasInvalidPatterns(cleanText))
            reasons.Add("Invalid character patterns");

        // Composite score
        var gibberishScore = CalculateCompositeScore(
            entropy, bigramScore, consonantRatio, reasons.Count);

        return new GibberishReport
        {
            IsGibberish = gibberishScore > 0.5,
            GibberishScore = gibberishScore,
            Entropy = entropy,
            BigramScore = bigramScore,
            ConsonantRatio = consonantRatio,
            Reasons = reasons.ToArray()
        };
    }

    /// <summary>
    /// Calculate Shannon entropy of text.
    /// Normal English text: ~4.0-4.5 bits/char
    /// Random/gibberish: ~5.0+ bits/char
    /// </summary>
    private double CalculateEntropy(string text)
    {
        var frequencies = new Dictionary<char, int>();
        var total = 0;

        foreach (var c in text.Where(char.IsLetterOrDigit))
        {
            frequencies.TryGetValue(c, out var count);
            frequencies[c] = count + 1;
            total++;
        }

        if (total == 0)
            return 0;

        var entropy = 0.0;
        foreach (var count in frequencies.Values)
        {
            var probability = (double)count / total;
            entropy -= probability * Math.Log2(probability);
        }

        return entropy;
    }

    /// <summary>
    /// Calculate bigram validity score.
    /// Higher = more valid English-like text.
    /// </summary>
    private double CalculateBigramScore(string text)
    {
        var letters = new string(text.Where(char.IsLetter).ToArray());
        if (letters.Length < 2)
            return 0;

        var validBigrams = 0;
        var totalBigrams = 0;

        for (int i = 0; i < letters.Length - 1; i++)
        {
            var bigram = letters.Substring(i, 2);
            totalBigrams++;

            if (_bigramFrequencies.ContainsKey(bigram))
                validBigrams++;
        }

        return totalBigrams > 0 ? (double)validBigrams / totalBigrams : 0;
    }

    /// <summary>
    /// Calculate ratio of consonant clusters (3+ consecutive consonants).
    /// </summary>
    private double CalculateConsonantClusterRatio(string text)
    {
        var vowels = new HashSet<char> { 'a', 'e', 'i', 'o', 'u' };
        var letters = text.Where(char.IsLetter).ToArray();

        if (letters.Length < 3)
            return 0;

        var clusterChars = 0;
        var consonantRun = 0;

        foreach (var c in letters)
        {
            if (!vowels.Contains(c))
            {
                consonantRun++;
            }
            else
            {
                if (consonantRun >= 3)
                    clusterChars += consonantRun;
                consonantRun = 0;
            }
        }

        // Check final run
        if (consonantRun >= 3)
            clusterChars += consonantRun;

        return (double)clusterChars / letters.Length;
    }

    /// <summary>
    /// Check for excessive character repetition (e.g., "aaaaa", "!!!!!").
    /// </summary>
    private bool HasExcessiveRepeats(string text)
    {
        var maxRepeat = 0;
        var currentRepeat = 1;
        char? lastChar = null;

        foreach (var c in text)
        {
            if (c == lastChar)
            {
                currentRepeat++;
                maxRepeat = Math.Max(maxRepeat, currentRepeat);
            }
            else
            {
                currentRepeat = 1;
            }
            lastChar = c;
        }

        return maxRepeat > 4;
    }

    /// <summary>
    /// Check for patterns that indicate OCR errors.
    /// </summary>
    private bool HasInvalidPatterns(string text)
    {
        // Common OCR error patterns
        var invalidPatterns = new[]
        {
            "qqqq", "xxxx", "zzzz",  // Repeated rare letters
            "rnrn", "lili", "1l1l",  // Confused characters
            "0o0o", "oO0o",          // Zero/O confusion
        };

        return invalidPatterns.Any(p =>
            text.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    private double CalculateCompositeScore(
        double entropy, double bigramScore, double consonantRatio, int reasonCount)
    {
        // Weighted combination
        var score = 0.0;

        // Entropy contribution (normalized to 0-1)
        var entropyScore = Math.Min(1.0, Math.Max(0, (entropy - 3.5) / 2.0));
        score += entropyScore * 0.3;

        // Bigram contribution (inverted - low bigram = high gibberish)
        score += (1.0 - bigramScore) * 0.4;

        // Consonant cluster contribution
        score += Math.Min(1.0, consonantRatio * 1.5) * 0.2;

        // Reason count contribution
        score += Math.Min(1.0, reasonCount * 0.2) * 0.1;

        return Math.Min(1.0, score);
    }

    private Dictionary<string, double> BuildDefaultBigramModel()
    {
        // Most common English bigrams with approximate frequencies
        return new Dictionary<string, double>
        {
            {"th", 0.0356}, {"he", 0.0307}, {"in", 0.0243}, {"er", 0.0205},
            {"an", 0.0199}, {"re", 0.0185}, {"on", 0.0176}, {"at", 0.0149},
            {"en", 0.0145}, {"nd", 0.0135}, {"ti", 0.0134}, {"es", 0.0134},
            {"or", 0.0128}, {"te", 0.0120}, {"of", 0.0117}, {"ed", 0.0117},
            {"is", 0.0113}, {"it", 0.0112}, {"al", 0.0109}, {"ar", 0.0107},
            {"st", 0.0105}, {"to", 0.0104}, {"nt", 0.0104}, {"ng", 0.0095},
            {"se", 0.0093}, {"ha", 0.0093}, {"as", 0.0087}, {"ou", 0.0087},
            {"io", 0.0083}, {"le", 0.0083}, {"ve", 0.0083}, {"co", 0.0079},
            {"me", 0.0079}, {"de", 0.0076}, {"hi", 0.0076}, {"ri", 0.0073},
            {"ro", 0.0073}, {"ic", 0.0070}, {"ne", 0.0069}, {"ea", 0.0069},
            {"ra", 0.0069}, {"ce", 0.0065}, {"li", 0.0062}, {"ch", 0.0060},
            {"ll", 0.0058}, {"be", 0.0058}, {"ma", 0.0057}, {"si", 0.0055},
            {"om", 0.0055}, {"ur", 0.0054}
        };
    }

    private Dictionary<string, double> LoadBigramModel(string path)
    {
        var model = new Dictionary<string, double>();
        foreach (var line in File.ReadLines(path))
        {
            var parts = line.Split('\t', ' ');
            if (parts.Length >= 2 && double.TryParse(parts[1], out var freq))
            {
                model[parts[0].ToLowerInvariant()] = freq;
            }
        }
        return model;
    }
}
```

### 8.4 Unified OCR Quality Validator

```csharp
namespace DocSummarizer.Validation;

/// <summary>
/// Unified OCR output quality validation.
/// </summary>
public class OcrQualityValidator
{
    private readonly LanguageDetector _languageDetector;
    private readonly WordValidator _wordValidator;
    private readonly GibberishDetector _gibberishDetector;
    private readonly ILogger<OcrQualityValidator> _logger;

    public record OcrQualityReport
    {
        public string DetectedLanguage { get; init; } = "unknown";
        public double LanguageConfidence { get; init; }
        public double ValidWordRatio { get; init; }
        public double GibberishScore { get; init; }
        public double OverallQuality { get; init; }
        public bool IsAcceptable { get; init; }
        public string[] Issues { get; init; } = Array.Empty<string>();
        public WordValidator.TextValidationReport? WordReport { get; init; }
        public GibberishDetector.GibberishReport? GibberishReport { get; init; }
    }

    public OcrQualityValidator(
        string dictionaryPath,
        string? technicalTermsPath = null,
        ILogger<OcrQualityValidator>? logger = null)
    {
        _languageDetector = new LanguageDetector();
        _wordValidator = new WordValidator(dictionaryPath, technicalTermsPath);
        _gibberishDetector = new GibberishDetector();
        _logger = logger ?? NullLogger<OcrQualityValidator>.Instance;
    }

    /// <summary>
    /// Validate OCR output quality.
    /// </summary>
    public OcrQualityReport Validate(string ocrText)
    {
        var issues = new List<string>();

        // 1. Language detection
        var langResult = _languageDetector.Detect(ocrText);
        if (!langResult.IsReliable)
        {
            issues.Add($"Low language confidence ({langResult.Confidence:P0})");
        }

        // 2. Word validation
        var wordReport = _wordValidator.ValidateText(ocrText);
        if (wordReport.ValidWordRatio < 0.7)
        {
            issues.Add($"Low valid word ratio ({wordReport.ValidWordRatio:P0})");
        }

        // 3. Gibberish detection
        var gibberishReport = _gibberishDetector.Analyze(ocrText);
        if (gibberishReport.IsGibberish)
        {
            issues.Add($"Text appears to be gibberish (score: {gibberishReport.GibberishScore:F2})");
            issues.AddRange(gibberishReport.Reasons);
        }

        // Calculate overall quality
        var overallQuality = CalculateOverallQuality(
            langResult.Confidence,
            wordReport.ValidWordRatio,
            gibberishReport.GibberishScore
        );

        var isAcceptable = overallQuality > 0.6 && !gibberishReport.IsGibberish;

        _logger.LogInformation(
            "OCR Quality: Lang={Lang} ({Conf:P0}), Words={Words:P0}, " +
            "Gibberish={Gib:F2}, Overall={Overall:P0}, Acceptable={Ok}",
            langResult.Language, langResult.Confidence,
            wordReport.ValidWordRatio, gibberishReport.GibberishScore,
            overallQuality, isAcceptable);

        return new OcrQualityReport
        {
            DetectedLanguage = langResult.Language,
            LanguageConfidence = langResult.Confidence,
            ValidWordRatio = wordReport.ValidWordRatio,
            GibberishScore = gibberishReport.GibberishScore,
            OverallQuality = overallQuality,
            IsAcceptable = isAcceptable,
            Issues = issues.ToArray(),
            WordReport = wordReport,
            GibberishReport = gibberishReport
        };
    }

    private double CalculateOverallQuality(
        double languageConfidence,
        double validWordRatio,
        double gibberishScore)
    {
        // Weighted combination
        var quality =
            languageConfidence * 0.2 +
            validWordRatio * 0.5 +
            (1.0 - gibberishScore) * 0.3;

        return Math.Clamp(quality, 0, 1);
    }
}
```

### 8.5 Integration as a Wave

```csharp
using Mostlylucid.Summarizer.Core;

namespace Mostlylucid.DocSummarizer.Core.Waves;

/// <summary>
/// Wave that validates OCR output quality.
/// </summary>
public class OcrValidationWave : WaveBase<OcrValidationResult>
{
    private readonly OcrQualityValidator _validator;
    private readonly ILogger<OcrValidationWave> _logger;

    public override string Name => "OcrValidation";
    public override int Order => 25; // After OCR extraction

    public OcrValidationWave(
        OcrQualityValidator validator,
        ILogger<OcrValidationWave> logger)
    {
        _validator = validator;
        _logger = logger;
    }

    public override async Task<OcrValidationResult> ProcessAsync(
        WaveContext context,
        CancellationToken ct = default)
    {
        var ocrText = context.Get<string>("ocrText");
        if (string.IsNullOrWhiteSpace(ocrText))
        {
            return new OcrValidationResult { Skipped = true };
        }

        var report = _validator.Validate(ocrText);

        // Store results in context for downstream waves
        context.Set("ocrQuality", report.OverallQuality);
        context.Set("detectedLanguage", report.DetectedLanguage);
        context.Set("isOcrAcceptable", report.IsAcceptable);

        if (!report.IsAcceptable)
        {
            _logger.LogWarning(
                "OCR quality below threshold: {Quality:P0}. Issues: {Issues}",
                report.OverallQuality, string.Join(", ", report.Issues));
        }

        return new OcrValidationResult
        {
            DetectedLanguage = report.DetectedLanguage,
            LanguageConfidence = report.LanguageConfidence,
            ValidWordRatio = report.ValidWordRatio,
            GibberishScore = report.GibberishScore,
            OverallQuality = report.OverallQuality,
            IsAcceptable = report.IsAcceptable,
            Issues = report.Issues
        };
    }
}

public record OcrValidationResult
{
    public bool Skipped { get; init; }
    public string DetectedLanguage { get; init; } = "unknown";
    public double LanguageConfidence { get; init; }
    public double ValidWordRatio { get; init; }
    public double GibberishScore { get; init; }
    public double OverallQuality { get; init; }
    public bool IsAcceptable { get; init; }
    public string[] Issues { get; init; } = Array.Empty<string>();
}
```

### 8.6 Configuration

```json
{
  "OcrValidation": {
    "DictionaryPath": "dictionaries/en_frequency.txt",
    "TechnicalTermsPath": "dictionaries/technical_terms.txt",
    "AbbreviationsPath": "dictionaries/abbreviations.txt",
    "MinValidWordRatio": 0.7,
    "MaxGibberishScore": 0.5,
    "MinOverallQuality": 0.6
  }
}
```

### 8.7 NuGet Packages Required

```xml
<ItemGroup>
  <!-- Language Detection -->
  <PackageReference Include="Panlingo.LanguageIdentification.FastText" Version="*" />

  <!-- Spelling/Word Validation -->
  <PackageReference Include="SymSpell" Version="6.7.3" />

  <!-- OR alternative: Lingua for high-accuracy language detection -->
  <PackageReference Include="Lingua" Version="*" />
</ItemGroup>
```

---

## 9. References

### Preprocessing Techniques
- [PyImageSearch: Text Skew Correction](https://pyimagesearch.com/2017/02/20/text-skew-correction-opencv-python/)
- [Skew Detection with Hough Transform](https://muthu.co/skew-detection-and-correction-of-document-images-using-hough-transform/)
- [Image Preprocessing for OCR](https://nextgeninvent.com/blogs/7-steps-of-image-pre-processing-to-improve-ocr-using-python-2/)

### Binarization
- [Document Binarization Review (MDPI)](https://www.mdpi.com/2079-9292/13/7/1394)
- [Degraded Document Binarization (PMC)](https://pmc.ncbi.nlm.nih.gov/articles/PMC8320943/)

### Deep Learning
- [PreP-OCR Pipeline (arXiv)](https://arxiv.org/html/2505.20429v1)
- [Deep Learning Skew Correction](https://jisem-journal.com/index.php/journal/article/view/4090)

### Tools
- [OpenCvSharp](https://github.com/shimat/opencvsharp) - .NET wrapper for OpenCV
- [Tesseract.NET](https://github.com/charlesw/tesseract) - .NET wrapper for Tesseract

### Language Detection
- [Panlingo](https://github.com/gluschenko/panlingo) - Multi-library .NET wrapper (FastText, CLD2, CLD3)
- [Lingua.NET](https://github.com/searchpioneer/lingua-dotnet) - High-accuracy language detection
- [FastText Language ID](https://fasttext.cc/blog/2017/10/02/blog-post.html) - 176 languages, 1MB model

### Word Validation & Spelling
- [SymSpell](https://github.com/wolfgarbe/SymSpell) - 1M times faster spelling correction
- [Gibberish Detector](https://github.com/rrenaud/Gibberish-Detector) - Markov chain approach
- [OCR Quality Assessment](https://arxiv.org/html/2510.21774) - Human-annotated dataset

### OCR Error Correction
- [OCR Post-Processing (arXiv)](https://arxiv.org/pdf/1204.0191) - Error correction algorithms
- [LEADTOOLS Spell Check](https://www.leadtools.com/help/sdk/dh/to/ocr-languages-and-spell-checking.html) - Commercial reference

---

## 10. Implementation Checklist

### Phase 1: Image Preprocessing
- [ ] Add OpenCvSharp4 NuGet package
- [ ] Implement ImageQualityAssessor
- [ ] Implement SkewCorrector (3 methods)
- [ ] Implement NoiseReducer (4 methods)
- [ ] Implement InkExtractor (5 methods)
- [ ] Implement OverCorrectionDetector
- [ ] Create OcrPreprocessor pipeline
- [ ] Create OcrPreprocessingWave

### Phase 2: OCR Result Validation
- [ ] Add Panlingo.LanguageIdentification.FastText NuGet
- [ ] Add SymSpell NuGet package
- [ ] Download FastText language model (lid.176.ftz)
- [ ] Download frequency dictionary (en_frequency.txt)
- [ ] Implement LanguageDetector
- [ ] Implement WordValidator with technical term handling
- [ ] Implement GibberishDetector
- [ ] Create OcrQualityValidator
- [ ] Create OcrValidationWave

### Phase 3: Integration
- [ ] Add configuration sections
- [ ] Wire up preprocessing and validation waves
- [ ] Add metrics/logging for quality tracking
- [ ] Write integration tests
