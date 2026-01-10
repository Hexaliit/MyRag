using AudioSummarizer.Core.Config;
using AudioSummarizer.Core.Models;
using NAudio.Wave;
using Whisper.net;

namespace AudioSummarizer.Core.Services.Transcription;

/// <summary>
/// Local transcription service using Whisper.NET (offline-capable)
/// Supports tiered model escalation for quality/speed tradeoffs
/// </summary>
public sealed class WhisperTranscriptionService : ITranscriptionService, IDisposable
{
    private readonly AudioConfig _config;
    private readonly ILogger<WhisperTranscriptionService> _logger;
    private readonly WhisperModelDownloader _modelDownloader;
    private WhisperFactory? _factory;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _disposed;

    public string ProviderName => "Whisper.NET";

    public WhisperTranscriptionService(
        IOptions<AudioConfig> config,
        ILogger<WhisperTranscriptionService> logger,
        WhisperModelDownloader modelDownloader)
    {
        _config = config.Value;
        _logger = logger;
        _modelDownloader = modelDownloader;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureModelLoadedAsync(cancellationToken);
            return _factory != null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Whisper.NET is not available: {Message}", ex.Message);
            return false;
        }
    }

    public async Task<AudioTranscript> TranscribeAsync(
        string audioPath,
        string? language = null,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await EnsureModelLoadedAsync(cancellationToken);

            if (_factory == null)
            {
                throw new InvalidOperationException("Whisper model not loaded");
            }

            _logger.LogDebug("Transcribing {AudioPath} with Whisper.NET (language: {Language})",
                audioPath, language ?? "auto");

            var segments = new List<TranscriptSegment>();
            var fullText = new System.Text.StringBuilder();
            var confidenceSum = 0.0;
            var segmentCount = 0;

            // Create processor with language if specified
            var processorBuilder = _factory.CreateBuilder()
                .WithThreads(_config.Whisper.Threads);

            if (!string.IsNullOrEmpty(language))
            {
                processorBuilder.WithLanguage(language);
            }

            using var processor = processorBuilder.Build();

            // Convert audio to WAV format (Whisper.NET requires WAV)
            await using var wavStream = await ConvertToWavStreamAsync(audioPath, cancellationToken);
            await foreach (var result in processor.ProcessAsync(wavStream, cancellationToken))
            {
                var segment = new TranscriptSegment
                {
                    Start = result.Start.TotalSeconds,
                    End = result.End.TotalSeconds,
                    Text = result.Text.Trim(),
                    Confidence = result.Probability
                };

                segments.Add(segment);
                fullText.AppendLine(segment.Text);

                // result.Probability is float, not nullable
                confidenceSum += result.Probability;
                segmentCount++;
            }

            sw.Stop();

            var avgConfidence = segmentCount > 0 ? (double?)(confidenceSum / segmentCount) : null;

            _logger.LogInformation(
                "Transcribed {AudioPath} in {ElapsedMs}ms: {SegmentCount} segments, avg confidence: {Confidence:F3}",
                Path.GetFileName(audioPath), sw.ElapsedMilliseconds, segments.Count, avgConfidence ?? 0);

            return new AudioTranscript
            {
                Text = fullText.ToString().Trim(),
                Segments = segments,
                Language = language ?? _config.Whisper.Language,
                Provider = ProviderName,
                Confidence = avgConfidence,
                ProcessingTimeMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Whisper.NET transcription failed for {AudioPath}: {Message}",
                audioPath, ex.Message);
            throw;
        }
    }

    private async Task EnsureModelLoadedAsync(CancellationToken cancellationToken)
    {
        if (_factory != null) return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_factory != null) return; // Double-check after lock

            // Auto-download model if it doesn't exist
            var modelPath = await _modelDownloader.EnsureModelAsync(
                _config.Whisper.ModelPath,
                _config.Whisper.ModelSize,
                _config.Whisper.Language,
                cancellationToken);

            _logger.LogDebug("Loading Whisper model from {ModelPath}", modelPath);
            _factory = WhisperFactory.FromPath(modelPath);
            _logger.LogInformation("Whisper model loaded successfully");
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<Stream> ConvertToWavStreamAsync(string audioPath, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(audioPath).ToLowerInvariant();

        // If already WAV, just return the file stream
        if (extension == ".wav")
        {
            return File.OpenRead(audioPath);
        }

        // Convert to WAV using NAudio (Whisper prefers 16kHz mono)
        var tempWavPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.wav");

        await Task.Run(() =>
        {
            using var reader = new AudioFileReader(audioPath);
            // Create a 16kHz mono stream
            var outFormat = new WaveFormat(16000, 1);
            using var resampler = new MediaFoundationResampler(reader, outFormat);
            WaveFileWriter.CreateWaveFile(tempWavPath, resampler);
        }, cancellationToken);

        // Return file stream, but wrap in a disposable stream that deletes the temp file
        return new TempFileStream(tempWavPath);
    }

    /// <summary>
    /// Stream wrapper that deletes temp file on dispose
    /// </summary>
    private class TempFileStream : FileStream
    {
        private readonly string _filePath;

        public TempFileStream(string path)
            : base(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        {
            _filePath = path;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try
            {
                if (File.Exists(_filePath))
                {
                    File.Delete(_filePath);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _factory?.Dispose();
        _initLock.Dispose();
        _disposed = true;
    }
}
