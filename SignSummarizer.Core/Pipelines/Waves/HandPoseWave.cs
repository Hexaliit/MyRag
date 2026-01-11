using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SignSummarizer.Models;
using SignSummarizer.Services;

namespace SignSummarizer.Pipelines;

public sealed class HandPoseWave : ISignWave
{
    private readonly IHandDetectionService _handDetectionService;
    private readonly ILogger<HandPoseWave> _logger;

    public HandPoseWave(
        ILogger<HandPoseWave> logger,
        IHandDetectionService handDetectionService)
    {
        _logger = logger;
        _handDetectionService = handDetectionService;
    }

    public string Name => "hand_pose";
    public string Description => "Extracts hand landmarks and confidence scores";
    public int Priority => 100;
    public bool Enabled { get; set; } = true;

    public async Task<SignWaveResult> ExecuteAsync(
        SignWaveContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(context.VideoPath)) return SignWaveResult.Failure("No video path provided");

        _logger.LogDebug("Executing HandPoseWave for {AtomId}", context.SignAtomId);

        try
        {
            var landmarks = await ExtractLandmarksAsync(
                context.VideoPath,
                cancellationToken);

            var resultData = new Dictionary<string, object>
            {
                ["landmarks"] = landmarks,
                ["frame_count"] = landmarks.Count,
                ["avg_confidence"] = landmarks
                    .Where(f => f.HasLeftHand || f.HasRightHand)
                    .Average(f => Math.Max(
                        f.LeftHand?.Confidence ?? 0f,
                        f.RightHand?.Confidence ?? 0f)),
                ["hand_presence"] = landmarks.Count(f => f.HasAnyHand) / (double)landmarks.Count
            };

            return SignWaveResult.Success(resultData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HandPoseWave failed");
            return SignWaveResult.Failure(ex.Message);
        }
    }

    public IHandDetectionService GetHandDetectionService()
    {
        return _handDetectionService;
    }

    private async Task<List<FrameLandmarks>> ExtractLandmarksAsync(
        string videoPath,
        CancellationToken cancellationToken)
    {
        var landmarks = new List<FrameLandmarks>();

        var captureService = new VideoCaptureService(
            videoPath,
            NullLogger<VideoCaptureService>.Instance);

        await foreach (var frameInfo in captureService.CaptureFramesAsync(
                           15,
                           cancellationToken))
        {
            var (leftHand, rightHand) = await _handDetectionService
                .DetectBothHandsAsync(
                    frameInfo.Image,
                    frameInfo.Index,
                    frameInfo.Timestamp,
                    cancellationToken);

            var frameLandmarks = new FrameLandmarks(
                frameInfo.Index,
                frameInfo.Timestamp,
                leftHand,
                rightHand);

            landmarks.Add(frameLandmarks);
        }

        captureService.Dispose();

        return landmarks;
    }
}