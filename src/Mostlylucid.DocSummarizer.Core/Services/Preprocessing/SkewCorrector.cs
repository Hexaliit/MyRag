using OpenCvSharp;

namespace Mostlylucid.DocSummarizer.Services.Preprocessing;

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

    public record DeskewResult(Mat Image, double Angle, DeskewMethod Method);

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

        return new DeskewResult(result, angle, method);
    }

    /// <summary>
    /// Deskew using minimum area bounding rectangle.
    /// </summary>
    private static (Mat result, double angle) DeskewMinArea(Mat image)
    {
        using var gray = image.Channels() == 1
            ? image.Clone()
            : image.CvtColor(ColorConversionCodes.BGR2GRAY);

        using var binary = new Mat();
        Cv2.Threshold(gray, binary, 0, 255,
            ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

        // Find all non-zero points
        using var nonZero = new Mat();
        Cv2.FindNonZero(binary, nonZero);

        if (nonZero.Empty() || nonZero.Rows < 10)
            return (image.Clone(), 0.0);

        // Convert to Point array for MinAreaRect
        var points = new Point[nonZero.Rows];
        for (var i = 0; i < nonZero.Rows; i++)
        {
            var pt = nonZero.At<Point>(i);
            points[i] = pt;
        }

        var rect = Cv2.MinAreaRect(points);
        var angle = rect.Angle;

        if (angle < -45)
            angle = 90 + angle;
        else if (angle > 45)
            angle -= 90;

        var center = new Point2f(image.Width / 2f, image.Height / 2f);
        using var rotMat = Cv2.GetRotationMatrix2D(center, angle, 1.0);

        var rotated = new Mat();
        Cv2.WarpAffine(image, rotated, rotMat, image.Size(),
            InterpolationFlags.Cubic, BorderTypes.Replicate);

        return (rotated, angle);
    }

    /// <summary>
    /// Deskew using Hough line detection.
    /// </summary>
    private static (Mat result, double angle) DeskewHough(Mat image)
    {
        using var gray = image.Channels() == 1
            ? image.Clone()
            : image.CvtColor(ColorConversionCodes.BGR2GRAY);

        using var edges = new Mat();
        Cv2.Canny(gray, edges, 50, 150);

        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.Dilate(edges, edges, kernel);

        var lines = Cv2.HoughLinesP(edges, 1, Math.PI / 180, 100,
            minLineLength: 100, maxLineGap: 10);

        if (lines.Length == 0)
            return (image.Clone(), 0.0);

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

        angles.Sort();
        var medianAngle = angles[angles.Count / 2];

        var center = new Point2f(image.Width / 2f, image.Height / 2f);
        using var rotMat = Cv2.GetRotationMatrix2D(center, medianAngle, 1.0);

        var rotated = new Mat();
        Cv2.WarpAffine(image, rotated, rotMat, image.Size(),
            InterpolationFlags.Cubic, BorderTypes.Replicate);

        return (rotated, medianAngle);
    }

    /// <summary>
    /// Deskew using projection profile variance.
    /// </summary>
    private static (Mat result, double angle) DeskewProjection(Mat image,
        double angleRange = 15.0, double angleStep = 0.5)
    {
        using var gray = image.Channels() == 1
            ? image.Clone()
            : image.CvtColor(ColorConversionCodes.BGR2GRAY);

        using var binary = new Mat();
        Cv2.Threshold(gray, binary, 0, 255,
            ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

        var center = new Point2f(binary.Width / 2f, binary.Height / 2f);
        var bestAngle = 0.0;
        var bestVariance = 0.0;

        for (var angle = -angleRange; angle <= angleRange; angle += angleStep)
        {
            using var rotMat = Cv2.GetRotationMatrix2D(center, angle, 1.0);
            using var rotated = new Mat();
            Cv2.WarpAffine(binary, rotated, rotMat, binary.Size(), InterpolationFlags.Nearest);

            using var projection = new Mat();
            Cv2.Reduce(rotated, projection, ReduceDimension.Column, ReduceTypes.Sum, MatType.CV_32F);

            Cv2.MeanStdDev(projection, out _, out var stddev);
            var variance = stddev.Val0 * stddev.Val0;

            if (variance > bestVariance)
            {
                bestVariance = variance;
                bestAngle = angle;
            }
        }

        using var finalRotMat = Cv2.GetRotationMatrix2D(center, bestAngle, 1.0);
        var result = new Mat();
        Cv2.WarpAffine(image, result, finalRotMat, image.Size(),
            InterpolationFlags.Cubic, BorderTypes.Replicate);

        return (result, bestAngle);
    }
}
