using AudioSummarizer.Core.Config;
using AudioSummarizer.Core.Models;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
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

            // Convert audio to float samples (Whisper.NET samples API)
            var samples = await ConvertToSamplesAsync(audioPath, cancellationToken);
            await foreach (var result in processor.ProcessAsync(samples, cancellationToken))
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

    private async Task<float[]> ConvertToSamplesAsync(string audioPath, CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            using var reader = new AudioFileReader(audioPath);

            // Resample to 16kHz if needed (Whisper.NET requirement)
            ISampleProvider sampleProvider = reader;

            if (reader.WaveFormat.SampleRate != 16000)
            {
                // Use WdlResamplingSampleProvider - pure .NET resampler that implements ISampleProvider
                sampleProvider = new WdlResamplingSampleProvider(reader, 16000);
            }

            // Convert to mono if needed (Whisper.NET requires mono)
            if (sampleProvider.WaveFormat.Channels > 1)
            {
                sampleProvider = new StereoToMonoSampleProvider(sampleProvider)
                {
                    LeftVolume = 0.5f,
                    RightVolume = 0.5f
                };
            }

            // Read all samples into memory
            var sampleList = new List<float>();
            var buffer = new float[sampleProvider.WaveFormat.SampleRate]; // 1 second buffer
            int samplesRead;

            while ((samplesRead = sampleProvider.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < samplesRead; i++)
                {
                    sampleList.Add(buffer[i]);
                }
            }

            _logger.LogDebug("Converted {AudioPath} to {SampleCount} float samples (16kHz mono)",
                audioPath, sampleList.Count);

            return sampleList.ToArray();
        }, cancellationToken);
    }

    /// <summary>
    /// Stream wrapper that deletes temp file on dispose
    /// </summary>
    private class TempFileStream : FileStream
    {
        private readonly string _filePath;
        private readonly ILogger? _logger;

        public TempFileStream(string path, ILogger? logger = null)
            : base(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        {
            _filePath = path;
            _logger = logger;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try
            {
                if (File.Exists(_filePath))
                {
                    File.Delete(_filePath);
                    _logger?.LogDebug("Deleted temp WAV file: {FilePath}", _filePath);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to delete temp WAV file: {FilePath}", _filePath);
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
