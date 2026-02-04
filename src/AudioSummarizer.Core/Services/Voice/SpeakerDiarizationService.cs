using AudioSummarizer.Core.Config;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioSummarizer.Core.Services.Voice;

/// <summary>
///     Pure .NET speaker diarization using voice embeddings and clustering
///     No Python dependencies - uses ECAPA-TDNN embeddings + agglomerative clustering
/// </summary>
public class SpeakerDiarizationService
{
    private readonly AudioConfig _config;
    private readonly VoiceEmbeddingService _embeddingService;
    private readonly ILogger<SpeakerDiarizationService> _logger;

    public SpeakerDiarizationService(
        ILogger<SpeakerDiarizationService> logger,
        VoiceEmbeddingService embeddingService,
        IOptions<AudioConfig> config)
    {
        _logger = logger;
        _embeddingService = embeddingService;
        _config = config.Value;
    }

    /// <summary>
    ///     Perform speaker diarization on audio file
    ///     Returns speaker turns with timestamps and speaker IDs
    /// </summary>
    public virtual async Task<DiarizationResult> DiarizeAsync(
        string audioPath,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting speaker diarization for {AudioPath}", audioPath);

        // Step 1: Detect speech segments using VAD
        var segments = DetectSpeechSegments(audioPath);
        _logger.LogDebug("Detected {Count} speech segments", segments.Count);

        if (segments.Count == 0)
            return new DiarizationResult
            {
                Turns = new List<SpeakerTurn>(),
                SpeakerCount = 0
            };

        // Step 2: Extract embeddings for each segment
        var embeddings = new List<(SpeechSegment Segment, float[] Embedding)>();
        foreach (var segment in segments)
            try
            {
                var embedding = await ExtractSegmentEmbeddingAsync(audioPath, segment, cancellationToken);
                embeddings.Add((segment, embedding));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract embedding for segment {Start}-{End}",
                    segment.StartSeconds, segment.EndSeconds);
            }

        if (embeddings.Count == 0)
            return new DiarizationResult
            {
                Turns = new List<SpeakerTurn>(),
                SpeakerCount = 0
            };

        // Step 3: Cluster embeddings to identify speakers
        var speakerClusters = ClusterSpeakers(embeddings);
        _logger.LogInformation("Identified {Count} speakers", speakerClusters.Keys.Count);

        // Step 4: Create speaker turns
        var turns = new List<SpeakerTurn>();
        foreach (var (segment, embedding) in embeddings)
        {
            var speakerId = FindSpeakerForEmbedding(embedding, speakerClusters);
            turns.Add(new SpeakerTurn
            {
                SpeakerId = speakerId,
                StartSeconds = segment.StartSeconds,
                EndSeconds = segment.EndSeconds,
                Confidence = 1.0 // TODO: Calculate based on cluster distance
            });
        }

        // Step 5: Merge consecutive turns from same speaker
        var mergedTurns = MergeConsecutiveTurns(turns);

        return new DiarizationResult
        {
            Turns = mergedTurns,
            SpeakerCount = speakerClusters.Keys.Count
        };
    }

    /// <summary>
    ///     Simple VAD using energy-based speech detection
    /// </summary>
    private List<SpeechSegment> DetectSpeechSegments(string audioPath)
    {
        using var reader = new AudioFileReader(audioPath);
        ISampleProvider sampleProvider = reader.WaveFormat.Channels == 1
            ? reader
            : new StereoToMonoSampleProvider(reader) { LeftVolume = 0.5f, RightVolume = 0.5f };

        var sampleRate = sampleProvider.WaveFormat.SampleRate;
        var windowSize = sampleRate / 10; // 100ms windows
        var buffer = new float[windowSize];

        var segments = new List<SpeechSegment>();
        SpeechSegment? currentSegment = null;

        double timeSeconds = 0;
        int samplesRead;

        while ((samplesRead = sampleProvider.Read(buffer, 0, buffer.Length)) > 0)
        {
            // Calculate RMS energy for this window
            var rms = Math.Sqrt(buffer.Take(samplesRead).Sum(s => s * s) / samplesRead);

            // Speech detection threshold (calibrated)
            var isSpeech = rms > 0.02; // Adjust threshold as needed

            if (isSpeech)
            {
                if (currentSegment == null)
                    // Start new segment
                    currentSegment = new SpeechSegment
                    {
                        StartSeconds = timeSeconds
                    };
            }
            else
            {
                if (currentSegment != null)
                {
                    // End current segment
                    currentSegment.EndSeconds = timeSeconds;

                    // Only keep segments longer than minimum duration
                    if (currentSegment.Duration >= _config.VoiceEmbedding.MinDurationSeconds)
                        segments.Add(currentSegment);

                    currentSegment = null;
                }
            }

            timeSeconds += (double)samplesRead / sampleRate;
        }

        // Close final segment if still open
        if (currentSegment != null)
        {
            currentSegment.EndSeconds = timeSeconds;
            if (currentSegment.Duration >= _config.VoiceEmbedding.MinDurationSeconds) segments.Add(currentSegment);
        }

        return segments;
    }

    /// <summary>
    ///     Extract embedding for a specific time segment
    /// </summary>
    private async Task<float[]> ExtractSegmentEmbeddingAsync(
        string audioPath,
        SpeechSegment segment,
        CancellationToken cancellationToken)
    {
        // For now, use whole file embedding
        // TODO: Extract only the segment time range
        var embedding = await _embeddingService.ExtractEmbeddingAsync(audioPath, cancellationToken);
        return embedding.Vector;
    }

    /// <summary>
    ///     Agglomerative clustering of speaker embeddings
    ///     Uses cosine similarity threshold for grouping
    /// </summary>
    private Dictionary<string, List<float[]>> ClusterSpeakers(
        List<(SpeechSegment Segment, float[] Embedding)> embeddings)
    {
        const double SimilarityThreshold = 0.75; // Cosine similarity threshold

        var clusters = new Dictionary<string, List<float[]>>();
        var nextSpeakerId = 0;

        foreach (var (segment, embedding) in embeddings)
        {
            string? assignedCluster = null;
            double maxSimilarity = 0;

            // Find best matching cluster
            foreach (var (clusterId, clusterEmbeddings) in clusters)
            {
                // Calculate average similarity to cluster
                var avgSimilarity = clusterEmbeddings
                    .Select(e => _embeddingService.CalculateSimilarity(embedding, e))
                    .Average();

                if (avgSimilarity > maxSimilarity)
                {
                    maxSimilarity = avgSimilarity;
                    assignedCluster = clusterId;
                }
            }

            // Assign to cluster or create new one
            if (maxSimilarity >= SimilarityThreshold && assignedCluster != null)
            {
                clusters[assignedCluster].Add(embedding);
            }
            else
            {
                var newClusterId = $"SPEAKER_{nextSpeakerId:D2}";
                clusters[newClusterId] = new List<float[]> { embedding };
                nextSpeakerId++;
            }
        }

        return clusters;
    }

    /// <summary>
    ///     Find which speaker cluster an embedding belongs to
    /// </summary>
    private string FindSpeakerForEmbedding(
        float[] embedding,
        Dictionary<string, List<float[]>> clusters)
    {
        var bestCluster = "SPEAKER_00";
        double maxSimilarity = 0;

        foreach (var (clusterId, clusterEmbeddings) in clusters)
        {
            var avgSimilarity = clusterEmbeddings
                .Select(e => _embeddingService.CalculateSimilarity(embedding, e))
                .Average();

            if (avgSimilarity > maxSimilarity)
            {
                maxSimilarity = avgSimilarity;
                bestCluster = clusterId;
            }
        }

        return bestCluster;
    }

    /// <summary>
    ///     Merge consecutive turns from the same speaker
    /// </summary>
    private List<SpeakerTurn> MergeConsecutiveTurns(List<SpeakerTurn> turns)
    {
        if (turns.Count == 0)
            return turns;

        var merged = new List<SpeakerTurn>();
        var current = turns[0];

        for (var i = 1; i < turns.Count; i++)
            if (turns[i].SpeakerId == current.SpeakerId)
            {
                // Extend current turn
                current.EndSeconds = turns[i].EndSeconds;
            }
            else
            {
                // Different speaker - save current and start new
                merged.Add(current);
                current = turns[i];
            }

        merged.Add(current);
        return merged;
    }
}

/// <summary>
///     Speech segment detected by VAD
/// </summary>
public class SpeechSegment
{
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public double Duration => EndSeconds - StartSeconds;
}

/// <summary>
///     Speaker turn with time boundaries
/// </summary>
public class SpeakerTurn
{
    public string SpeakerId { get; set; } = string.Empty;
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public double Duration => EndSeconds - StartSeconds;
    public double Confidence { get; set; }
}

/// <summary>
///     Diarization result
/// </summary>
public class DiarizationResult
{
    public List<SpeakerTurn> Turns { get; set; } = new();
    public int SpeakerCount { get; set; }
}