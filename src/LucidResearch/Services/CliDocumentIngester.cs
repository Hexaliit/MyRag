using System.Text.Json;
using System.Threading.Channels;
using LucidRAG.Data;
using LucidRAG.Entities;
using LucidRAG.Services;
using LucidRAG.UltraResearch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.Summarizer.Core.Utilities;

namespace LucidResearch.Services;

/// <summary>
///     Real document ingester for the standalone TUI.
///     Uses the DocSummarizer pipeline to chunk, embed, extract entities,
///     and store in the vector index — identical to the CLI and web app paths.
/// </summary>
public class DocumentIngester : IDocumentIngester
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DocumentIngester> _logger;
    private int _ingestedCount;

    public DocumentIngester(IServiceScopeFactory scopeFactory, ILogger<DocumentIngester> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<DocumentIngestResult> IngestAsync(string filePath, Guid collectionId, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("File not found for ingestion: {FilePath}", filePath);
            return new DocumentIngestResult(false, $"File not found: {filePath}");
        }

        var filename = Path.GetFileName(filePath);
        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();
            var summarizer = scope.ServiceProvider.GetRequiredService<IDocumentSummarizer>();

            // XxHash64 content hash — 10x faster than SHA256, sufficient for deduplication
            string contentHash;
            await using (var fileStream = File.OpenRead(filePath))
            {
                contentHash = ContentHasher.ComputeHash(fileStream);
            }

            // Check for duplicates
            var existing = await db.Documents
                .FirstOrDefaultAsync(d => d.ContentHash == contentHash && d.CollectionId == collectionId, ct);
            if (existing != null)
            {
                _logger.LogDebug("Document already indexed: {FileName} (hash {Hash})", filename, contentHash);
                return new DocumentIngestResult(true, "Already indexed", existing.Id, existing.SegmentCount);
            }

            // Create document entity
            var documentId = Guid.NewGuid();
            var document = new DocumentEntity
            {
                Id = documentId,
                CollectionId = collectionId,
                Name = Path.GetFileNameWithoutExtension(filename),
                OriginalFilename = filename,
                ContentHash = contentHash,
                FilePath = filePath,
                FileSizeBytes = new FileInfo(filePath).Length,
                MimeType = GetMimeType(extension),
                Status = DocumentStatus.Processing
            };

            db.Documents.Add(document);
            await db.SaveChangesAsync(ct);

            // Process through DocSummarizer pipeline (chunk, embed, entity extract)
            var progressChannel = Channel.CreateUnbounded<ProgressUpdate>();
            var processTask = summarizer.SummarizeFileAsync(
                filePath,
                progressChannel.Writer,
                cancellationToken: ct);

            // Drain progress channel (log only — TUI has its own polling)
            await foreach (var update in progressChannel.Reader.ReadAllAsync(ct))
            {
                _logger.LogDebug("[{Stage}] {Message} ({Percent:F0}%)",
                    update.Stage, update.Message, update.PercentComplete);
                if (update.Type == ProgressType.Completed) break;
                if (update.Type == ProgressType.Error) throw new Exception(update.Message);
            }

            var result = await processTask;
            var segmentCount = result.Trace.TotalChunks;

            // Update document status
            document.Status = DocumentStatus.Completed;
            document.SegmentCount = segmentCount;
            document.ProcessedAt = DateTimeOffset.UtcNow;

            PropagateFileMetadata(document, filePath);

            await db.SaveChangesAsync(ct);

            // Promote authors from metadata to first-class graph entities
            var entityMatcher = scope.ServiceProvider.GetService<IEntityMatcher>();
            if (entityMatcher != null)
            {
                await PromoteAuthorsToEntitiesAsync(db, entityMatcher, document, collectionId, ct);
                await db.SaveChangesAsync(ct);
            }

            var count = Interlocked.Increment(ref _ingestedCount);
            _logger.LogInformation(
                "Ingested document #{Count}: {FileName} ({Segments} segments, {Size:N0} bytes)",
                count, filename, segmentCount, document.FileSizeBytes);

            return new DocumentIngestResult(true, $"Ingested {filename}", documentId, segmentCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to ingest {FileName}", filename);
            return new DocumentIngestResult(false, $"Ingestion failed: {ex.Message}");
        }
    }

    private static string GetMimeType(string extension) => extension switch
    {
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".md" => "text/markdown",
        ".txt" => "text/plain",
        ".html" => "text/html",
        _ => "application/octet-stream"
    };

    /// <summary>
    ///     Read YAML frontmatter from a markdown file and propagate selected metadata
    ///     to DocumentEntity.Metadata as JSON. Used for venue quality scoring in RRF.
    /// </summary>
    private static void PropagateFileMetadata(DocumentEntity document, string filePath)
    {
        try
        {
            if (!Path.GetExtension(filePath).Equals(".md", StringComparison.OrdinalIgnoreCase))
                return;

            var content = File.ReadAllText(filePath);
            if (!content.StartsWith("---"))
                return;

            var endIdx = content.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (endIdx < 0)
                return;

            var yamlBlock = content[3..endIdx];
            var frontmatter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in yamlBlock.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var colonIdx = line.IndexOf(':');
                if (colonIdx <= 0) continue;

                var key = line[..colonIdx].Trim().ToLowerInvariant();
                var value = line[(colonIdx + 1)..].Trim();

                if (value.Length >= 2 &&
                    ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                    value = value[1..^1];

                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                    frontmatter[key] = value;
            }

            if (frontmatter.Count == 0)
                return;

            var metadataDict = new Dictionary<string, object>();

            if (frontmatter.TryGetValue("venue_quality", out var vq) && double.TryParse(vq, out var venueQuality))
                metadataDict["venue_quality"] = venueQuality;

            if (frontmatter.TryGetValue("citation_count", out var cc) && int.TryParse(cc, out var citationCount))
                metadataDict["citation_count"] = citationCount;

            if (frontmatter.TryGetValue("influential_citations", out var ic) && int.TryParse(ic, out var influential))
                metadataDict["influential_citations"] = influential;

            if (frontmatter.TryGetValue("venue", out var venue) && !string.IsNullOrEmpty(venue))
                metadataDict["venue"] = venue;

            if (frontmatter.TryGetValue("year", out var yr) && int.TryParse(yr, out var year))
                metadataDict["year"] = year;

            if (frontmatter.TryGetValue("authors", out var authorStr) && !string.IsNullOrEmpty(authorStr))
                metadataDict["authors"] = authorStr;

            if (frontmatter.TryGetValue("title", out var titleStr) && !string.IsNullOrEmpty(titleStr))
                metadataDict["title"] = titleStr;

            if (frontmatter.TryGetValue("doi", out var doiStr) && !string.IsNullOrEmpty(doiStr))
                metadataDict["doi"] = doiStr;

            if (frontmatter.TryGetValue("arxiv_id", out var arxivStr) && !string.IsNullOrEmpty(arxivStr))
                metadataDict["arxiv_id"] = arxivStr;

            if (metadataDict.Count > 0)
                document.Metadata = JsonSerializer.Serialize(metadataDict);
        }
        catch
        {
            // Metadata propagation failure shouldn't affect document processing
        }
    }

    /// <summary>
    ///     Promote authors from document metadata to first-class ExtractedEntity records
    ///     with co-authorship relationships. Uses IEntityMatcher for deduplication.
    /// </summary>
    private async Task PromoteAuthorsToEntitiesAsync(
        RagDocumentsDbContext db,
        IEntityMatcher entityMatcher,
        DocumentEntity document,
        Guid collectionId,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrEmpty(document.Metadata)) return;

            var metadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(document.Metadata);
            if (metadata == null || !metadata.TryGetValue("authors", out var authorsElement)) return;

            var authorsStr = authorsElement.GetString();
            if (string.IsNullOrEmpty(authorsStr)) return;

            var authorNames = authorsStr.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(n => n.Length >= 2)
                .ToList();

            if (authorNames.Count == 0) return;

            var authorEntityIds = new List<Guid>();

            foreach (var authorName in authorNames)
            {
                var normalized = entityMatcher.Normalize(authorName, "author");

                // Try to find existing match
                var existing = await entityMatcher.FindMatchAsync(authorName, "author", collectionId, ct);

                Guid entityId;
                if (existing != null)
                {
                    entityId = existing.Id;
                    // Add original name as alias if not already present
                    if (!existing.Aliases.Contains(authorName, StringComparer.OrdinalIgnoreCase))
                    {
                        existing.Aliases = [..existing.Aliases, authorName];
                        existing.UpdatedAt = DateTimeOffset.UtcNow;
                    }
                }
                else
                {
                    // Create new author entity
                    var entity = new ExtractedEntity
                    {
                        Id = Guid.NewGuid(),
                        CanonicalName = normalized,
                        EntityType = "author",
                        Description = $"Author: {authorName}",
                        Aliases = authorName.Equals(normalized, StringComparison.OrdinalIgnoreCase)
                            ? []
                            : [authorName]
                    };
                    db.Entities.Add(entity);
                    entityId = entity.Id;
                }

                authorEntityIds.Add(entityId);

                // Create document-entity link
                var existingLink = await db.DocumentEntityLinks
                    .FirstOrDefaultAsync(l => l.DocumentId == document.Id && l.EntityId == entityId, ct);
                if (existingLink == null)
                {
                    db.DocumentEntityLinks.Add(new DocumentEntityLink
                    {
                        DocumentId = document.Id,
                        EntityId = entityId,
                        MentionCount = 1,
                        SegmentIds = []
                    });
                }
                else
                {
                    existingLink.MentionCount++;
                }
            }

            // Create co-authorship relationships between author pairs
            for (var i = 0; i < authorEntityIds.Count; i++)
            {
                for (var j = i + 1; j < authorEntityIds.Count; j++)
                {
                    var sourceId = authorEntityIds[i];
                    var targetId = authorEntityIds[j];

                    var existingRel = await db.EntityRelationships
                        .FirstOrDefaultAsync(r =>
                            r.SourceEntityId == sourceId && r.TargetEntityId == targetId &&
                            r.RelationshipType == "co_author", ct);

                    if (existingRel == null)
                    {
                        db.EntityRelationships.Add(new EntityRelationship
                        {
                            Id = Guid.NewGuid(),
                            SourceEntityId = sourceId,
                            TargetEntityId = targetId,
                            RelationshipType = "co_author",
                            Strength = (float)(1.0 / authorNames.Count),
                            SourceDocuments = [document.Id]
                        });
                    }
                    else
                    {
                        existingRel.Strength = Math.Max(existingRel.Strength, (float)(1.0 / authorNames.Count));
                        if (!existingRel.SourceDocuments.Contains(document.Id))
                            existingRel.SourceDocuments = [..existingRel.SourceDocuments, document.Id];
                    }
                }
            }

            _logger.LogDebug("Promoted {Count} authors to entities for document {DocumentId}",
                authorEntityIds.Count, document.Id);
        }
        catch (Exception ex)
        {
            // Author promotion failure shouldn't block ingestion
            _logger.LogDebug(ex, "Failed to promote authors for document {DocumentId}", document.Id);
        }
    }
}
