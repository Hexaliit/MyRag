using LucidRAG.Data;
using LucidRAG.Entities;
using Mostlylucid.DocSummarizer.Services;
using Npgsql;
using Pgvector;

namespace LucidRAG.Services;

/// <summary>
///     Manages semantic embeddings for extracted features in pgvector.
///     Enables similarity queries like "yellow" → "gold", "amber", "lemon".
/// </summary>
public class FeatureEmbeddingService(
    RagDocumentsDbContext db,
    IEmbeddingService embeddingService,
    ILogger<FeatureEmbeddingService> logger) : IFeatureEmbeddingService
{
    /// <summary>
    ///     Store or update a feature's embedding.
    /// </summary>
    public async Task<Guid> UpsertFeatureAsync(
        string featureText,
        string featureType,
        Guid? collectionId = null,
        CancellationToken ct = default)
    {
        var normalized = NormalizeText(featureText);

        // Check for existing feature
        var existing = await db.Set<FeatureEmbedding>()
            .FirstOrDefaultAsync(f =>
                    f.NormalizedText == normalized &&
                    f.FeatureType == featureType &&
                    f.CollectionId == collectionId,
                ct);

        if (existing != null)
        {
            // Update occurrence count
            existing.OccurrenceCount++;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return existing.Id;
        }

        // Generate embedding
        var embedding = await embeddingService.EmbedAsync(featureText, ct);

        var feature = new FeatureEmbedding
        {
            Id = Guid.NewGuid(),
            CollectionId = collectionId,
            FeatureText = featureText,
            NormalizedText = normalized,
            FeatureType = featureType,
            Embedding = new Vector(embedding),
            EmbeddingModel = "all-MiniLM-L6-v2",
            DocumentCount = 1,
            OccurrenceCount = 1
        };

        db.Set<FeatureEmbedding>().Add(feature);
        await db.SaveChangesAsync(ct);

        logger.LogDebug("Stored feature embedding: {Feature} ({Type})", featureText, featureType);
        return feature.Id;
    }

    /// <summary>
    ///     Batch upsert multiple features efficiently.
    /// </summary>
    public async Task<int> UpsertFeaturesAsync(
        IEnumerable<(string Text, string Type)> features,
        Guid? collectionId = null,
        CancellationToken ct = default)
    {
        var featureList = features.ToList();
        if (featureList.Count == 0) return 0;

        // Get unique features to process
        var uniqueFeatures = featureList
            .Select(f => (f.Text, f.Type, Normalized: NormalizeText(f.Text)))
            .DistinctBy(f => (f.Normalized, f.Type))
            .ToList();

        // Check for existing features
        var normalizedTexts = uniqueFeatures.Select(f => f.Normalized).ToList();
        var existing = await db.Set<FeatureEmbedding>()
            .Where(f => normalizedTexts.Contains(f.NormalizedText) &&
                        f.CollectionId == collectionId)
            .ToDictionaryAsync(f => (f.NormalizedText, f.FeatureType), ct);

        var toInsert = new List<FeatureEmbedding>();
        var textsToEmbed = new List<(string Text, string Type, string Normalized)>();

        foreach (var feature in uniqueFeatures)
            if (existing.TryGetValue((feature.Normalized, feature.Type), out var existingFeature))
            {
                existingFeature.OccurrenceCount++;
                existingFeature.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                textsToEmbed.Add(feature);
            }

        // Batch embed new features
        if (textsToEmbed.Count > 0)
        {
            var texts = textsToEmbed.Select(f => f.Text).ToList();
            var embeddings = await embeddingService.EmbedBatchAsync(texts, ct);

            for (var i = 0; i < textsToEmbed.Count; i++)
            {
                var feature = textsToEmbed[i];
                toInsert.Add(new FeatureEmbedding
                {
                    Id = Guid.NewGuid(),
                    CollectionId = collectionId,
                    FeatureText = feature.Text,
                    NormalizedText = feature.Normalized,
                    FeatureType = feature.Type,
                    Embedding = new Vector(embeddings[i]),
                    EmbeddingModel = "all-MiniLM-L6-v2",
                    DocumentCount = 1,
                    OccurrenceCount = 1
                });
            }

            db.Set<FeatureEmbedding>().AddRange(toInsert);
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Upserted {New} new features, updated {Existing} existing",
            toInsert.Count, uniqueFeatures.Count - toInsert.Count);

        return toInsert.Count;
    }

    /// <summary>
    ///     Find similar features using pgvector cosine similarity.
    /// </summary>
    public async Task<List<SimilarFeature>> FindSimilarAsync(
        string queryText,
        int topK = 10,
        string? featureType = null,
        Guid? collectionId = null,
        double minSimilarity = 0.5,
        CancellationToken ct = default)
    {
        // Generate query embedding
        var queryEmbedding = await embeddingService.EmbedAsync(queryText, ct);

        // Use raw SQL for pgvector cosine distance operator (<=>)
        var vectorString = $"[{string.Join(",", queryEmbedding)}]";

        var sql = @"
            SELECT ""Id"", ""FeatureText"", ""FeatureType"", ""DocumentCount"", ""OccurrenceCount"",
                   (""Embedding"" <=> $1::vector) as distance
            FROM feature_embeddings
            WHERE ""Embedding"" IS NOT NULL";

        var parameters = new List<object> { vectorString };
        var paramIndex = 2;

        if (featureType != null)
        {
            sql += $" AND \"FeatureType\" = ${paramIndex}";
            parameters.Add(featureType);
            paramIndex++;
        }

        if (collectionId.HasValue)
        {
            sql += $" AND \"CollectionId\" = ${paramIndex}";
            parameters.Add(collectionId.Value);
        }

        sql += $" ORDER BY distance LIMIT {topK * 2}";

        // Execute using raw connection
        var connectionString = db.Database.GetConnectionString();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(sql, connection);
        for (var i = 0; i < parameters.Count; i++) command.Parameters.AddWithValue(parameters[i]);

        var results = new List<SimilarFeature>();
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var distance = reader.GetDouble(5);
            var similarity = 1.0 - distance;

            if (similarity >= minSimilarity)
                results.Add(new SimilarFeature(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    similarity,
                    reader.GetInt32(3),
                    reader.GetInt32(4)));
        }

        return results.Take(topK).ToList();
    }

    /// <summary>
    ///     Find similar features to an existing feature by ID.
    /// </summary>
    public async Task<List<SimilarFeature>> FindSimilarToFeatureAsync(
        Guid featureId,
        int topK = 10,
        bool sameTypeOnly = false,
        CancellationToken ct = default)
    {
        var feature = await db.Set<FeatureEmbedding>()
            .FirstOrDefaultAsync(f => f.Id == featureId, ct);

        if (feature?.Embedding == null)
            return [];

        // Use the feature's text to find similar
        return await FindSimilarAsync(
            feature.FeatureText,
            topK + 1, // +1 to exclude self
            sameTypeOnly ? feature.FeatureType : null,
            feature.CollectionId,
            0.0, // Return all, let caller filter
            ct);
    }

    /// <summary>
    ///     Expand a query using similar features.
    ///     Useful for query expansion: "car" → ["car", "automobile", "vehicle"]
    /// </summary>
    public async Task<List<string>> ExpandQueryAsync(
        string queryText,
        int maxExpansions = 5,
        double minSimilarity = 0.7,
        CancellationToken ct = default)
    {
        var similar = await FindSimilarAsync(
            queryText,
            maxExpansions + 1,
            minSimilarity: minSimilarity,
            ct: ct);

        // Return similar terms excluding exact match
        var normalized = NormalizeText(queryText);
        return similar
            .Where(s => NormalizeText(s.FeatureText) != normalized)
            .Select(s => s.FeatureText)
            .Take(maxExpansions)
            .ToList();
    }

    /// <summary>
    ///     Get feature statistics for a collection.
    /// </summary>
    public async Task<FeatureStats> GetStatsAsync(
        Guid? collectionId = null,
        CancellationToken ct = default)
    {
        var query = db.Set<FeatureEmbedding>().AsQueryable();

        if (collectionId.HasValue)
            query = query.Where(f => f.CollectionId == collectionId);

        var stats = await query
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalFeatures = g.Count(),
                TotalOccurrences = g.Sum(f => f.OccurrenceCount),
                UniqueTypes = g.Select(f => f.FeatureType).Distinct().Count()
            })
            .FirstOrDefaultAsync(ct);

        var typeCounts = await query
            .GroupBy(f => f.FeatureType)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Type, x => x.Count, ct);

        return new FeatureStats(
            stats?.TotalFeatures ?? 0,
            stats?.TotalOccurrences ?? 0,
            stats?.UniqueTypes ?? 0,
            typeCounts);
    }

    private static string NormalizeText(string text)
    {
        return text.ToLowerInvariant().Trim();
    }
}

public interface IFeatureEmbeddingService
{
    Task<Guid> UpsertFeatureAsync(string featureText, string featureType, Guid? collectionId = null,
        CancellationToken ct = default);

    Task<int> UpsertFeaturesAsync(IEnumerable<(string Text, string Type)> features, Guid? collectionId = null,
        CancellationToken ct = default);

    Task<List<SimilarFeature>> FindSimilarAsync(string queryText, int topK = 10, string? featureType = null,
        Guid? collectionId = null, double minSimilarity = 0.5, CancellationToken ct = default);

    Task<List<SimilarFeature>> FindSimilarToFeatureAsync(Guid featureId, int topK = 10, bool sameTypeOnly = false,
        CancellationToken ct = default);

    Task<List<string>> ExpandQueryAsync(string queryText, int maxExpansions = 5, double minSimilarity = 0.7,
        CancellationToken ct = default);

    Task<FeatureStats> GetStatsAsync(Guid? collectionId = null, CancellationToken ct = default);
}

public record SimilarFeature(
    Guid Id,
    string FeatureText,
    string FeatureType,
    double Similarity,
    int DocumentCount,
    int OccurrenceCount);

public record FeatureStats(
    int TotalFeatures,
    int TotalOccurrences,
    int UniqueTypes,
    Dictionary<string, int> TypeCounts);