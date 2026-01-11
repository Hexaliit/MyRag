using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace SignSummarizer.Services;

public sealed class VideoCaptureService : IDisposable
{
    private readonly ILogger<VideoCaptureService> _logger;
    private readonly string _videoPath;
    private VideoCapture? _capture;

    public VideoCaptureService(string videoPath, ILogger<VideoCaptureService> logger)
    {
        _videoPath = videoPath;
        _logger = logger;
    }

    public void Dispose()
    {
        _capture?.Dispose();
    }

    public async IAsyncEnumerable<FrameInfo> CaptureFramesAsync(
        int targetFps = 30,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _capture = new VideoCapture(_videoPath);

        if (!_capture.IsOpened())
            throw new InvalidOperationException($"Failed to open video: {_videoPath}");

        var frameCount = _capture.FrameCount;
        var fps = _capture.Fps;
        var frameInterval = (int)(1000.0 / Math.Min(targetFps, fps));
        var totalDuration = TimeSpan.FromSeconds(frameCount / fps);

        var frameIndex = 0;
        using var frame = new Mat();

        while (_capture.Read(frame) && !frame.Empty() && !cancellationToken.IsCancellationRequested)
        {
            var timestamp = TimeSpan.FromSeconds(frameIndex / fps);
            yield return new FrameInfo(frameIndex, timestamp, frame);
            frameIndex++;
        }
    }
}

public sealed record FrameInfo(int Index, TimeSpan Timestamp, Mat Image);