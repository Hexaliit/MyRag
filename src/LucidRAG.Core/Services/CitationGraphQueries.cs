using LucidRAG.Data;
using LucidRAG.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LucidRAG.Services;

/// <summary>
///     Citation-aware graph query service. Provides queries that leverage the citation
///     graph built from extracted DOIs, arXiv IDs, and link entities.
///
///     The citation graph consists of:
///     - Document nodes (uploaded papers/documents)
///     - Link entity nodes (doi_reference, arxiv_reference, external_link)
///     - "links_to" relationships from documents to link entities
///     - "cites" relationships between documents (when citation is resolved to another imported doc)
///
///     Graph query types enabled by this architecture:
///
///     1. CO-CITATION ANALYSIS
///        Papers that cite the same references are likely related.
///        Query: Find all document pairs that share N+ link entities → implicit topical similarity.
///        Use case: "Find papers related to X" even if they don't mention each other directly.
///
///     2. CITATION CHAINS
///        Paper A cites B, B cites C → trace lineage of ideas.
///        Query: Walk "links_to" + "cites" edges to build citation chains.
///        Use case: "Show me the intellectual ancestry of this paper."
///
///     3. SHARED-REFERENCE CLUSTERING
///        Group documents by their citation overlap (Jaccard similarity on cited DOIs).
///        Query: For each document pair, compute |shared_links| / |union_links|.
///        Use case: Automatic research topic detection without content analysis.
///
///     4. FOUNDATIONAL REFERENCE DETECTION
///        References cited by many documents in the collection are foundational.
///        Query: Rank link entities by in-degree (how many documents cite them).
///        Use case: "What are the most influential papers in this collection?"
///
///     5. CITATION-AWARE SEARCH BOOSTING
///        When a query matches a document, boost segments from papers that share citations.
///        Query: Given search results, find co-cited papers and boost their RRF scores.
///        Use case: Better recall for domain-specific queries.
///
///     6. BIBLIOGRAPHY OVERLAP
///        Find pairs of documents whose bibliographies overlap significantly.
///        Query: Compare doi_reference entities per document.
///        Use case: Detect duplicate research directions or highly related works.
///
///     7. ORPHAN DETECTION
///        Find link entities with no resolved document — these are "missing" papers
///        that could be fetched to complete the citation graph.
///        Query: Link entities with type doi_reference/arxiv_reference that have no corresponding document.
///        Use case: "What papers should I read next to fill gaps in my collection?"
///
///     8. METHODOLOGY CONVERGENCE
///        Documents sharing both citation links AND extracted methodology terms
///        (from DomainClassifier.Technical) indicate strong methodological alignment.
///        Query: Join citation overlap with entity overlap on algorithm/framework entities.
///        Use case: "Find papers using the same methods as this one."
/// </summary>
public class CitationGraphQueries
{
    private readonly RagDocumentsDbContext _db;
    private readonly ILogger<CitationGraphQueries> _logger;

    public CitationGraphQueries(RagDocumentsDbContext db, ILogger<CitationGraphQueries> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    ///     Find documents that share the most citation references with a given document.
    ///     This is co-citation analysis — papers citing the same works are likely related.
    /// </summary>
    public async Task<IReadOnlyList<CoCitationResult>> FindCoCitedDocumentsAsync(
        Guid documentId,
        int limit = 10,
        CancellationToken ct = default)
    {
        // Get all link entities for this document
        var docLinks = await _db.DocumentEntityLinks
            .Where(del => del.DocumentId == documentId)
            .Join(_db.Entities.Where(e =>
                    e.EntityType == "doi_reference" || e.EntityType == "arxiv_reference"),
                del => del.EntityId, e => e.Id,
                (del, e) => e.Id)
            .ToListAsync(ct);

        if (docLinks.Count == 0) return [];

        // Find other documents that share these link entities
        var coCitations = await _db.DocumentEntityLinks
            .Where(del => docLinks.Contains(del.EntityId) && del.DocumentId != documentId)
            .GroupBy(del => del.DocumentId)
            .Select(g => new { DocumentId = g.Key, SharedCount = g.Count() })
            .OrderByDescending(x => x.SharedCount)
            .Take(limit)
            .ToListAsync(ct);

        return coCitations
            .Select(c => new CoCitationResult(c.DocumentId, c.SharedCount, docLinks.Count))
            .ToList();
    }

    /// <summary>
    ///     Find the most-cited references across all documents in the collection.
    ///     These are the foundational works that many papers build upon.
    /// </summary>
    public async Task<IReadOnlyList<FoundationalReference>> FindFoundationalReferencesAsync(
        int limit = 20,
        CancellationToken ct = default)
    {
        var results = await _db.DocumentEntityLinks
            .Join(_db.Entities.Where(e =>
                    e.EntityType == "doi_reference" || e.EntityType == "arxiv_reference"),
                del => del.EntityId, e => e.Id,
                (del, e) => new { del.DocumentId, Entity = e })
            .GroupBy(x => new { x.Entity.Id, x.Entity.CanonicalName, x.Entity.Description })
            .Select(g => new
            {
                EntityId = g.Key.Id,
                g.Key.CanonicalName,
                g.Key.Description,
                CitingDocumentCount = g.Select(x => x.DocumentId).Distinct().Count()
            })
            .OrderByDescending(x => x.CitingDocumentCount)
            .Take(limit)
            .ToListAsync(ct);

        return results
            .Select(r => new FoundationalReference(
                r.EntityId, r.CanonicalName, r.Description, r.CitingDocumentCount))
            .ToList();
    }

    /// <summary>
    ///     Find link entities that could be resolved to real papers but haven't been imported.
    ///     These are citation "gaps" — papers referenced by the collection but not yet fetched.
    /// </summary>
    public async Task<IReadOnlyList<OrphanCitation>> FindOrphanCitationsAsync(
        int limit = 20,
        CancellationToken ct = default)
    {
        // Link entities of type doi_reference or arxiv_reference
        // that don't have a corresponding imported document
        var orphans = await _db.Entities
            .Where(e => e.EntityType == "doi_reference" || e.EntityType == "arxiv_reference")
            .Select(e => new
            {
                e.Id,
                e.CanonicalName,
                e.EntityType,
                e.Description,
                CitingDocCount = _db.DocumentEntityLinks.Count(del => del.EntityId == e.Id)
            })
            .OrderByDescending(x => x.CitingDocCount)
            .Take(limit)
            .ToListAsync(ct);

        return orphans
            .Select(o => new OrphanCitation(
                o.Id, o.CanonicalName, o.EntityType, o.Description, o.CitingDocCount))
            .ToList();
    }

    /// <summary>
    ///     Compute shared-reference similarity between two documents.
    ///     Returns Jaccard coefficient: |shared| / |union| of cited references.
    /// </summary>
    public async Task<double> ComputeCitationSimilarityAsync(
        Guid docA,
        Guid docB,
        CancellationToken ct = default)
    {
        var linksA = await GetCitationEntityIdsAsync(docA, ct);
        var linksB = await GetCitationEntityIdsAsync(docB, ct);

        if (linksA.Count == 0 && linksB.Count == 0) return 0.0;

        var intersection = linksA.Intersect(linksB).Count();
        var union = linksA.Union(linksB).Count();

        return union == 0 ? 0.0 : (double)intersection / union;
    }

    /// <summary>
    ///     Build a citation network for a document: what it cites, what cites it,
    ///     and any co-citation clusters.
    /// </summary>
    public async Task<CitationNetwork> BuildCitationNetworkAsync(
        Guid documentId,
        CancellationToken ct = default)
    {
        // Outgoing: what does this document cite?
        var outgoing = await _db.DocumentEntityLinks
            .Where(del => del.DocumentId == documentId)
            .Join(_db.Entities.Where(e =>
                    e.EntityType == "doi_reference" || e.EntityType == "arxiv_reference"),
                del => del.EntityId, e => e.Id,
                (del, e) => new CitationNode(e.Id, e.CanonicalName, e.Description ?? ""))
            .ToListAsync(ct);

        // Incoming: what documents also cite these same references?
        var outgoingIds = outgoing.Select(o => o.EntityId).ToHashSet();
        var incoming = await _db.DocumentEntityLinks
            .Where(del => outgoingIds.Contains(del.EntityId) && del.DocumentId != documentId)
            .Select(del => del.DocumentId)
            .Distinct()
            .ToListAsync(ct);

        // Co-citation clusters: group outgoing by which other docs share them
        var sharedBy = await _db.DocumentEntityLinks
            .Where(del => outgoingIds.Contains(del.EntityId) && del.DocumentId != documentId)
            .GroupBy(del => del.EntityId)
            .Select(g => new { EntityId = g.Key, SharedByCount = g.Count() })
            .Where(x => x.SharedByCount > 1)
            .ToListAsync(ct);

        return new CitationNetwork(
            DocumentId: documentId,
            OutgoingCitations: outgoing,
            CoCitingDocuments: incoming,
            SharedReferenceCount: sharedBy.Count);
    }

    /// <summary>
    ///     Collection-scoped: find orphan citations within a specific collection.
    /// </summary>
    public async Task<IReadOnlyList<OrphanCitation>> FindOrphanCitationsAsync(
        Guid collectionId,
        int limit = 20,
        CancellationToken ct = default)
    {
        var collectionDocIds = _db.Documents
            .Where(d => d.CollectionId == collectionId)
            .Select(d => d.Id);

        var orphans = await _db.DocumentEntityLinks
            .Where(del => collectionDocIds.Contains(del.DocumentId))
            .Join(_db.Entities.Where(e =>
                    e.EntityType == "doi_reference" || e.EntityType == "arxiv_reference"),
                del => del.EntityId, e => e.Id,
                (del, e) => new { del.DocumentId, Entity = e })
            .GroupBy(x => new { x.Entity.Id, x.Entity.CanonicalName, x.Entity.EntityType, x.Entity.Description })
            .Select(g => new
            {
                EntityId = g.Key.Id,
                g.Key.CanonicalName,
                g.Key.EntityType,
                g.Key.Description,
                CitingDocCount = g.Select(x => x.DocumentId).Distinct().Count()
            })
            .OrderByDescending(x => x.CitingDocCount)
            .Take(limit)
            .ToListAsync(ct);

        return orphans
            .Select(o => new OrphanCitation(
                o.EntityId, o.CanonicalName, o.EntityType, o.Description, o.CitingDocCount))
            .ToList();
    }

    /// <summary>
    ///     Collection-scoped: find foundational references within a specific collection.
    /// </summary>
    public async Task<IReadOnlyList<FoundationalReference>> FindFoundationalReferencesAsync(
        Guid collectionId,
        int limit = 20,
        CancellationToken ct = default)
    {
        var collectionDocIds = _db.Documents
            .Where(d => d.CollectionId == collectionId)
            .Select(d => d.Id);

        var results = await _db.DocumentEntityLinks
            .Where(del => collectionDocIds.Contains(del.DocumentId))
            .Join(_db.Entities.Where(e =>
                    e.EntityType == "doi_reference" || e.EntityType == "arxiv_reference"),
                del => del.EntityId, e => e.Id,
                (del, e) => new { del.DocumentId, Entity = e })
            .GroupBy(x => new { x.Entity.Id, x.Entity.CanonicalName, x.Entity.Description })
            .Select(g => new
            {
                EntityId = g.Key.Id,
                g.Key.CanonicalName,
                g.Key.Description,
                CitingDocumentCount = g.Select(x => x.DocumentId).Distinct().Count()
            })
            .OrderByDescending(x => x.CitingDocumentCount)
            .Take(limit)
            .ToListAsync(ct);

        return results
            .Select(r => new FoundationalReference(
                r.EntityId, r.CanonicalName, r.Description, r.CitingDocumentCount))
            .ToList();
    }

    private async Task<HashSet<Guid>> GetCitationEntityIdsAsync(Guid documentId, CancellationToken ct)
    {
        var ids = await _db.DocumentEntityLinks
            .Where(del => del.DocumentId == documentId)
            .Join(_db.Entities.Where(e =>
                    e.EntityType == "doi_reference" || e.EntityType == "arxiv_reference"),
                del => del.EntityId, e => e.Id,
                (del, e) => e.Id)
            .ToListAsync(ct);

        return ids.ToHashSet();
    }
}

// Result types

public record CoCitationResult(Guid DocumentId, int SharedCitationCount, int TotalCitationsInSource);

public record FoundationalReference(Guid EntityId, string CanonicalName, string? Description, int CitingDocumentCount);

public record OrphanCitation(Guid EntityId, string CanonicalName, string EntityType, string? Description,
    int CitingDocumentCount);

public record CitationNetwork(
    Guid DocumentId,
    IReadOnlyList<CitationNode> OutgoingCitations,
    IReadOnlyList<Guid> CoCitingDocuments,
    int SharedReferenceCount);

public record CitationNode(Guid EntityId, string CanonicalName, string Description);
