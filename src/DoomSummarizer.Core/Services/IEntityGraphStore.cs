using DoomSummarizer.Models;

namespace DoomSummarizer.Services;

/// <summary>
///     Abstraction for the entity knowledge graph store.
///     Manages entities, mentions, relationships, entity embeddings, entity profiles, and statistics.
///     Item-level vector similarity search is handled separately by IVectorStore (Storage.Core).
/// </summary>
public interface IEntityGraphStore : IAsyncDisposable
{
    /// <summary>Initialize the store (create tables, indexes, etc.).</summary>
    Task InitializeAsync();

    // --- Entity CRUD ---

    /// <summary>Upsert an entity node, incrementing mention count and updating last_seen.</summary>
    Task UpsertEntityAsync(string id, string name, string type, double confidence, float[]? embedding = null);

    /// <summary>Record an entity→item mention with confidence and optional context.</summary>
    Task UpsertEntityMentionAsync(string entityId, string itemId, double confidence, string? context = null);

    /// <summary>Upsert a co-occurrence relationship between two entities.</summary>
    Task UpsertRelationshipAsync(string sourceId, string targetId, string relType = "co_occurs");

    // --- Entity Retrieval ---

    /// <summary>Get top entities by mention count with optional type/freshness filters.</summary>
    Task<List<GraphEntity>> GetTopEntitiesAsync(int limit = 20, string? type = null, int? daysBack = null);

    /// <summary>Get relationships for an entity.</summary>
    Task<List<GraphRelationship>> GetRelationshipsAsync(string entityId);

    /// <summary>Get articles that mention a specific entity.</summary>
    Task<List<(string itemId, string title, string? url, double confidence)>>
        GetArticlesForEntityAsync(string entityId);

    /// <summary>Find entities similar to a query embedding using HNSW.</summary>
    Task<List<(GraphEntity entity, float similarity)>> FindSimilarEntitiesAsync(float[] queryEmbedding, int topK = 10);

    // --- Entity Embeddings (cached text embeddings for profile computation) ---

    /// <summary>Get cached entity text embeddings by entity IDs.</summary>
    Task<Dictionary<string, float[]>> GetEntityEmbeddingsAsync(IEnumerable<string> entityIds);

    /// <summary>Batch update entity text embeddings.</summary>
    Task UpdateEntityEmbeddingsBatchAsync(Dictionary<string, float[]> embeddings);

    // --- Entity Profiles (per-item TF×IDF×confidence weighted entity fingerprints) ---

    /// <summary>Upsert an item's entity profile embedding.</summary>
    Task UpsertItemEntityProfileAsync(string itemId, float[] entityProfile);

    /// <summary>Find documents with similar entity profiles using HNSW.</summary>
    Task<List<(string itemId, string title, float similarity)>> FindRelatedByEntityProfileAsync(
        float[] queryEntityProfile, int topK = 5, float minSimilarity = 0.3f);

    /// <summary>Check if any items have entity profiles computed.</summary>
    Task<bool> HasEntityProfilesAsync();

    /// <summary>Get entity profiles for specific item IDs.</summary>
    Task<Dictionary<string, float[]>> GetEntityProfilesAsync(IEnumerable<string> itemIds);

    /// <summary>Get item IDs that have entity mentions but no profile yet.</summary>
    Task<List<string>> GetItemsWithoutEntityProfilesAsync(int limit = 100);

    // --- Entity-Item Lookups (for profile computation) ---

    /// <summary>Get document counts per entity for IDF computation.</summary>
    Task<Dictionary<string, int>> GetEntityDocCountsAsync();

    /// <summary>Get total number of unique documents with entity mentions (N for IDF).</summary>
    Task<int> GetTotalDocsWithEntitiesAsync();

    /// <summary>Get entities for a specific item.</summary>
    Task<List<(string entityId, string name, float confidence, int mentions)>> GetEntitiesForItemAsync(string itemId);

    /// <summary>Get entities for a batch of items.</summary>
    Task<List<(string itemId, string entityId, string name, string type, float confidence, int mentions)>>
        GetEntitiesForItemsAsync(IEnumerable<string> itemIds);

    // --- Statistics & Maintenance ---

    /// <summary>Get graph statistics (entity count, relationship count, mention count, item count).</summary>
    Task<(int entities, int relationships, int mentions, int itemEmbeddings)> GetStatsAsync();

    /// <summary>Delete all data (full reset).</summary>
    Task ClearAllAsync();

    /// <summary>Cleanup old data by retention policy.</summary>
    Task CleanupAsync(int retentionDays);
}