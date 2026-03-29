using OpenCvSharp;

namespace Mostlylucid.DocSummarizer.Images.Services.Preprocessing;

/// <summary>
///     Ink extraction (binarization) with multiple methods.
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
    ///     Extract ink using the specified method.
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
    ///     Simple Otsu binarization - best for clean scans.
    /// </summary>
    private static Mat OtsuBinarize(Mat gray)
    {
        var binary = new Mat();
        Cv2.Threshold(gray, binary, 0, 255,
            ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        return binary;
    }

    /// <summary>
    ///     Adaptive thresholding - best for uneven illumination.
    /// </summary>
    private static Mat AdaptiveBinarize(Mat gray, int blockSize = 31, double c = 10)
    {
        var binary = new Mat();
        Cv2.AdaptiveThreshold(gray, binary, 255,
            AdaptiveThresholdTypes.GaussianC,
            ThresholdTypes.BinaryInv,
            blockSize, c);
        return binary;
    }

    /// <summary>
    ///     Sauvola binarization - best for historical/degraded documents.
    ///     T(x,y) = mean(x,y) * (1 + k * (std(x,y) / r - 1))
    /// </summary>
    private static Mat SauvolaBinarize(Mat gray, int windowSize = 25,
        double k = 0.2, double r = 128)
    {
        var binary = new Mat(gray.Size(), MatType.CV_8UC1);

        using var mean = new Mat();
        Cv2.Blur(gray, mean, new Size(windowSize, windowSize));

        using var graySq = new Mat();
        Cv2.Multiply(gray, gray, graySq, 1.0 / 255.0);

        using var meanSq = new Mat();
        Cv2.Blur(graySq, meanSq, new Size(windowSize, windowSize));

        using var meanF = new Mat();
        using var meanSqF = new Mat();
        mean.ConvertTo(meanF, MatType.CV_32F);
        meanSq.ConvertTo(meanSqF, MatType.CV_32F);

        using var variance = new Mat();
        Cv2.Multiply(meanF, meanF, variance, 1.0 / 255.0);
        Cv2.Subtract(meanSqF, variance, variance);
        Cv2.Max(variance, 0, variance);

        using var std = new Mat();
        Cv2.Sqrt(variance, std);

        using var threshold = new Mat();
        Cv2.Divide(std, r, threshold);
        Cv2.Subtract(threshold, 1, threshold);
        Cv2.Multiply(threshold, k, threshold);
        Cv2.Add(threshold, 1, threshold);
        Cv2.Multiply(meanF, threshold, threshold);

        using var grayF = new Mat();
        gray.ConvertTo(grayF, MatType.CV_32F);

        Cv2.Compare(grayF, threshold, binary, CmpTypes.LT);

        return binary;
    }

    /// <summary>
    ///     CLAHE + Otsu - best for low contrast documents.
    /// </summary>
    private static Mat ClaheOtsuBinarize(Mat gray, double clipLimit = 2.0)
    {
        using var clahe = Cv2.CreateCLAHE(clipLimit, new Size(8, 8));
        using var enhanced = new Mat();
        clahe.Apply(gray, enhanced);
        return OtsuBinarize(enhanced);
    }

    /// <summary>
    ///     Morphological background removal - best for complex backgrounds.
    /// </summary>
    private static Mat MorphologicalBinarize(Mat gray, int kernelSize = 15)
    {
        using var kernel = Cv2.GetStructuringElement(
            MorphShapes.Ellipse, new Size(kernelSize, kernelSize));

        using var background = new Mat();
        Cv2.MorphologyEx(gray, background, MorphTypes.Close, kernel);

        using var foreground = new Mat();
        Cv2.Subtract(background, gray, foreground);

        Cv2.Normalize(foreground, foreground, 0, 255, NormTypes.MinMax);
        return OtsuBinarize(foreground);
    }
}