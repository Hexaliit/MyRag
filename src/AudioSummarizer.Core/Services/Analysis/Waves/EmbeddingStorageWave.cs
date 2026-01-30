using AudioSummarizer.Core.Config;
using AudioSummarizer.Core.Models;
using Mostlylucid.Storage.Core.Abstractions;
using Mostlylucid.Storage.Core.Abstractions.Models;

namespace AudioSummarizer.Core.Services.Analysis.Waves;

/// <summary>
/// Persists voice embeddings to a vector store for later similarity search.
/// Runs after VoiceEmbeddingWave (Priority 30) — only when a vector store is available.
/// Gracefully no-ops if IVectorStore is not registered.
/// </summary>
public class EmbeddingStorageWave : IAudioWave
{
    private readonly ILogger<EmbeddingStorageWave> _logger;
    private readonly IVectorStore? _vectorStore;
    private readonly AudioConfig _config;
    private bool _collectionInitialized;

    public int Priority => 20;
    public string Name => "EmbeddingStorageWave";

    public EmbeddingStorageWave(
        ILogger<EmbeddingStorageWave> logger,
        IOptions<AudioConfig> config,
        IVectorStore? vectorStore = null)
    {
        _logger = logger;
        _config = config.Value;
        _vectorStore = vectorStore;
    }

    public bool ShouldRun(string audioPath, AnalysisContext context)
    {
        // Skip if no vector store registered or persistence disabled
        if (_vectorStore == null)
            return false;

        if (!_config.VoiceEmbedding.PersistEmbeddings)
            return false;

        // Only run if we have a voice embedding from a previous wave
        return context.HasSignal("speaker.embedding_vector");
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string audioPath,
        AnalysisContext context,
        CancellationToken cancellationToken)
    {
        var signals = new List<Signal>();

        try
        {
            var collectionName = _config.VoiceEmbedding.CollectionName;
            var dimension = _config.VoiceEmbedding.EmbeddingDimension;

            // Initialize collection on first use
            if (!_collectionInitialized)
            {
                await _vectorStore!.InitializeAsync(collectionName, new VectorStoreSchema
                {
                    VectorDimension = dimension,
                    DistanceMetric = VectorDistance.Cosine,
                    StoreText = false
                }, cancellationToken);

                _collectionInitialized = true;
            }

            // Read embedding from context (stored as base64 by VoiceEmbeddingWave)
            var vectorBase64 = context.GetValue<string>("speaker.embedding_vector");
            if (string.IsNullOrEmpty(vectorBase64))
            {
                _logger.LogWarning("speaker.embedding_vector signal is empty, skipping storage");
                return signals;
            }

            var bytes = Convert.FromBase64String(vectorBase64);
            var embedding = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, embedding, 0, bytes.Length);

            // Read metadata from context
            var voiceprintId = context.GetValue<string>("speaker.voiceprint_id") ?? Path.GetFileNameWithoutExtension(audioPath);
            var model = context.GetValue<string>("speaker.embedding_model") ?? "ecapa-tdnn";
            var fileHash = context.GetValue<string>("audio.hash.sha256") ?? "";

            var document = new VectorDocument
            {
                Id = voiceprintId,
                Embedding = embedding,
                ParentId = fileHash,
                ContentHash = voiceprintId,
                Metadata = new Dictionary<string, object>
                {
                    ["model"] = model,
                    ["dimension"] = dimension,
                    ["content_type"] = "voice_embedding",
                    ["source_file"] = Path.GetFileName(audioPath)
                }
            };

            await _vectorStore!.UpsertDocumentsAsync(collectionName, [document], cancellationToken);

            signals.Add(new Signal
            {
                Name = "storage.embedding_persisted",
                Value = true,
                Type = SignalType.Metadata,
                Source = Name
            });

            signals.Add(new Signal
            {
                Name = "storage.embedding_collection",
                Value = collectionName,
                Type = SignalType.Metadata,
                Source = Name
            });

            _logger.LogInformation(
                "Persisted voice embedding to collection {Collection}: voiceprint={VoiceprintId}, dim={Dimension}",
                collectionName, voiceprintId, dimension);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist voice embedding to vector store");

            signals.Add(new Signal
            {
                Name = "storage.embedding_persisted",
                Value = false,
                Confidence = 0.0,
                Type = SignalType.Metadata,
                Source = Name
            });
        }

        return signals;
    }
}
