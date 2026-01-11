using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using LucidRAG.Data;
using LucidRAG.Entities;

namespace LucidRAG.Tests.Integration;

/// <summary>
/// Integration tests for retrieval quality across all filtering mechanisms.
/// Tests file types, collections, signals, date ranges, and entities.
/// Requires PostgreSQL database - tests skip if unavailable.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public class RetrievalQualityTests : IDisposable
{
    private readonly RagDocumentsDbContext? _db;
    private readonly bool _dbAvailable;
    private readonly Guid _testCollectionId = Guid.NewGuid();
    private readonly Guid _testCollection2Id = Guid.NewGuid();

    public RetrievalQualityTests()
    {
        try
        {
            _db = TestDbContextFactory.CreateInMemory();
            _dbAvailable = true;
        }
        catch
        {
            _dbAvailable = false;
        }
    }

    public void Dispose()
    {
        _db?.Dispose();
        GC.SuppressFinalize(this);
    }

    private bool SkipIfNoDb() => !_dbAvailable;

    #region File Type Filtering Tests

    [Fact]
    public async Task FilterByFileType_Pdf_ReturnsOnlyPdfDocuments()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        await SeedMixedFileTypeDocumentsAsync();

        // Act
        var documents = await _db!.Documents
            .Where(d => d.CollectionId == _testCollectionId)
            .Where(d => d.OriginalFilename != null && d.OriginalFilename.EndsWith(".pdf"))
            .ToListAsync();

        // Assert
        documents.Should().NotBeEmpty();
        documents.Should().OnlyContain(d => d.OriginalFilename!.EndsWith(".pdf"));
    }

    [Fact]
    public async Task FilterByMimeType_ApplicationPdf_ReturnsPdfOnly()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        await SeedMixedFileTypeDocumentsAsync();

        // Act
        var documents = await _db!.Documents
            .Where(d => d.MimeType == "application/pdf")
            .ToListAsync();

        // Assert
        documents.Should().NotBeEmpty();
        documents.Should().OnlyContain(d => d.MimeType == "application/pdf");
    }

    #endregion

    #region Collection Filtering Tests

    [Fact]
    public async Task FilterByCollection_ReturnsOnlyCollectionDocuments()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        await SeedMultipleCollectionsAsync();

        // Act
        var collection1Docs = await _db!.Documents
            .Where(d => d.CollectionId == _testCollectionId)
            .ToListAsync();

        var collection2Docs = await _db.Documents
            .Where(d => d.CollectionId == _testCollection2Id)
            .ToListAsync();

        // Assert
        collection1Docs.Should().NotBeEmpty();
        collection2Docs.Should().NotBeEmpty();
        collection1Docs.Should().OnlyContain(d => d.CollectionId == _testCollectionId);
        collection2Docs.Should().OnlyContain(d => d.CollectionId == _testCollection2Id);
        collection1Docs.Should().NotIntersectWith(collection2Docs);
    }

    [Fact]
    public async Task FilterByCollection_AllCollections_ReturnsAllDocuments()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        await SeedMultipleCollectionsAsync();

        // Act
        var allDocs = await _db!.Documents
            .Where(d => d.CollectionId == _testCollectionId || d.CollectionId == _testCollection2Id)
            .ToListAsync();

        // Assert
        allDocs.Should().HaveCountGreaterThan(1);
        allDocs.Select(d => d.CollectionId).Distinct().Should().HaveCount(2);
    }

    #endregion

    #region Entity Filtering Tests

    [Fact]
    public async Task FilterByEntityType_Person_ReturnsPersonEntities()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        await SeedExtractedEntitiesAsync();

        // Act
        var personEntities = await _db!.Set<ExtractedEntity>()
            .Where(e => e.EntityType == "person")
            .ToListAsync();

        // Assert
        personEntities.Should().NotBeEmpty();
        personEntities.Should().OnlyContain(e => e.EntityType == "person");
    }

    [Fact]
    public async Task FilterByEntityType_Organization_ReturnsOrgEntities()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        await SeedExtractedEntitiesAsync();

        // Act
        var orgEntities = await _db!.Set<ExtractedEntity>()
            .Where(e => e.EntityType == "organization")
            .ToListAsync();

        // Assert
        orgEntities.Should().NotBeEmpty();
        orgEntities.Should().OnlyContain(e => e.EntityType == "organization");
    }

    #endregion

    #region Signal/Evidence Filtering Tests

    [Fact]
    public async Task FilterBySignal_TranscriptionConfidence_HighQualityOnly()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        await SeedAudioWithSignalsAsync();

        // Act - Filter evidence with high transcription confidence
        var highConfidenceArtifacts = await _db!.EvidenceArtifacts
            .Where(e => e.ArtifactType == EvidenceTypes.Transcript)
            .Where(e => e.Confidence >= 0.8)
            .ToListAsync();

        // Assert
        highConfidenceArtifacts.Should().NotBeEmpty();
        highConfidenceArtifacts.Should().OnlyContain(a => a.Confidence >= 0.8);
    }

    [Fact]
    public async Task FilterBySignal_HasOcrText_ReturnsOcrProcessedContent()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        await SeedOcrProcessedDocumentsAsync();

        // Act
        var ocrArtifacts = await _db!.EvidenceArtifacts
            .Where(e => e.ArtifactType == EvidenceTypes.OcrText)
            .ToListAsync();

        // Assert
        ocrArtifacts.Should().NotBeEmpty();
    }

    [Fact]
    public async Task FilterBySignal_HasTables_ReturnsTableContent()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        await SeedDocumentsWithTablesAsync();

        // Act
        var tableArtifacts = await _db!.EvidenceArtifacts
            .Where(e => e.ArtifactType == EvidenceTypes.TableCsv ||
                        e.ArtifactType == EvidenceTypes.TableJson)
            .ToListAsync();

        // Assert
        tableArtifacts.Should().NotBeEmpty();
    }

    #endregion

    #region Date Range Filtering Tests

    [Fact]
    public async Task FilterByDateRange_RecentDocuments_ReturnsAfterDate()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        await SeedDocumentsWithDatesAsync();
        var cutoffDate = DateTimeOffset.UtcNow.AddDays(-30);

        // Act
        var recentDocs = await _db!.Documents
            .Where(d => d.CreatedAt >= cutoffDate)
            .ToListAsync();

        // Assert
        recentDocs.Should().NotBeEmpty();
        recentDocs.Should().OnlyContain(d => d.CreatedAt >= cutoffDate);
    }

    [Fact]
    public async Task FilterByDateRange_OldDocuments_ReturnsBeforeDate()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        await SeedDocumentsWithDatesAsync();
        var cutoffDate = DateTimeOffset.UtcNow.AddDays(-60);

        // Act
        var oldDocs = await _db!.Documents
            .Where(d => d.CreatedAt < cutoffDate)
            .ToListAsync();

        // Assert
        oldDocs.Should().NotBeEmpty();
        oldDocs.Should().OnlyContain(d => d.CreatedAt < cutoffDate);
    }

    #endregion

    #region Combined Filter Tests

    [Fact]
    public async Task CombinedFilter_CollectionAndFileType_ReturnsIntersection()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        await SeedMixedFileTypeDocumentsAsync();

        // Act
        var filteredDocs = await _db!.Documents
            .Where(d => d.CollectionId == _testCollectionId)
            .Where(d => d.OriginalFilename != null && d.OriginalFilename.EndsWith(".pdf"))
            .ToListAsync();

        // Assert
        filteredDocs.Should().NotBeEmpty();
        filteredDocs.Should().OnlyContain(d =>
            d.CollectionId == _testCollectionId &&
            d.OriginalFilename!.EndsWith(".pdf"));
    }

    [Fact]
    public async Task CombinedFilter_DateRangeAndMimeType_ReturnsIntersection()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        await SeedMixedFileTypeDocumentsAsync();
        var cutoffDate = DateTimeOffset.UtcNow.AddDays(-30);

        // Act
        var docs = await _db!.Documents
            .Where(d => d.MimeType == "application/pdf")
            .Where(d => d.CreatedAt >= cutoffDate)
            .ToListAsync();

        // Assert
        docs.Should().NotBeEmpty();
        docs.Should().OnlyContain(d =>
            d.MimeType == "application/pdf" &&
            d.CreatedAt >= cutoffDate);
    }

    #endregion

    #region Segment Graph Tests

    [Fact]
    public async Task SegmentLinks_SequentialLinks_ExistBetweenAdjacentSegments()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        await SeedSegmentLinksAsync();

        // Act
        var sequentialLinks = await _db!.Set<SegmentLink>()
            .Where(l => l.LinkType == SegmentLinkTypes.Sequential)
            .ToListAsync();

        // Assert
        sequentialLinks.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SegmentLinks_SemanticLinks_ConnectSimilarSegments()
    {
        if (SkipIfNoDb()) return;

        // Arrange
        await SeedSegmentLinksAsync();

        // Act
        var semanticLinks = await _db!.Set<SegmentLink>()
            .Where(l => l.LinkType == SegmentLinkTypes.Semantic)
            .ToListAsync();

        // Assert
        semanticLinks.Should().NotBeEmpty();
        semanticLinks.Should().OnlyContain(l => l.Weight > 0);
    }

    #endregion

    #region Helper Methods

    private Guid _testDocumentId = Guid.NewGuid();
    private Guid _testEntityId = Guid.NewGuid();

    private async Task SeedMixedFileTypeDocumentsAsync()
    {
        var docs = new[]
        {
            CreateDocument("report.pdf", "application/pdf"),
            CreateDocument("notes.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
            CreateDocument("readme.md", "text/markdown"),
            CreateDocument("data.csv", "text/csv"),
            CreateDocument("presentation.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation")
        };

        _db!.Documents.AddRange(docs);
        await _db.SaveChangesAsync();
    }

    private async Task SeedMultipleCollectionsAsync()
    {
        // Collection 1 docs
        _db!.Documents.AddRange(new[]
        {
            CreateDocument("doc1.pdf", collectionId: _testCollectionId),
            CreateDocument("doc2.pdf", collectionId: _testCollectionId)
        });

        // Collection 2 docs
        _db.Documents.AddRange(new[]
        {
            CreateDocument("doc3.pdf", collectionId: _testCollection2Id),
            CreateDocument("doc4.pdf", collectionId: _testCollection2Id)
        });

        await _db.SaveChangesAsync();
    }

    private async Task SeedExtractedEntitiesAsync()
    {
        var entities = new[]
        {
            CreateExtractedEntity("John Smith", "person"),
            CreateExtractedEntity("Jane Doe", "person"),
            CreateExtractedEntity("Acme Corp", "organization"),
            CreateExtractedEntity("Tech Inc", "organization"),
            CreateExtractedEntity("New York", "location")
        };

        _db!.Set<ExtractedEntity>().AddRange(entities);
        await _db.SaveChangesAsync();
    }

    private async Task SeedAudioWithSignalsAsync()
    {
        var entity = CreateRetrievalEntity("Audio Recording", ContentTypes.Audio);
        _db!.Set<RetrievalEntityRecord>().Add(entity);
        await _db.SaveChangesAsync();

        // High confidence transcript
        var highConfidenceArtifact = new EvidenceArtifact
        {
            Id = Guid.NewGuid(),
            EntityId = entity.Id,
            ArtifactType = EvidenceTypes.Transcript,
            MimeType = "text/plain",
            StorageBackend = "inline",
            StoragePath = "inline:transcript",
            Content = "High quality transcript with clear speech.",
            Confidence = 0.95,
            ProducerSource = "whisper"
        };

        // Low confidence transcript
        var lowConfidenceArtifact = new EvidenceArtifact
        {
            Id = Guid.NewGuid(),
            EntityId = entity.Id,
            ArtifactType = EvidenceTypes.Transcript,
            MimeType = "text/plain",
            StorageBackend = "inline",
            StoragePath = "inline:transcript",
            Content = "Low quality transcript with background noise.",
            Confidence = 0.45,
            ProducerSource = "whisper"
        };

        _db.EvidenceArtifacts.AddRange(highConfidenceArtifact, lowConfidenceArtifact);
        await _db.SaveChangesAsync();
    }

    private async Task SeedOcrProcessedDocumentsAsync()
    {
        var entity = CreateRetrievalEntity("Scanned Document", ContentTypes.Image);
        _db!.Set<RetrievalEntityRecord>().Add(entity);
        await _db.SaveChangesAsync();

        var ocrArtifact = new EvidenceArtifact
        {
            Id = Guid.NewGuid(),
            EntityId = entity.Id,
            ArtifactType = EvidenceTypes.OcrText,
            MimeType = "text/plain",
            StorageBackend = "inline",
            StoragePath = "inline:ocr",
            Content = "OCR extracted text from scanned document.",
            Confidence = 0.85,
            ProducerSource = "tesseract"
        };

        _db.EvidenceArtifacts.Add(ocrArtifact);
        await _db.SaveChangesAsync();
    }

    private async Task SeedDocumentsWithTablesAsync()
    {
        var entity = CreateRetrievalEntity("Table Document", ContentTypes.Document);
        _db!.Set<RetrievalEntityRecord>().Add(entity);
        await _db.SaveChangesAsync();

        var tableArtifact = new EvidenceArtifact
        {
            Id = Guid.NewGuid(),
            EntityId = entity.Id,
            ArtifactType = EvidenceTypes.TableCsv,
            MimeType = "text/csv",
            StorageBackend = "inline",
            StoragePath = "inline:table",
            Content = "col1,col2,col3\nval1,val2,val3",
            Confidence = 0.9,
            ProducerSource = "docx_extractor"
        };

        _db.EvidenceArtifacts.Add(tableArtifact);
        await _db.SaveChangesAsync();
    }

    private async Task SeedDocumentsWithDatesAsync()
    {
        // Recent document
        var recentDoc = CreateDocument("recent.pdf");
        recentDoc.CreatedAt = DateTimeOffset.UtcNow.AddDays(-10);

        // Old document
        var oldDoc = CreateDocument("old.pdf");
        oldDoc.CreatedAt = DateTimeOffset.UtcNow.AddDays(-90);

        _db!.Documents.AddRange(recentDoc, oldDoc);
        await _db.SaveChangesAsync();
    }

    private async Task SeedSegmentLinksAsync()
    {
        var links = new[]
        {
            new SegmentLink
            {
                Id = Guid.NewGuid(),
                DocumentId = _testDocumentId,
                SourceSegmentHash = "hash1",
                TargetSegmentHash = "hash2",
                LinkType = SegmentLinkTypes.Sequential,
                Weight = 1.0
            },
            new SegmentLink
            {
                Id = Guid.NewGuid(),
                DocumentId = _testDocumentId,
                SourceSegmentHash = "hash1",
                TargetSegmentHash = "hash3",
                LinkType = SegmentLinkTypes.Semantic,
                Weight = 0.85
            },
            new SegmentLink
            {
                Id = Guid.NewGuid(),
                DocumentId = _testDocumentId,
                SourceSegmentHash = "hash2",
                TargetSegmentHash = "hash3",
                LinkType = SegmentLinkTypes.Heading,
                Weight = 0.7
            }
        };

        _db!.Set<SegmentLink>().AddRange(links);
        await _db.SaveChangesAsync();
    }

    private DocumentEntity CreateDocument(string filename, string? mimeType = null, Guid? collectionId = null)
    {
        return new DocumentEntity
        {
            Id = Guid.NewGuid(),
            Name = Path.GetFileNameWithoutExtension(filename),
            OriginalFilename = filename,
            ContentHash = Guid.NewGuid().ToString("N"),
            MimeType = mimeType ?? "application/pdf",
            CollectionId = collectionId ?? _testCollectionId,
            Status = DocumentStatus.Completed,
            FileSizeBytes = 1024,
            CreatedAt = DateTimeOffset.UtcNow,
            VectorStoreDocId = Guid.NewGuid().ToString()
        };
    }

    private ExtractedEntity CreateExtractedEntity(string name, string entityType)
    {
        return new ExtractedEntity
        {
            Id = Guid.NewGuid(),
            CanonicalName = name,
            EntityType = entityType,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private RetrievalEntityRecord CreateRetrievalEntity(string source, string contentType)
    {
        return new RetrievalEntityRecord
        {
            Id = Guid.NewGuid(),
            Source = source,
            ContentType = contentType,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    #endregion
}
