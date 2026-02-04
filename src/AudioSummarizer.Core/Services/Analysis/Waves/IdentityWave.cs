using System.Security.Cryptography;
using AudioSummarizer.Core.Models;
using NAudio.Wave;

namespace AudioSummarizer.Core.Services.Analysis.Waves;

/// <summary>
///     Identity Wave - Extracts deterministic cryptographic and file metadata signals.
///     Priority: 100 (highest - runs first)
///     Signals:
///     - audio.hash.sha256: SHA-256 hash of file bytes
///     - audio.hash.pcm_sha256: SHA-256 hash of decoded PCM audio
///     - audio.format: File format (mp3, wav, etc.)
///     - audio.duration_seconds: Total duration
///     - audio.sample_rate: Sample rate in Hz
///     - audio.channels: Number of audio channels
///     - audio.bitrate: Bitrate in bps
/// </summary>
public sealed class IdentityWave : IAudioWave
{
    private readonly ILogger<IdentityWave> _logger;

    public IdentityWave(ILogger<IdentityWave> logger)
    {
        _logger = logger;
    }

    public string Name => "IdentityWave";
    public int Priority => 100;

    public bool ShouldRun(string audioPath, AnalysisContext context)
    {
        // Always run - this is the foundation wave
        return true;
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string audioPath,
        AnalysisContext context,
        CancellationToken cancellationToken = default)
    {
        var signals = new List<Signal>();

        try
        {
            // 1. Compute SHA-256 of file bytes (hard identity)
            var fileSha256 = await ComputeFileSha256Async(audioPath, cancellationToken);
            signals.Add(new Signal
            {
                Name = "audio.hash.sha256",
                Value = fileSha256,
                Type = SignalType.Identity,
                Source = Name
            });

            // 2. Extract file metadata using NAudio
            var metadata = ExtractFileMetadata(audioPath);
            signals.AddRange(metadata);

            // 3. Compute PCM SHA-256 (content-based identity)
            // This is expensive, so we'll do it asynchronously
            try
            {
                var pcmSha256 = await ComputePcmSha256Async(audioPath, cancellationToken);
                signals.Add(new Signal
                {
                    Name = "audio.hash.pcm_sha256",
                    Value = pcmSha256,
                    Type = SignalType.Identity,
                    Source = Name
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compute PCM SHA-256 for {AudioPath}", audioPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IdentityWave failed for {AudioPath}: {Message}", audioPath, ex.Message);
            throw;
        }

        return signals;
    }

    private static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<string> ComputePcmSha256Async(string filePath, CancellationToken cancellationToken)
    {
        using var reader = new AudioFileReader(filePath);
        using var sha256 = SHA256.Create();

        var buffer = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels]; // 1 second buffer
        int samplesRead;

        while ((samplesRead = await Task.Run(() => reader.Read(buffer, 0, buffer.Length), cancellationToken)) > 0)
        {
            // Convert float samples to bytes for hashing
            var bytes = new byte[samplesRead * sizeof(float)];
            Buffer.BlockCopy(buffer, 0, bytes, 0, bytes.Length);

            sha256.TransformBlock(bytes, 0, bytes.Length, null, 0);

            cancellationToken.ThrowIfCancellationRequested();
        }

        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
    }

    private IEnumerable<Signal> ExtractFileMetadata(string filePath)
    {
        var signals = new List<Signal>();

        try
        {
            // File-level metadata
            var fileInfo = new FileInfo(filePath);

            signals.Add(new Signal
            {
                Name = "audio.file_size_bytes",
                Value = fileInfo.Length,
                Type = SignalType.Metadata,
                Source = Name
            });

            signals.Add(new Signal
            {
                Name = "audio.file_size_mb",
                Value = Math.Round(fileInfo.Length / (1024.0 * 1024.0), 2),
                Type = SignalType.Metadata,
                Source = Name
            });

            signals.Add(new Signal
            {
                Name = "audio.created_at",
                Value = fileInfo.CreationTimeUtc.ToString("O"),
                Type = SignalType.Metadata,
                Source = Name
            });

            signals.Add(new Signal
            {
                Name = "audio.modified_at",
                Value = fileInfo.LastWriteTimeUtc.ToString("O"),
                Type = SignalType.Metadata,
                Source = Name
            });

            // Audio format metadata
            using var reader = new AudioFileReader(filePath);

            // File format
            var extension = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
            signals.Add(new Signal
            {
                Name = "audio.format",
                Value = extension,
                Type = SignalType.Metadata,
                Source = Name
            });

            // Duration
            var durationSeconds = reader.TotalTime.TotalSeconds;
            signals.Add(new Signal
            {
                Name = "audio.duration_seconds",
                Value = durationSeconds,
                Type = SignalType.Metadata,
                Source = Name
            });

            signals.Add(new Signal
            {
                Name = "audio.duration_formatted",
                Value = reader.TotalTime.ToString(@"hh\:mm\:ss"),
                Type = SignalType.Metadata,
                Source = Name
            });

            // Sample rate
            signals.Add(new Signal
            {
                Name = "audio.sample_rate",
                Value = reader.WaveFormat.SampleRate,
                Type = SignalType.Metadata,
                Source = Name
            });

            // Channels
            signals.Add(new Signal
            {
                Name = "audio.channels",
                Value = reader.WaveFormat.Channels,
                Type = SignalType.Metadata,
                Source = Name
            });

            signals.Add(new Signal
            {
                Name = "audio.channel_layout",
                Value = reader.WaveFormat.Channels switch
                {
                    1 => "mono",
                    2 => "stereo",
                    6 => "5.1",
                    8 => "7.1",
                    _ => $"{reader.WaveFormat.Channels}-channel"
                },
                Type = SignalType.Metadata,
                Source = Name
            });

            // Bits per sample
            signals.Add(new Signal
            {
                Name = "audio.bits_per_sample",
                Value = reader.WaveFormat.BitsPerSample,
                Type = SignalType.Metadata,
                Source = Name
            });

            // Block align
            signals.Add(new Signal
            {
                Name = "audio.block_align",
                Value = reader.WaveFormat.BlockAlign,
                Type = SignalType.Metadata,
                Source = Name
            });

            // Encoding
            signals.Add(new Signal
            {
                Name = "audio.encoding",
                Value = reader.WaveFormat.Encoding.ToString(),
                Type = SignalType.Metadata,
                Source = Name
            });

            // Average bytes per second (bitrate estimate)
            var averageBytesPerSecond = reader.WaveFormat.AverageBytesPerSecond;
            var bitrateKbps = averageBytesPerSecond * 8 / 1000;
            signals.Add(new Signal
            {
                Name = "audio.bitrate",
                Value = averageBytesPerSecond * 8, // bits per second
                Type = SignalType.Metadata,
                Source = Name
            });

            signals.Add(new Signal
            {
                Name = "audio.bitrate_kbps",
                Value = bitrateKbps,
                Type = SignalType.Metadata,
                Source = Name
            });

            // Total samples
            var totalSamples = reader.Length / (reader.WaveFormat.BitsPerSample / 8) / reader.WaveFormat.Channels;
            signals.Add(new Signal
            {
                Name = "audio.total_samples",
                Value = totalSamples,
                Type = SignalType.Metadata,
                Source = Name
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract metadata from {AudioPath}: {Message}", filePath, ex.Message);
            throw;
        }

        return signals;
    }
}