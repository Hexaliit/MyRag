using OpenCvSharp;

namespace Mostlylucid.DocSummarizer.Images.Services.Preprocessing;

/// <summary>
///     Detects over-correction by comparing bounding box metrics before/after processing.
/// </summary>
public class OverCorrectionDetector
{
    /// <summary>
    ///     Detect over-correction by comparing original and processed images.
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
            Issues = [.. issues],
            Severity = issues.Count / 4.0
        };
    }

    private static ComponentStats GetComponentStats(Mat image)
    {
        using var gray = image.Channels() == 1
            ? image.Clone()
            : image.CvtColor(ColorConversionCodes.BGR2GRAY);

        using var binary = new Mat();
        Cv2.Threshold(gray, binary, 0, 255,
            ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var numLabels = Cv2.ConnectedComponentsWithStats(
            binary, labels, stats, centroids);

        var areas = new List<int>();
        var boxes = new List<Rect>();

        for (var i = 1; i < numLabels; i++)
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

        return new ComponentStats(
            areas.Count,
            areas.Sum(),
            areas.Count > 0 ? areas.Average() : 0,
            [.. boxes]
        );
    }

    public record ComponentStats(
        int NumComponents,
        int TotalInkArea,
        double MeanComponentArea,
        Rect[] BoundingBoxes);

    public record OverCorrectionReport
    {
        public double ComponentRatio { get; init; }
        public double InkRatio { get; init; }
        public double AreaRatio { get; init; }
        public bool IsOverCorrected { get; init; }
        public string[] Issues { get; init; } = [];
        public double Severity { get; init; }
    }
}