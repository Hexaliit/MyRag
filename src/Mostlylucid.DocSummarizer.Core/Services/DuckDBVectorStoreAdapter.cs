using System.Text.Json;
using Mostlylucid.DocSummarizer.Models;
using Mostlylucid.Storage.Core.Abstractions;
using Mostlylucid.Storage.Core.Abstractions.Models;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
///     Adapter wrapping Storage.Core's DuckDBVectorStore (IVectorStore) to implement
///     DocSummarizer.Core's IVectorStore interface. Maps Segment ↔ VectorDocument models.
/// </summary>
public class DuckDBVectorStoreAdapter : IVectorStore
{
    private readonly Storage.Core.Abstractions.IVectorStore _inner;

    public DuckDBVectorStoreAdapter(Storage.Core.Abstractions.IVectorStore inner)
    {
        _inner = inner;
    }

    public bool IsPersistent => true;

    public async Task InitializeAsync(string collectionName, int vectorSize, CancellationToken ct = default)
    {
        var schema = new VectorStoreSchema
        {
            VectorDimension = vectorSize,
            DistanceMetric = VectorDistance.Cosine,
            StoreText = false, // Privacy: text stays in evidence repo
            MetadataFields =
            [
                new MetadataField { Name = "type", Type = MetadataFieldType.String },
                new MetadataField { Name = "index", Type = MetadataFieldType.Integer },
                new MetadataField { Name = "salience", Type = MetadataFieldType.Float },
                new MetadataField { Name = "section", Type = MetadataFieldType.String }
            ]
        };
        await _inner.InitializeAsync(collectionName, schema, ct);
    }

    public async Task<bool> HasDocumentAsync(string collectionName, string docId, CancellationToken ct = default)
    {
        // DocSummarizer uses sanitized doc ID as prefix for segment IDs
        // Check by looking for any document with this parent ID
        var docs = await _inner.GetAllDocumentsAsync(collectionName, SanitizeDocId(docId), ct);
        return docs.Count > 0;
    }

    public async Task UpsertSegmentsAsync(string collectionName, IEnumerable<Segment> segments,
        CancellationToken ct = default)
    {
        var docs = segments.Where(s => s.Embedding != null).Select(SegmentToDocument).ToList();
        if (docs.Count > 0)
            await _inner.UpsertDocumentsAsync(collectionName, docs, ct);
    }

    public async Task<List<Segment>> SearchAsync(string collectionName, float[] queryEmbedding, int topK,
        string? docId = null, CancellationToken ct = default)
    {
        var query = new VectorSearchQuery
        {
            QueryEmbedding = queryEmbedding,
            TopK = topK,
            IncludeDocument = true,
            IncludeEmbedding = true,
            ParentId = docId != null ? SanitizeDocId(docId) : null
        };

        var results = await _inner.SearchAsync(collectionName, query, ct);
        return results.Select(r => ResultToSegment(r, r.Score)).ToList();
    }

    public async Task<List<Segment>> GetDocumentSegmentsAsync(string collectionName, string docId,
        CancellationToken ct = default)
    {
        var docs = await _inner.GetAllDocumentsAsync(collectionName, SanitizeDocId(docId), ct);
        return docs.Select(d => DocumentToSegment(d)).ToList();
    }

    public Task DeleteCollectionAsync(string collectionName, CancellationToken ct = default)
    {
        return _inner.DeleteCollectionAsync(collectionName, ct);
    }

    public async Task DeleteDocumentAsync(string collectionName, string docId, CancellationToken ct = default)
    {
        // Delete all segments for this parent document
        var sanitized = SanitizeDocId(docId);
        var docs = await _inner.GetAllDocumentsAsync(collectionName, sanitized, ct);
        foreach (var doc in docs)
            await _inner.DeleteDocumentAsync(collectionName, doc.Id, ct);
    }

    public async Task<Dictionary<string, Segment>> GetSegmentsByHashAsync(string collectionName,
        IEnumerable<string> contentHashes, CancellationToken ct = default)
    {
        var hashList = contentHashes.ToList();
        if (hashList.Count == 0)
            return new Dictionary<string, Segment>();

        var docsByHash = await _inner.GetDocumentsByHashAsync(collectionName, hashList, ct);
        return docsByHash.ToDictionary(
            kvp => kvp.Key,
            kvp => DocumentToSegment(kvp.Value));
    }

    public async Task RemoveStaleSegmentsAsync(string collectionName, string docId,
        IEnumerable<string> validContentHashes, CancellationToken ct = default)
    {
        await _inner.RemoveStaleDocumentsAsync(collectionName, SanitizeDocId(docId), validContentHashes, ct);
    }

    public async Task UpdateDomainMetadataAsync(string collectionName, IEnumerable<Segment> segments,
        CancellationToken ct = default)
    {
        // Re-upsert segments with updated metadata (DuckDB replaces on same ID)
        var docs = segments.Where(s => s.Embedding != null).Select(SegmentToDocument).ToList();
        if (docs.Count > 0)
            await _inner.UpsertDocumentsAsync(collectionName, docs, ct);
    }

    public async Task<DocumentSummary?> GetCachedSummaryAsync(string collectionName, string evidenceHash,
        CancellationToken ct = default)
    {
        var cached = await _inner.GetCachedSummaryAsync(collectionName, evidenceHash, ct);
        if (cached == null) return null;

        try
        {
            return JsonSerializer.Deserialize<DocumentSummary>(cached.Summary);
        }
        catch
        {
            return null;
        }
    }

    public async Task CacheSummaryAsync(string collectionName, string evidenceHash, DocumentSummary summary,
        CancellationToken ct = default)
    {
        var cached = new CachedSummary
        {
            DocumentId = evidenceHash,
            Summary = JsonSerializer.Serialize(summary),
            CachedAt = DateTimeOffset.UtcNow
        };
        await _inner.CacheSummaryAsync(collectionName, cached, ct);
    }

    public ValueTask DisposeAsync()
    {
        return _inner.DisposeAsync();
    }

    public void Dispose()
    {
        _inner.Dispose();
    }

    // === Mapping helpers ===

    private static VectorDocument SegmentToDocument(Segment segment)
    {
        var metadata = new Dictionary<string, object>
        {
            ["type"] = segment.Type.ToString(),
            ["index"] = segment.Index,
            ["salience"] = segment.SalienceScore,
            ["startChar"] = segment.StartChar,
            ["endChar"] = segment.EndChar
        };

        if (!string.IsNullOrEmpty(segment.SectionTitle))
            metadata["section"] = segment.SectionTitle;
        if (!string.IsNullOrEmpty(segment.HeadingPath))
            metadata["headingPath"] = segment.HeadingPath;
        if (segment.HeadingLevel > 0)
            metadata["headingLevel"] = segment.HeadingLevel;
        if (segment.PageNumber.HasValue)
            metadata["pageNumber"] = segment.PageNumber.Value;
        if (segment.LineNumber.HasValue)
            metadata["lineNumber"] = segment.LineNumber.Value;
        if (!string.IsNullOrEmpty(segment.DomainDetected))
        {
            metadata["domainDetected"] = segment.DomainDetected;
            metadata["domainConfidence"] = segment.DomainConfidence;
        }
        if (segment.DomainEntities is { Count: > 0 })
            metadata["domainEntities"] = string.Join("|", segment.DomainEntities);
        if (!string.IsNullOrEmpty(segment.DomainSignalsJson))
            metadata["domainSignalsJson"] = segment.DomainSignalsJson;

        // Extract sanitized doc ID from segment ID (format: {docId}_{typePrefix}_{index})
        var parentId = ExtractParentId(segment.Id);

        return new VectorDocument
        {
            Id = segment.Id,
            Embedding = segment.Embedding!,
            ParentId = parentId,
            ContentHash = segment.ContentHash,
            Text = null, // Privacy: no text in vector store
            Metadata = metadata
        };
    }

    private static Segment DocumentToSegment(VectorDocument doc)
    {
        var meta = doc.Metadata;

        var typeStr = meta.TryGetValue("type", out var t) ? t.ToString() ?? "Sentence" : "Sentence";
        var segType = Enum.TryParse<SegmentType>(typeStr, true, out var parsed) ? parsed : SegmentType.Sentence;
        var index = GetMetadataInt(meta, "index");
        var startChar = GetMetadataInt(meta, "startChar");
        var endChar = GetMetadataInt(meta, "endChar");

        var segment = new Segment(
            doc.ParentId ?? "",
            doc.Text ?? "", // Text will be hydrated from evidence repo
            segType,
            index,
            startChar,
            endChar,
            doc.ContentHash)
        {
            SectionTitle = meta.TryGetValue("section", out var sec) ? sec.ToString() ?? "" : "",
            HeadingPath = meta.TryGetValue("headingPath", out var hp) ? hp.ToString() ?? "" : "",
            HeadingLevel = GetMetadataInt(meta, "headingLevel"),
            PageNumber = meta.ContainsKey("pageNumber") ? GetMetadataInt(meta, "pageNumber") : null,
            LineNumber = meta.ContainsKey("lineNumber") ? GetMetadataInt(meta, "lineNumber") : null,
            SalienceScore = GetMetadataDouble(meta, "salience"),
            Embedding = doc.Embedding,
            DomainDetected = meta.TryGetValue("domainDetected", out var dd) ? dd.ToString() : null,
            DomainConfidence = GetMetadataDouble(meta, "domainConfidence"),
            DomainEntities = meta.TryGetValue("domainEntities", out var de)
                ? de.ToString()?.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList()
                : null,
            DomainSignalsJson = meta.TryGetValue("domainSignalsJson", out var dsj) ? dsj.ToString() : null
        };

        return segment;
    }

    private static Segment ResultToSegment(VectorSearchResult result, double score)
    {
        var segment = result.Document != null
            ? DocumentToSegment(result.Document)
            : new Segment("", "", SegmentType.Sentence, 0, 0, 0);

        segment.QuerySimilarity = score;
        return segment;
    }

    private static string ExtractParentId(string segmentId)
    {
        // Segment ID format: {sanitizedDocId}_{typePrefix}_{index}
        // Find the last two underscore-separated parts and strip them
        var lastUnderscore = segmentId.LastIndexOf('_');
        if (lastUnderscore <= 0) return segmentId;

        var beforeLast = segmentId.LastIndexOf('_', lastUnderscore - 1);
        return beforeLast > 0 ? segmentId[..beforeLast] : segmentId;
    }

    private static string SanitizeDocId(string docId)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in docId)
            if (char.IsLetterOrDigit(c) || c == '_')
                sb.Append(c);
            else if (c == '.' || c == '-' || c == ' ')
                sb.Append('_');
        return sb.ToString().ToLowerInvariant();
    }

    private static int GetMetadataInt(Dictionary<string, object> meta, string key)
    {
        if (!meta.TryGetValue(key, out var val)) return 0;
        if (val is int i) return i;
        if (val is long l) return (int)l;
        if (val is JsonElement je && je.TryGetInt32(out var ji)) return ji;
        if (int.TryParse(val.ToString(), out var parsed)) return parsed;
        return 0;
    }

    private static double GetMetadataDouble(Dictionary<string, object> meta, string key)
    {
        if (!meta.TryGetValue(key, out var val)) return 0.0;
        if (val is double d) return d;
        if (val is float f) return f;
        if (val is JsonElement je && je.TryGetDouble(out var jd)) return jd;
        if (double.TryParse(val.ToString(), out var parsed)) return parsed;
        return 0.0;
    }
}
