using System.Numerics;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using VideoSummarizer.Core.Models;

namespace VideoSummarizer.Core.Waves;

/// <summary>
///     Stage 1: Shot boundary detection using deterministic signals.
///     Detects hard cuts, fades, and other transitions without LLM involvement.
/// </summary>
public class ShotDetectionWave : IVideoWave
{
    // Detection thresholds
    private const double HistogramThreshold = 0.4; // Histogram difference for hard cut
    private const double HashThreshold = 0.3; // Perceptual hash difference threshold
    private const double FadeThreshold = 0.15; // Threshold for fade detection
    private const int MinShotFrames = 5; // Minimum frames in a shot
    private const double SampleFps = 3.0; // Sample rate for analysis
    private readonly ILogger<ShotDetectionWave> _logger;

    public ShotDetectionWave(ILogger<ShotDetectionWave> logger)
    {
        _logger = logger;
    }

    public string Name => "shot_detection";
    public int Priority => 900;
    public IReadOnlyList<string> Tags => [VideoSignalTags.Shot, VideoSignalTags.Visual];

    public bool ShouldRun(VideoContext context)
    {
        return context.Metadata != null;
    }

    public async Task ProcessAsync(VideoContext context, CancellationToken ct = default)
    {
        context.ReportProgress("Detecting shots", 0);

        var videoPath = context.VideoPath;
        var metadata = context.Metadata!;

        using var capture = new VideoCapture(videoPath);
        if (!capture.IsOpened())
            throw new InvalidOperationException($"Could not open video: {videoPath}");

        var totalFrames = capture.FrameCount;
        var fps = capture.Fps;
        var sampleInterval = Math.Max(1, (int)(fps / SampleFps));

        _logger.LogInformation("Analyzing {TotalFrames} frames at {SampleFps}fps sample rate (interval: {Interval})",
            totalFrames, SampleFps, sampleInterval);

        var frameAnalyses = new List<FrameAnalysis>();
        Mat? prevFrame = null;
        Mat? prevGray = null;
        var frameIndex = 0;
        var sampledCount = 0;

        // Analyze sampled frames
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            using var frame = new Mat();
            if (!capture.Read(frame) || frame.Empty())
                break;

            if (frameIndex % sampleInterval == 0)
            {
                var analysis = await AnalyzeFrameAsync(frame, prevFrame, prevGray, frameIndex, fps);
                frameAnalyses.Add(analysis);

                // Cache for next iteration
                prevFrame?.Dispose();
                prevGray?.Dispose();
                prevFrame = frame.Clone();
                prevGray = new Mat();
                Cv2.CvtColor(frame, prevGray, ColorConversionCodes.BGR2GRAY);

                sampledCount++;

                if (sampledCount % 100 == 0)
                {
                    var progress = (double)frameIndex / totalFrames * 50;
                    context.ReportProgress($"Analyzing frame {frameIndex}/{totalFrames}", progress);
                }
            }

            frameIndex++;
        }

        prevFrame?.Dispose();
        prevGray?.Dispose();

        _logger.LogInformation("Analyzed {SampledCount} sampled frames", sampledCount);

        // Detect shot boundaries
        context.ReportProgress("Finding shot boundaries", 50);
        var boundaries = DetectBoundaries(frameAnalyses);

        _logger.LogInformation("Detected {BoundaryCount} shot boundaries", boundaries.Count);

        // Create shot segments
        context.ReportProgress("Creating shot segments", 75);
        await CreateShotsAsync(context, boundaries, frameAnalyses, fps, ct);

        // Add summary signals
        context.AddSignals([
            new VideoSignal
                { Key = "shots.count", Value = context.Shots.Count, Source = Name, Tags = [VideoSignalTags.Shot] },
            new VideoSignal
            {
                Key = "shots.avg_duration", Value = context.Shots.Average(s => s.EndTime - s.StartTime), Source = Name,
                Tags = [VideoSignalTags.Shot]
            }
        ]);

        context.ReportProgress("Shot detection complete", 100);
    }

    private Task<FrameAnalysis> AnalyzeFrameAsync(Mat frame, Mat? prevFrame, Mat? prevGray, int frameIndex, double fps)
    {
        var analysis = new FrameAnalysis
        {
            FrameIndex = frameIndex,
            Timestamp = frameIndex / fps
        };

        // Compute histogram (HSV for better color representation)
        using var hsv = new Mat();
        Cv2.CvtColor(frame, hsv, ColorConversionCodes.BGR2HSV);

        var hChannels = new[] { 0, 1 };
        var hHistSize = new[] { 50, 60 };
        var hRanges = new Rangef[] { new(0, 180), new(0, 256) };

        using var histogram = new Mat();
        Cv2.CalcHist([hsv], hChannels, null, histogram, 2, hHistSize, hRanges);
        Cv2.Normalize(histogram, histogram, 0, 1, NormTypes.MinMax);

        analysis.Histogram = histogram.Clone();

        // Compute luminance
        using var gray = new Mat();
        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        analysis.Luminance = Cv2.Mean(gray).Val0;

        // Compute sharpness (Laplacian variance)
        using var laplacian = new Mat();
        Cv2.Laplacian(gray, laplacian, MatType.CV_64F);
        Cv2.MeanStdDev(laplacian, out _, out var stdDev);
        analysis.Sharpness = stdDev.Val0 * stdDev.Val0;

        // Compute perceptual hash (dHash)
        analysis.PerceptualHash = ComputeDHash(gray);

        // Compute motion (if previous frame available)
        if (prevGray != null)
        {
            using var diff = new Mat();
            Cv2.Absdiff(gray, prevGray, diff);
            analysis.MotionScore = Cv2.Mean(diff).Val0 / 255.0;

            // Histogram difference
            if (prevFrame != null)
            {
                using var prevHsv = new Mat();
                Cv2.CvtColor(prevFrame, prevHsv, ColorConversionCodes.BGR2HSV);

                using var prevHist = new Mat();
                Cv2.CalcHist([prevHsv], hChannels, null, prevHist, 2, hHistSize, hRanges);
                Cv2.Normalize(prevHist, prevHist, 0, 1, NormTypes.MinMax);

                analysis.HistogramDiff = 1.0 - Cv2.CompareHist(histogram, prevHist, HistCompMethods.Correl);
            }
        }

        return Task.FromResult(analysis);
    }

    private string ComputeDHash(Mat gray)
    {
        // Resize to 9x8 for 64-bit hash
        using var resized = new Mat();
        Cv2.Resize(gray, resized, new Size(9, 8));

        var hash = 0UL;
        var bit = 0;

        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
        {
            var left = resized.At<byte>(y, x);
            var right = resized.At<byte>(y, x + 1);

            if (left > right)
                hash |= 1UL << bit;

            bit++;
        }

        return hash.ToString("x16");
    }

    private int HammingDistance(string hash1, string hash2)
    {
        if (hash1.Length != hash2.Length) return 64;

        var h1 = Convert.ToUInt64(hash1, 16);
        var h2 = Convert.ToUInt64(hash2, 16);
        var xor = h1 ^ h2;

        return BitOperations.PopCount(xor);
    }

    private List<ShotBoundary> DetectBoundaries(List<FrameAnalysis> frames)
    {
        var boundaries = new List<ShotBoundary>();

        for (var i = 1; i < frames.Count; i++)
        {
            var prev = frames[i - 1];
            var curr = frames[i];

            var cutType = CutType.Unknown;
            var confidence = 0.0;

            // Check histogram difference (hard cut)
            if (curr.HistogramDiff > HistogramThreshold)
            {
                cutType = CutType.HardCut;
                confidence = Math.Min(1.0, curr.HistogramDiff / 0.6);
            }

            // Check perceptual hash difference
            var hashDist = HammingDistance(prev.PerceptualHash, curr.PerceptualHash) / 64.0;
            if (hashDist > HashThreshold && cutType == CutType.Unknown)
            {
                cutType = CutType.HardCut;
                confidence = Math.Max(confidence, hashDist);
            }

            // Check for flash (high luminance spike that returns)
            if (i >= 2 && i < frames.Count - 1)
            {
                var prevPrev = frames[i - 2];
                var next = frames[Math.Min(i + 1, frames.Count - 1)];

                var lumSpike = curr.Luminance - (prev.Luminance + next.Luminance) / 2;
                if (lumSpike > 50 && Math.Abs(prev.Luminance - next.Luminance) < 20)
                {
                    cutType = CutType.Flash;
                    confidence = Math.Min(1.0, lumSpike / 100);
                }
            }

            // Check for fade (gradual luminance change)
            if (cutType == CutType.Unknown && i >= 3)
            {
                var lumChanges = new List<double>();
                for (var j = i - 3; j < i; j++) lumChanges.Add(frames[j + 1].Luminance - frames[j].Luminance);

                var avgChange = lumChanges.Average();
                var isMonotonic = lumChanges.All(c => c * avgChange > 0);

                if (isMonotonic && Math.Abs(avgChange) > 5)
                {
                    cutType = CutType.Fade;
                    confidence = Math.Min(1.0, Math.Abs(avgChange) / 15);
                }
            }

            if (cutType != CutType.Unknown && confidence > 0.5)
                boundaries.Add(new ShotBoundary
                {
                    FrameIndex = curr.FrameIndex,
                    Timestamp = curr.Timestamp,
                    CutType = cutType,
                    Confidence = confidence
                });
        }

        // Filter boundaries that are too close together
        var filtered = new List<ShotBoundary>();
        ShotBoundary? lastBoundary = null;

        foreach (var boundary in boundaries.OrderBy(b => b.FrameIndex))
            if (lastBoundary == null ||
                boundary.FrameIndex - lastBoundary.FrameIndex >= MinShotFrames)
            {
                filtered.Add(boundary);
                lastBoundary = boundary;
            }
            else if (boundary.Confidence > lastBoundary.Confidence)
            {
                // Replace with higher confidence boundary
                filtered[^1] = boundary;
                lastBoundary = boundary;
            }

        return filtered;
    }

    private Task CreateShotsAsync(
        VideoContext context,
        List<ShotBoundary> boundaries,
        List<FrameAnalysis> frames,
        double fps,
        CancellationToken ct)
    {
        var videoId = context.Metadata!.Id;
        var lastBoundaryTime = 0.0;

        for (var i = 0; i <= boundaries.Count; i++)
        {
            var startTime = lastBoundaryTime;
            var endTime = i < boundaries.Count ? boundaries[i].Timestamp : context.Metadata.Duration;

            if (endTime - startTime < 0.1) continue; // Skip very short shots

            // Find keyframe (highest sharpness in shot)
            var shotFrames = frames
                .Where(f => f.Timestamp >= startTime && f.Timestamp < endTime)
                .ToList();

            var keyframe = shotFrames.OrderByDescending(f => f.Sharpness).FirstOrDefault();
            var avgMotion = shotFrames.Average(f => f.MotionScore);

            var shot = new ShotSegment
            {
                Id = Guid.NewGuid(),
                VideoId = videoId,
                StartTime = startTime,
                EndTime = endTime,
                KeyframeIndex = keyframe?.FrameIndex ?? (int)(startTime * fps),
                CutType = i < boundaries.Count ? boundaries[i].CutType : CutType.Unknown,
                MotionScore = avgMotion,
                Confidence = i < boundaries.Count ? boundaries[i].Confidence : 1.0,
                PerceptualHash = keyframe?.PerceptualHash
            };

            context.Shots.Add(shot);

            // Store frame timestamp mapping
            if (keyframe != null) context.FrameTimestamps[keyframe.FrameIndex] = keyframe.Timestamp;

            lastBoundaryTime = endTime;
        }

        _logger.LogInformation("Created {ShotCount} shots", context.Shots.Count);

        return Task.CompletedTask;
    }

    private class FrameAnalysis
    {
        public int FrameIndex { get; init; }
        public double Timestamp { get; init; }
        public double Luminance { get; set; }
        public double Sharpness { get; set; }
        public double MotionScore { get; set; }
        public double HistogramDiff { get; set; }
        public string PerceptualHash { get; set; } = "";
        public Mat? Histogram { get; set; }
    }

    private class ShotBoundary
    {
        public int FrameIndex { get; init; }
        public double Timestamp { get; init; }
        public CutType CutType { get; init; }
        public double Confidence { get; init; }
    }
}