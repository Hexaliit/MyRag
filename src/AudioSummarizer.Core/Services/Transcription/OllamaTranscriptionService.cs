using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioSummarizer.Core.Config;
using AudioSummarizer.Core.Models;
using NAudio.Wave;

namespace AudioSummarizer.Core.Services.Transcription;

/// <summary>
/// HTTP-based transcription service using Ollama API
/// Fallback option when Whisper.NET is unavailable
/// </summary>
public sealed class OllamaTranscriptionService : ITranscriptionService
{
    private readonly AudioConfig _config;
    private readonly ILogger<OllamaTranscriptionService> _logger;
    private readonly HttpClient _httpClient;

    public string ProviderName => "Ollama";

    public OllamaTranscriptionService(
        IOptions<AudioConfig> config,
        ILogger<OllamaTranscriptionService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _config = config.Value;
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("Ollama");
        _httpClient.BaseAddress = new Uri(_config.Ollama?.BaseUrl ?? "http://localhost:11434");
        _httpClient.Timeout = TimeSpan.FromMinutes(10); // Long timeout for large audio files
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/tags", cancellationToken);
            if (!response.IsSuccessStatusCode) return false;

            // Check if whisper model is available
            var tags = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(cancellationToken);
            return tags?.Models?.Any(m => m.Name.Contains("whisper", StringComparison.OrdinalIgnoreCase)) ?? false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ollama is not available: {Message}", ex.Message);
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
            _logger.LogDebug("Transcribing {AudioPath} with Ollama (language: {Language})",
                audioPath, language ?? "auto");

            // Convert audio to WAV format for Ollama (if needed)
            var wavPath = await ConvertToWavIfNeededAsync(audioPath, cancellationToken);

            try
            {
                // Read audio file as base64
                var audioBytes = await File.ReadAllBytesAsync(wavPath, cancellationToken);
                var audioBase64 = Convert.ToBase64String(audioBytes);

                // Call Ollama API
                var request = new OllamaTranscribeRequest
                {
                    Model = _config.Ollama?.Model ?? "whisper",
                    Audio = audioBase64,
                    Language = language
                };

                var response = await _httpClient.PostAsJsonAsync("/api/transcribe", request, cancellationToken);
                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<OllamaTranscribeResponse>(cancellationToken);

                sw.Stop();

                if (result == null || string.IsNullOrEmpty(result.Text))
                {
                    throw new InvalidOperationException("Ollama returned empty transcription");
                }

                // Parse segments if available
                var segments = ParseSegments(result.Text);

                _logger.LogInformation(
                    "Transcribed {AudioPath} in {ElapsedMs}ms: {SegmentCount} segments",
                    Path.GetFileName(audioPath), sw.ElapsedMilliseconds, segments.Count);

                return new AudioTranscript
                {
                    Text = result.Text.Trim(),
                    Segments = segments,
                    Language = result.Language ?? language ?? "unknown",
                    Provider = ProviderName,
                    Confidence = null, // Ollama doesn't provide confidence scores
                    ProcessingTimeMs = sw.ElapsedMilliseconds
                };
            }
            finally
            {
                // Clean up temporary WAV file if we created one
                if (wavPath != audioPath && File.Exists(wavPath))
                {
                    File.Delete(wavPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ollama transcription failed for {AudioPath}: {Message}",
                audioPath, ex.Message);
            throw;
        }
    }

    private async Task<string> ConvertToWavIfNeededAsync(string audioPath, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(audioPath).ToLowerInvariant();
        if (extension == ".wav") return audioPath;

        // Convert to WAV using NAudio
        var tempWavPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.wav");

        await Task.Run(() =>
        {
            using var reader = new AudioFileReader(audioPath);
            WaveFileWriter.CreateWaveFile(tempWavPath, reader);
        }, cancellationToken);

        _logger.LogDebug("Converted {AudioPath} to WAV: {TempPath}", audioPath, tempWavPath);
        return tempWavPath;
    }

    private List<TranscriptSegment> ParseSegments(string text)
    {
        // Simple segmentation by sentences (Ollama doesn't provide timestamps)
        // In a real implementation, we'd split by sentence boundaries
        var sentences = text.Split(new[] { ". ", "! ", "? " }, StringSplitOptions.RemoveEmptyEntries);
        var segments = new List<TranscriptSegment>();
        var currentTime = 0.0;
        var avgSegmentDuration = 5.0; // Assume ~5 seconds per sentence

        foreach (var sentence in sentences)
        {
            segments.Add(new TranscriptSegment
            {
                Start = currentTime,
                End = currentTime + avgSegmentDuration,
                Text = sentence.Trim()
            });
            currentTime += avgSegmentDuration;
        }

        return segments;
    }

    #region Ollama API Models

    private class OllamaTranscribeRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("audio")]
        public required string Audio { get; init; }

        [JsonPropertyName("language")]
        public string? Language { get; init; }
    }

    private class OllamaTranscribeResponse
    {
        [JsonPropertyName("text")]
        public required string Text { get; init; }

        [JsonPropertyName("language")]
        public string? Language { get; init; }
    }

    private class OllamaTagsResponse
    {
        [JsonPropertyName("models")]
        public List<OllamaModel>? Models { get; init; }
    }

    private class OllamaModel
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }
    }

    #endregion
}
