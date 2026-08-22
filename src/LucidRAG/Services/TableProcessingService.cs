using System.Text;
using System.Text.Json;
using LucidRAG.Data;
using LucidRAG.Entities;
using LucidRAG.Core.Services;
using Mostlylucid.DocSummarizer.Core.Models;
using Mostlylucid.DocSummarizer.Core.Services;
using Mostlylucid.DocSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.Summarizer.Core.Pipeline;

namespace LucidRAG.Services;

/// <summary>
///     Processes extracted tables: stores as evidence, creates entities, generates embeddings
///     Integrates tables into the multimodal entity + evidence architecture
/// </summary>
public class TableProcessingService
{
    private readonly RagDocumentsDbContext _dbContext;
    private readonly IEvidenceRepository _evidenceRepo;
    private readonly ILogger<TableProcessingService> _logger;
    private readonly ITableExtractorFactory _tableExtractorFactory;
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorStore _vectorStore;
    private readonly IRetrievalEntityService _retrievalEntityService;

    public TableProcessingService(
        ILogger<TableProcessingService> logger,
        ITableExtractorFactory tableExtractorFactory,
        IEvidenceRepository evidenceRepo,
        RagDocumentsDbContext dbContext,
        IEmbeddingService embeddingService,
        IVectorStore vectorStore,
        IRetrievalEntityService retrievalEntityService)
    {
        _logger = logger;
        _tableExtractorFactory = tableExtractorFactory;
        _evidenceRepo = evidenceRepo;
        _dbContext = dbContext;
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _retrievalEntityService = retrievalEntityService;
    }

    /// <summary>
    ///     Extract tables from document and store as evidence artifacts linked to parent document entity
    /// </summary>
    public async Task<List<TableEntity>> ProcessDocumentTablesAsync(
        string filePath,
        Guid parentEntityId,
        Guid? collectionId,
        TableExtractionOptions? options = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Processing tables from {File} for parent entity {ParentId}",
            Path.GetFileName(filePath), parentEntityId);

        // 1. Extract tables from document
        var extractor = await _tableExtractorFactory.GetExtractorForFileAsync(filePath, ct);
        if (extractor == null)
        {
            _logger.LogWarning("No table extractor available for {File}", filePath);
            return new List<TableEntity>();
        }

        var extractionResult = await extractor.ExtractTablesAsync(filePath, options, ct);

        if (!extractionResult.Success || extractionResult.Tables.Count == 0)
        {
            _logger.LogInformation("No tables found in {File}", Path.GetFileName(filePath));
            return new List<TableEntity>();
        }

        _logger.LogInformation("Extracted {Count} tables from {File}",
            extractionResult.Tables.Count, Path.GetFileName(filePath));

        // 2. Process each table
        var tableEntities = new List<TableEntity>();

        foreach (var table in extractionResult.Tables)
            try
            {
                var tableEntity = await ProcessSingleTableAsync(
                    table, parentEntityId, collectionId, filePath, ct);

                tableEntities.Add(tableEntity);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing table {TableId}", table.Id);
            }

        return tableEntities;
    }

    /// <summary>
    ///     Process a single extracted table: create entity, store evidence, generate embeddings
    /// </summary>
    private async Task<TableEntity> ProcessSingleTableAsync(
        ExtractedTable table,
        Guid parentEntityId,
        Guid? collectionId,
        string sourceFilePath,
        CancellationToken ct)
    {
        // 1. Create table entity in database
        var tableEntity = new RetrievalEntityRecord
        {
            Id = Guid.NewGuid(),
            CollectionId = collectionId,
            ContentType = "table",
            Source = sourceFilePath,
            Title = $"{Path.GetFileNameWithoutExtension(sourceFilePath)} - Table {table.TableNumber}",
            Summary = GenerateTableSummary(table),
            TextContent = GenerateTableTextRepresentation(table),
            ContentConfidence = table.Confidence ?? 1.0,
            SourceModalities = JsonSerializer.Serialize(new[] { "table" }),
            ProcessingState = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["parentEntityId"] = parentEntityId.ToString(), // Store parent link in processing state
                ["extractionMethod"] = table.ExtractionMethod ?? "unknown",
                ["confidence"] = table.Confidence ?? 0.0,
                ["rowCount"] = table.RowCount,
                ["columnCount"] = table.ColumnCount,
                ["pageOrSection"] = table.PageOrSection
            }),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _dbContext.RetrievalEntities.Add(tableEntity);
        await _dbContext.SaveChangesAsync(ct);

        _logger.LogDebug("Created table entity {EntityId} for table {TableId}",
            tableEntity.Id, table.Id);

        // 2. Store table CSV as evidence artifact
        var csvContent = table.ToCsv();
        var csvBytes = Encoding.UTF8.GetBytes(csvContent);

        using var csvStream = new MemoryStream(csvBytes);

        var csvArtifactId = await _evidenceRepo.StoreAsync(
            tableEntity.Id,
            EvidenceTypes.TableCsv,
            csvStream,
            "text/csv",
            metadata: new TableEvidenceMetadata
            {
                TableId = table.Id,
                PageNumber = table.PageOrSection,
                RowCount = table.RowCount,
                ColumnCount = table.ColumnCount,
                ColumnNames = table.ColumnNames,
                HasHeader = table.HasHeader,
                IsHeuristic = false,
                Confidence = table.Confidence ?? 1.0
            },
            ct: ct);

        _logger.LogDebug("Stored table CSV as evidence artifact {ArtifactId}", csvArtifactId);

        // 3. Store table metadata as JSON artifact (optional)
        var metadataJson = JsonSerializer.Serialize(new
        {
            tableId = table.Id,
            sourcePath = table.SourcePath,
            pageOrSection = table.PageOrSection,
            boundingBox = table.BoundingBox,
            rowCount = table.RowCount,
            columnCount = table.ColumnCount,
            hasHeader = table.HasHeader,
            columnNames = table.ColumnNames,
            confidence = table.Confidence,
            extractionMethod = table.ExtractionMethod
        });

        using var metadataStream = new MemoryStream(Encoding.UTF8.GetBytes(metadataJson));

        await _evidenceRepo.StoreAsync(
            tableEntity.Id,
            EvidenceTypes.TableJson,
            metadataStream,
            "application/json",
            metadata: new { type = "table_metadata" },
            ct: ct);

        // 4. Generate embedding and upsert to vector store for retrieval
        try
        {
            await _embeddingService.InitializeAsync(ct);
            var tableText = tableEntity.TextContent; // The markdown table from GenerateTableTextRepresentation
            if (!string.IsNullOrWhiteSpace(tableText))
            {
                var embedding = await _embeddingService.EmbedAsync(tableText, ct);
                var segment = new Segment(
                    tableEntity.Id.ToString("N"), // Use table entity ID as docId for vector store
                    tableText,
                    SegmentType.TableRow,
                    0,
                    0,
                    tableText.Length,
                    HashHelper.ComputeHash(tableText))
                {
                    Embedding = embedding,
                    ContentHash = HashHelper.ComputeHash(tableText),
                    SectionTitle = "Table",
                    SalienceScore = 1.0,
                    PositionWeight = 1.0,
                    ChunkIndex = 0,
                };

                await _vectorStore.UpsertSegmentsAsync("ragdocs", [segment], ct);

                // Also store embedding in entity_embeddings table for graph/retrieval
                await _retrievalEntityService.AddEmbeddingAsync(
                    tableEntity.Id.ToString(),
                    EmbeddingNames.Text,
                    embedding,
                    ct: ct);

                _logger.LogDebug("Generated embedding and upserted table {TableId} to vector store", table.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate embedding for table {TableId}, continuing without vector indexing", table.Id);
        }

        _logger.LogInformation("Processed table {TableId} → Entity {EntityId} with {Rows}×{Cols} cells. Embedding generated and indexed.",
            table.Id, tableEntity.Id, table.RowCount, table.ColumnCount);

        return new TableEntity
        {
            EntityId = tableEntity.Id,
            TableId = table.Id,
            ParentEntityId = parentEntityId,
            CollectionId = collectionId,
            RowCount = table.RowCount,
            ColumnCount = table.ColumnCount,
            CsvArtifactId = csvArtifactId,
            PageOrSection = table.PageOrSection
        };
    }

    /// <summary>
    ///     Generate brief summary of table for Summary field
    /// </summary>
    private string GenerateTableSummary(ExtractedTable table)
    {
        var summary = $"Table with {table.RowCount} rows and {table.ColumnCount} columns";

        if (table.HasHeader && table.ColumnNames != null && table.ColumnNames.Count > 0)
        {
            var columns = string.Join(", ", table.ColumnNames.Take(5));
            summary += $". Columns: {columns}";
            if (table.ColumnNames.Count > 5) summary += $" (+ {table.ColumnNames.Count - 5} more)";
        }

        return summary;
    }

    /// <summary>
    ///     Generate text representation of table for embedding
    ///     Format: "Table from {source} on page {page}: {title}\nColumns: {col1}, {col2}, ...\nData: {preview}"
    /// </summary>
    private string GenerateTableTextRepresentation(ExtractedTable table)
    {
        var sb = new StringBuilder();

        // Source information
        sb.AppendLine($"Table from {Path.GetFileName(table.SourcePath)} on page {table.PageOrSection}");

        // Column names (if available)
        if (table.HasHeader && table.ColumnNames != null && table.ColumnNames.Count > 0)
            sb.AppendLine($"Columns: {string.Join(", ", table.ColumnNames)}");

        // Data preview (first few rows)
        if (table.Rows.Count > 0)
        {
            foreach (var row in table.Rows)
            {
                var rowText = string.Join(" | ", row.Select(cell => cell.Text ?? ""));
                sb.AppendLine(rowText);
            }
        }

        return sb.ToString();
    }
}

/// <summary>
///     Result of table processing
/// </summary>
public class TableEntity
{
    public Guid EntityId { get; init; }
    public required string TableId { get; init; }
    public Guid ParentEntityId { get; init; }
    public Guid? CollectionId { get; init; }
    public int RowCount { get; init; }
    public int ColumnCount { get; init; }
    public Guid CsvArtifactId { get; init; }
    public int PageOrSection { get; init; }
}