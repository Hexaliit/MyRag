using System.IO.Hashing;
using Microsoft.Extensions.Logging;
using VideoSummarizer.Core.Models;
using Xabe.FFmpeg;

namespace VideoSummarizer.Core.Waves;

/// <summary>
/// Stage 0: Normalize the video - demux, extract metadata, compute content hash.
/// This is always the first wave to run.
/// </summary>
public class NormalizeWave : IVideoWave
{
    private readonly ILogger<NormalizeWave> _logger;

    public string Name => "normalize";
    public int Priority => 1000; // Highest priority - runs first
    public IReadOnlyList<string> Tags => [VideoSignalTags.Metadata];

    public NormalizeWave(ILogger<NormalizeWave> logger)
    {
        _logger = logger;
    }

    public async Task ProcessAsync(VideoContext context, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        context.ReportProgress("Normalizing video", 0);

        // Emit wave started signal
        context.AddSignal(new VideoSignal
        {
            Key = $"{Name}.started",
            Value = DateTimeOffset.UtcNow,
            Source = Name,
            Tags = [VideoSignalTags.Metadata, "wave", "flow"]
        });

        var videoPath = context.VideoPath;
        if (!File.Exists(videoPath))
            throw new FileNotFoundException($"Video file not found: {videoPath}");

        // Set FFmpeg executables path if not in PATH
        EnsureFFmpegPath();

        // Get media info
        var mediaInfo = await FFmpeg.GetMediaInfo(videoPath, ct);
        var videoStream = mediaInfo.VideoStreams.FirstOrDefault();
        var audioStream = mediaInfo.AudioStreams.FirstOrDefault();

        if (videoStream == null)
            throw new InvalidOperationException("No video stream found in file");

        // Compute content hash
        var contentHash = await ComputeContentHashAsync(videoPath, ct);

        // Create metadata
        context.Metadata = new VideoMetadata
        {
            Id = Guid.NewGuid(),
            ContentHash = contentHash,
            Duration = mediaInfo.Duration.TotalSeconds,
            Fps = videoStream.Framerate,
            Width = videoStream.Width,
            Height = videoStream.Height,
            VideoCodec = videoStream.Codec,
            AudioCodec = audioStream?.Codec,
            AudioSampleRate = audioStream?.SampleRate ?? 0,
            AudioChannels = audioStream?.Channels ?? 0,
            Status = VideoProcessingStatus.Normalizing,
            ProcessingStarted = DateTimeOffset.UtcNow
        };

        _logger.LogInformation("Video metadata: {Duration}s, {Width}x{Height} @ {Fps}fps, codec: {Codec}",
            context.Metadata.Duration,
            context.Metadata.Width,
            context.Metadata.Height,
            context.Metadata.Fps,
            context.Metadata.VideoCodec);

        // Add metadata signals
        context.AddSignals([
            new VideoSignal { Key = "video.duration", Value = context.Metadata.Duration, Source = Name, Tags = [VideoSignalTags.Metadata] },
            new VideoSignal { Key = "video.fps", Value = context.Metadata.Fps, Source = Name, Tags = [VideoSignalTags.Metadata] },
            new VideoSignal { Key = "video.width", Value = context.Metadata.Width, Source = Name, Tags = [VideoSignalTags.Metadata] },
            new VideoSignal { Key = "video.height", Value = context.Metadata.Height, Source = Name, Tags = [VideoSignalTags.Metadata] },
            new VideoSignal { Key = "video.codec", Value = context.Metadata.VideoCodec, Source = Name, Tags = [VideoSignalTags.Metadata] },
            new VideoSignal { Key = "video.content_hash", Value = contentHash, Source = Name, Tags = [VideoSignalTags.Metadata] },
        ]);

        if (audioStream != null)
        {
            context.AddSignals([
                new VideoSignal { Key = "audio.codec", Value = context.Metadata.AudioCodec, Source = Name, Tags = [VideoSignalTags.Audio, VideoSignalTags.Metadata] },
                new VideoSignal { Key = "audio.sample_rate", Value = context.Metadata.AudioSampleRate, Source = Name, Tags = [VideoSignalTags.Audio, VideoSignalTags.Metadata] },
                new VideoSignal { Key = "audio.channels", Value = context.Metadata.AudioChannels, Source = Name, Tags = [VideoSignalTags.Audio, VideoSignalTags.Metadata] },
            ]);
        }

        // Extract audio track for transcription
        if (audioStream != null)
        {
            context.ReportProgress("Extracting audio track", 50);
            context.AudioPath = await ExtractAudioAsync(videoPath, context.WorkingDirectory, ct);
            _logger.LogInformation("Audio extracted to: {AudioPath}", context.AudioPath);

            context.AddSignal(new VideoSignal
            {
                Key = "audio.extracted_path",
                Value = context.AudioPath,
                Source = Name,
                Tags = [VideoSignalTags.Audio]
            });
        }

        sw.Stop();

        // Emit wave completed signal with timing
        context.AddSignals([
            new VideoSignal
            {
                Key = $"{Name}.completed",
                Value = true,
                Source = Name,
                Tags = [VideoSignalTags.Metadata, "wave", "flow"]
            },
            new VideoSignal
            {
                Key = $"{Name}.duration_ms",
                Value = sw.ElapsedMilliseconds,
                Source = Name,
                Tags = [VideoSignalTags.Metadata, "timing", "persist"]
            }
        ]);

        context.ReportProgress("Normalization complete", 100);
    }

    private async Task<string> ComputeContentHashAsync(string videoPath, CancellationToken ct)
    {
        // Use XxHash64 for fast content hashing
        var xxHash = new XxHash64();

        await using var stream = File.OpenRead(videoPath);

        // For large files, hash first 1MB + last 1MB + file size
        var buffer = new byte[1024 * 1024]; // 1MB
        var fileSize = stream.Length;

        // Hash first 1MB
        var bytesRead = await stream.ReadAsync(buffer, ct);
        xxHash.Append(buffer.AsSpan(0, bytesRead));

        // Hash file size
        xxHash.Append(BitConverter.GetBytes(fileSize));

        // Hash last 1MB if file is large enough
        if (fileSize > 2 * 1024 * 1024)
        {
            stream.Seek(-1024 * 1024, SeekOrigin.End);
            bytesRead = await stream.ReadAsync(buffer, ct);
            xxHash.Append(buffer.AsSpan(0, bytesRead));
        }

        return Convert.ToHexString(xxHash.GetCurrentHash()).ToLowerInvariant();
    }

    private async Task<string> ExtractAudioAsync(string videoPath, string workingDir, CancellationToken ct)
    {
        var audioPath = Path.Combine(workingDir, "audio.wav");

        var conversion = FFmpeg.Conversions.New()
            .AddParameter($"-i \"{videoPath}\"")
            .AddParameter("-vn") // No video
            .AddParameter("-acodec pcm_s16le") // PCM 16-bit for Whisper
            .AddParameter("-ar 16000") // 16kHz sample rate for Whisper
            .AddParameter("-ac 1") // Mono
            .SetOutput(audioPath)
            .SetOverwriteOutput(true);

        await conversion.Start(ct);

        return audioPath;
    }

    private static bool _ffmpegPathSet;
    private static readonly object _ffmpegLock = new();

    /// <summary>
    /// Ensure FFmpeg executables path is set. Looks for FFmpeg in common locations.
    /// </summary>
    private static void EnsureFFmpegPath()
    {
        if (_ffmpegPathSet) return;

        lock (_ffmpegLock)
        {
            if (_ffmpegPathSet) return;

            // Check if ffmpeg is already in PATH
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            var pathDirs = pathEnv.Split(Path.PathSeparator);

            foreach (var dir in pathDirs)
            {
                var ffmpegPath = Path.Combine(dir, "ffmpeg.exe");
                if (File.Exists(ffmpegPath))
                {
                    FFmpeg.SetExecutablesPath(dir);
                    _ffmpegPathSet = true;
                    return;
                }
            }

            // Common FFmpeg installation locations
            var searchPaths = new[]
            {
                // Winget installation
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "WinGet", "Packages"),
                // Chocolatey
                @"C:\ProgramData\chocolatey\lib\ffmpeg\tools\ffmpeg\bin",
                // Scoop
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "scoop", "apps", "ffmpeg", "current", "bin"),
                // Manual install
                @"C:\ffmpeg\bin",
                @"C:\Program Files\ffmpeg\bin",
            };

            foreach (var basePath in searchPaths)
            {
                if (!Directory.Exists(basePath)) continue;

                // For winget, search recursively for ffmpeg.exe
                if (basePath.Contains("WinGet"))
                {
                    try
                    {
                        var ffmpegExe = Directory.GetFiles(basePath, "ffmpeg.exe", SearchOption.AllDirectories)
                            .FirstOrDefault();
                        if (ffmpegExe != null)
                        {
                            var binDir = Path.GetDirectoryName(ffmpegExe)!;
                            FFmpeg.SetExecutablesPath(binDir);
                            _ffmpegPathSet = true;
                            return;
                        }
                    }
                    catch
                    {
                        // Ignore search errors
                    }
                }
                else
                {
                    var ffmpegPath = Path.Combine(basePath, "ffmpeg.exe");
                    if (File.Exists(ffmpegPath))
                    {
                        FFmpeg.SetExecutablesPath(basePath);
                        _ffmpegPathSet = true;
                        return;
                    }
                }
            }

            // If we still haven't found it, let Xabe.FFmpeg try on its own
            _ffmpegPathSet = true;
        }
    }
}
