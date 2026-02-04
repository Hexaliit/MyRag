using OpenCvSharp;

namespace Mostlylucid.DocSummarizer.Images.Services.Preprocessing;

/// <summary>
///     Assesses document image quality to determine preprocessing needs.
/// </summary>
public class ImageQualityAssessor
{
    /// <summary>
    ///     Analyze image quality and return recommendations.
    /// </summary>
    public QualityReport Analyze(Mat image)
    {
        using var gray = image.Channels() == 1
            ? image.Clone()
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

        return new QualityReport
        {
            BlurScore = blur,
            SkewAngle = skew,
            NoiseLevel = noise,
            ContrastScore = contrast,
            BrightnessUniformity = uniformity,
            TextDensity = density,
            NeedsPreprocessing = recommendations.Count > 0,
            Recommendations = [.. recommendations]
        };
    }

    /// <summary>
    ///     Estimate blur using Laplacian variance.
    ///     Higher = sharper, Lower = blurrier.
    /// </summary>
    private static double EstimateBlur(Mat gray)
    {
        using var laplacian = new Mat();
        Cv2.Laplacian(gray, laplacian, MatType.CV_64F);
        Cv2.MeanStdDev(laplacian, out _, out var stddev);
        return stddev.Val0 * stddev.Val0;
    }

    /// <summary>
    ///     Estimate skew angle using Hough lines.
    /// </summary>
    private static double EstimateSkew(Mat gray)
    {
        using var edges = new Mat();
        Cv2.Canny(gray, edges, 50, 150);

        var lines = Cv2.HoughLinesP(edges, 1, Math.PI / 180, 100,
            100, 10);

        if (lines.Length == 0)
            return 0.0;

        var angles = new List<double>();
        foreach (var line in lines)
        {
            var angle = Math.Atan2(
                line.P2.Y - line.P1.Y,
                line.P2.X - line.P1.X
            ) * 180 / Math.PI;

            if (Math.Abs(angle) < 45)
                angles.Add(angle);
        }

        if (angles.Count == 0)
            return 0.0;

        angles.Sort();
        return angles[angles.Count / 2];
    }

    /// <summary>
    ///     Estimate noise using median absolute deviation.
    /// </summary>
    private static double EstimateNoise(Mat gray)
    {
        using var blur = new Mat();
        Cv2.GaussianBlur(gray, blur, new Size(5, 5), 0);

        using var noise = new Mat();
        Cv2.Subtract(gray, blur, noise);

        noise.GetArray(out byte[] noiseArray);
        Array.Sort(noiseArray);

        var median = noiseArray[noiseArray.Length / 2];
        var deviations = noiseArray.Select(x => Math.Abs(x - median)).OrderBy(x => x).ToArray();
        return deviations[deviations.Length / 2];
    }

    /// <summary>
    ///     Estimate contrast using Michelson contrast.
    /// </summary>
    private static double EstimateContrast(Mat gray)
    {
        gray.GetArray(out byte[] data);
        Array.Sort(data);

        var minVal = data[(int)(data.Length * 0.05)];
        var maxVal = data[(int)(data.Length * 0.95)];

        if (maxVal + minVal == 0)
            return 0.0;

        return (double)(maxVal - minVal) / (maxVal + minVal);
    }

    /// <summary>
    ///     Check for uneven illumination using grid analysis.
    /// </summary>
    private static double EstimateUniformity(Mat gray)
    {
        var gridH = gray.Rows / 4;
        var gridW = gray.Cols / 4;
        var means = new List<double>();

        for (var i = 0; i < 4; i++)
        for (var j = 0; j < 4; j++)
        {
            var roi = new Rect(j * gridW, i * gridH, gridW, gridH);
            using var region = new Mat(gray, roi);
            means.Add(region.Mean().Val0);
        }

        var mean = means.Average();
        var std = Math.Sqrt(means.Average(x => Math.Pow(x - mean, 2)));
        return mean > 0 ? std / mean : 0;
    }

    /// <summary>
    ///     Estimate text density (percentage of image with text).
    /// </summary>
    private static double EstimateTextDensity(Mat gray)
    {
        using var binary = new Mat();
        Cv2.Threshold(gray, binary, 0, 255,
            ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

        var nonZero = Cv2.CountNonZero(binary);
        var total = binary.Rows * binary.Cols;
        return (double)nonZero / total;
    }

    public record QualityReport
    {
        public double BlurScore { get; init; }
        public double SkewAngle { get; init; }
        public double NoiseLevel { get; init; }
        public double ContrastScore { get; init; }
        public double BrightnessUniformity { get; init; }
        public double TextDensity { get; init; }
        public bool NeedsPreprocessing { get; init; }
        public string[] Recommendations { get; init; } = [];
    }
}