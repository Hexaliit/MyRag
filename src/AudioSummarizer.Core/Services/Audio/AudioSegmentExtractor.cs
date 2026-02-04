using AudioSummarizer.Core.Services.Voice;
using NAudio.Wave;

namespace AudioSummarizer.Core.Services.Audio;

/// <summary>
///     Extracts time-range segments from audio files for evidence and playback.
///     Used to create small clips of speaker turns for diarization evidence.
/// </summary>
public class AudioSegmentExtractor
{
    private readonly ILogger<AudioSegmentExtractor> _logger;

    public AudioSegmentExtractor(ILogger<AudioSegmentExtractor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///     Extract a time range from an audio file and return as Base64-encoded WAV
    /// </summary>
    /// <param name="audioPath">Source audio file path</param>
    /// <param name="startSeconds">Start time in seconds</param>
    /// <param name="endSeconds">End time in seconds</param>
    /// <param name="maxDurationSeconds">Maximum segment duration (default: 3 seconds)</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Base64-encoded WAV data</returns>
    public async Task<string> ExtractSegmentAsBase64Async(
        string audioPath,
        double startSeconds,
        double endSeconds,
        double maxDurationSeconds = 3.0,
        CancellationToken cancellationToken = default)
    {
        var wavBytes = await ExtractSegmentAsWavBytesAsync(
            audioPath,
            startSeconds,
            endSeconds,
            maxDurationSeconds,
            cancellationToken);

        return Convert.ToBase64String(wavBytes);
    }

    /// <summary>
    ///     Extract a time range from an audio file and return as WAV bytes
    /// </summary>
    public async Task<byte[]> ExtractSegmentAsWavBytesAsync(
        string audioPath,
        double startSeconds,
        double endSeconds,
        double maxDurationSeconds = 3.0,
        CancellationToken cancellationToken = default)
    {
        // Clamp duration to max
        var duration = endSeconds - startSeconds;
        if (duration > maxDurationSeconds)
        {
            endSeconds = startSeconds + maxDurationSeconds;
            _logger.LogDebug("Clamping segment duration from {Original}s to {Max}s",
                duration, maxDurationSeconds);
        }

        using var outputStream = new MemoryStream();
        await ExtractSegmentToStreamAsync(
            audioPath,
            startSeconds,
            endSeconds,
            outputStream,
            cancellationToken);

        return outputStream.ToArray();
    }

    /// <summary>
    ///     Extract a time range from an audio file and save to a file
    /// </summary>
    public async Task ExtractSegmentToFileAsync(
        string audioPath,
        double startSeconds,
        double endSeconds,
        string outputPath,
        double maxDurationSeconds = 3.0,
        CancellationToken cancellationToken = default)
    {
        // Clamp duration to max
        var duration = endSeconds - startSeconds;
        if (duration > maxDurationSeconds) endSeconds = startSeconds + maxDurationSeconds;

        await using var outputStream = File.Create(outputPath);
        await ExtractSegmentToStreamAsync(
            audioPath,
            startSeconds,
            endSeconds,
            outputStream,
            cancellationToken);

        _logger.LogInformation("Extracted segment {Start:F2}s-{End:F2}s to {OutputPath}",
            startSeconds, endSeconds, outputPath);
    }

    /// <summary>
    ///     Extract audio segment and write to stream as WAV
    /// </summary>
    private async Task ExtractSegmentToStreamAsync(
        string audioPath,
        double startSeconds,
        double endSeconds,
        Stream outputStream,
        CancellationToken cancellationToken)
    {
        using var reader = new AudioFileReader(audioPath);

        // Calculate byte positions
        var startPosition = (long)(startSeconds * reader.WaveFormat.AverageBytesPerSecond);
        var endPosition = (long)(endSeconds * reader.WaveFormat.AverageBytesPerSecond);

        // Align to block boundaries
        startPosition -= startPosition % reader.WaveFormat.BlockAlign;
        endPosition -= endPosition % reader.WaveFormat.BlockAlign;

        // Clamp to file boundaries
        startPosition = Math.Max(0, startPosition);
        endPosition = Math.Min(reader.Length, endPosition);

        var segmentLength = endPosition - startPosition;

        if (segmentLength <= 0)
        {
            _logger.LogWarning("Invalid segment range: {Start}-{End} seconds", startSeconds, endSeconds);
            return;
        }

        // Seek to start position
        reader.Position = startPosition;

        // Create trimmed reader
        using var trimmedReader = new WaveFileReader(new TrimmedStream(reader, segmentLength));

        // Convert to 16-bit PCM WAV for maximum compatibility
        using var pcmStream = WaveFormatConversionStream.CreatePcmStream(trimmedReader);
        using var resampler = new MediaFoundationResampler(pcmStream, new WaveFormat(16000, 16, 1))
        {
            ResamplerQuality = 60 // High quality
        };

        // Write to output as WAV
        await Task.Run(() => WaveFileWriter.WriteWavFileToStream(outputStream, resampler), cancellationToken);
    }

    /// <summary>
    ///     Extract speaker sample clips from diarization result
    ///     Returns dictionary of speaker ID -> Base64 WAV
    /// </summary>
    public virtual async Task<Dictionary<string, string>> ExtractSpeakerSamplesAsync(
        string audioPath,
        IEnumerable<SpeakerTurn> turns,
        double sampleDurationSeconds = 2.0,
        CancellationToken cancellationToken = default)
    {
        var samples = new Dictionary<string, string>();

        // Group turns by speaker and take first turn for each
        var speakerFirstTurns = turns
            .GroupBy(t => t.SpeakerId)
            .Select(g => g.OrderBy(t => t.StartSeconds).First())
            .ToList();

        foreach (var turn in speakerFirstTurns)
            try
            {
                // Extract sample from start of first turn
                var sampleEnd = Math.Min(turn.EndSeconds, turn.StartSeconds + sampleDurationSeconds);
                var base64 = await ExtractSegmentAsBase64Async(
                    audioPath,
                    turn.StartSeconds,
                    sampleEnd,
                    sampleDurationSeconds,
                    cancellationToken);

                samples[turn.SpeakerId] = base64;

                _logger.LogDebug("Extracted {Duration:F2}s sample for {SpeakerId}",
                    sampleEnd - turn.StartSeconds, turn.SpeakerId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract sample for {SpeakerId}", turn.SpeakerId);
            }

        return samples;
    }
}

/// <summary>
///     Helper class to create a trimmed stream view of an audio stream
/// </summary>
internal class TrimmedStream : Stream
{
    private readonly long _length;
    private readonly Stream _sourceStream;
    private readonly long _startPosition;
    private long _position;

    public TrimmedStream(Stream sourceStream, long length)
    {
        _sourceStream = sourceStream ?? throw new ArgumentNullException(nameof(sourceStream));
        _startPosition = sourceStream.Position;
        _length = length;
        _position = 0;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0 || value > _length)
                throw new ArgumentOutOfRangeException(nameof(value));

            _position = value;
            _sourceStream.Position = _startPosition + value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var maxRead = (int)Math.Min(count, _length - _position);
        if (maxRead <= 0)
            return 0;

        var bytesRead = _sourceStream.Read(buffer, offset, maxRead);
        _position += bytesRead;
        return bytesRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentException("Invalid seek origin", nameof(origin))
        };

        Position = newPosition;
        return newPosition;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }
}