using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;
using SignSummarizer.Pipelines;
using SignSummarizer.Services;

namespace SignSummarizer.UI.Services;

public interface IVideoPlayerService
{
    bool IsPlaying { get; }
    TimeSpan Duration { get; }
    TimeSpan Position { get; }
    Task LoadVideoAsync(string path, CancellationToken cancellationToken = default);
    Task PlayAsync();
    Task PauseAsync();
    Task StopAsync();
    Task SeekAsync(TimeSpan position);
    Task StepForwardAsync();
    Task StepBackwardAsync();

    event EventHandler<TimeSpan> PositionChanged;
    event EventHandler<bool> PlaybackStateChanged;
    event EventHandler<Bitmap?> FrameUpdated;
}

public sealed class VideoPlayerService : IVideoPlayerService, IDisposable
{
    private readonly VideoCaptureService? _captureService;
    private readonly ISignWaveCoordinator _coordinator;
    private readonly ILogger<VideoPlayerService> _logger;
    private readonly Timer _timer;
    private string? _currentPath;
    private CancellationTokenSource? _playbackCts;
    private TimeSpan _position;

    public VideoPlayerService(
        ILogger<VideoPlayerService> logger,
        ISignWaveCoordinator coordinator)
    {
        _logger = logger;
        _coordinator = coordinator;
        _timer = new Timer(OnTimerTick, null, Timeout.Infinite, 16); // ~60fps
    }

    public void Dispose()
    {
        _timer.Dispose();
        _playbackCts?.Dispose();
    }

    public bool IsPlaying { get; private set; }

    public TimeSpan Duration { get; private set; }

    public TimeSpan Position => _position;

    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler<bool>? PlaybackStateChanged;
    public event EventHandler<Bitmap?>? FrameUpdated;

    public async Task LoadVideoAsync(string path, CancellationToken cancellationToken = default)
    {
        await StopAsync();

        _currentPath = path;
        _logger.LogInformation("Loading video: {Path}", path);

        try
        {
            // Simple duration check - in real app would use FFmpeg
            Duration = TimeSpan.FromMinutes(1);

            // Process initial frame
            await SeekAsync(TimeSpan.Zero);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load video");
            throw;
        }
    }

    public async Task PlayAsync()
    {
        if (IsPlaying) return;

        IsPlaying = true;
        _playbackCts = new CancellationTokenSource();
        _timer.Change(0, 16);

        PlaybackStateChanged?.Invoke(this, true);
        _logger.LogInformation("Playback started");

        await Task.CompletedTask;
    }

    public async Task PauseAsync()
    {
        if (!IsPlaying) return;

        IsPlaying = false;
        _playbackCts?.Cancel();
        _timer.Change(Timeout.Infinite, 16);

        PlaybackStateChanged?.Invoke(this, false);
        _logger.LogInformation("Playback paused");

        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        await PauseAsync();
        await SeekAsync(TimeSpan.Zero);
    }

    public async Task SeekAsync(TimeSpan position)
    {
        _position = position;
        PositionChanged?.Invoke(this, position);

        // Render frame at position
        // Implementation omitted for brevity
        await Task.CompletedTask;
    }

    public async Task StepForwardAsync()
    {
        await PauseAsync();
        await SeekAsync(_position.Add(TimeSpan.FromMilliseconds(33)));
    }

    public async Task StepBackwardAsync()
    {
        await PauseAsync();
        await SeekAsync(_position.Subtract(TimeSpan.FromMilliseconds(33)));
    }

    private void OnTimerTick(object? state)
    {
        if (!IsPlaying) return;

        _position = _position.Add(TimeSpan.FromMilliseconds(16));
        PositionChanged?.Invoke(this, _position);

        if (_position >= Duration) _ = StopAsync();
    }
}