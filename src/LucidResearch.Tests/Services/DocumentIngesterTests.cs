using System.Text.Json;
using System.Threading.Channels;
using LucidRAG.Data;
using LucidRAG.Entities;
using LucidRAG.Services;
using LucidRAG.UltraResearch;
using LucidResearch.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.DocSummarizer;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;

namespace LucidResearch.Tests.Services;

public class DocumentIngesterTests : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly string _tempDir;

    public DocumentIngesterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lucidresearch-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.ClearProviders());

        // SQLite file DB for test isolation
        sc.AddDbContext<RagDocumentsDbContext>(options =>
            options.UseSqlite($"Data Source={Path.Combine(_tempDir, "test.db")}"));

        // Fake IDocumentSummarizer that writes progress and returns a result
        sc.AddScoped<IDocumentSummarizer>(_ => new FakeSummarizer());

        // EntityMatcher for author promotion tests
        sc.AddScoped<IEntityMatcher, EntityMatcher>();

        _services = sc.BuildServiceProvider();

        // Ensure DB created
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _services.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { /* cleanup best-effort */ }
        GC.SuppressFinalize(this);
    }

    private DocumentIngester CreateIngester() =>
        new(_services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DocumentIngester>.Instance);

    private string CreateTestFile(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    ///     Create a CollectionEntity in the DB so the FK constraint on DocumentEntity.CollectionId is satisfied.
    /// </summary>
    private async Task<Guid> EnsureCollectionAsync(string name = "test-collection")
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();
        var collection = new CollectionEntity { Id = Guid.NewGuid(), Name = name };
        db.Collections.Add(collection);
        await db.SaveChangesAsync();
        return collection.Id;
    }

    [Fact]
    public async Task IngestAsync_returns_failure_for_missing_file()
    {
        var ingester = CreateIngester();
        var collectionId = await EnsureCollectionAsync();
        var result = await ingester.IngestAsync(
            Path.Combine(_tempDir, "nonexistent.md"),
            collectionId);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("not found");
    }

    [Fact]
    public async Task IngestAsync_succeeds_for_valid_file()
    {
        var ingester = CreateIngester();
        var filePath = CreateTestFile("test.md", "# Test Document\n\nSome content here.");
        var collectionId = await EnsureCollectionAsync();

        var result = await ingester.IngestAsync(filePath, collectionId);

        result.Success.Should().BeTrue(because: result.Message);
        result.DocumentId.Should().NotBeNull();
        result.SegmentCount.Should().Be(5);
    }

    [Fact]
    public async Task IngestAsync_deduplicates_by_content_hash()
    {
        var ingester = CreateIngester();
        var filePath = CreateTestFile("test.md", "# Duplicate Content\n\nSame hash.");
        var collectionId = await EnsureCollectionAsync();

        var result1 = await ingester.IngestAsync(filePath, collectionId);
        var result2 = await ingester.IngestAsync(filePath, collectionId);

        result1.Success.Should().BeTrue(because: result1.Message);
        result2.Success.Should().BeTrue();
        result2.Message.Should().Contain("Already indexed");
        result2.DocumentId.Should().Be(result1.DocumentId);
    }

    [Fact]
    public async Task IngestAsync_allows_same_content_in_different_collections()
    {
        var ingester = CreateIngester();
        var filePath = CreateTestFile("test.md", "# Cross-Collection Test");
        var collectionA = await EnsureCollectionAsync("collection-a");
        var collectionB = await EnsureCollectionAsync("collection-b");

        var result1 = await ingester.IngestAsync(filePath, collectionA);
        var result2 = await ingester.IngestAsync(filePath, collectionB);

        result1.Success.Should().BeTrue(because: result1.Message);
        result2.Success.Should().BeTrue(because: result2.Message);
        Assert.NotEqual(result1.DocumentId, result2.DocumentId);
    }

    [Fact]
    public async Task IngestAsync_creates_document_entity_in_db()
    {
        var ingester = CreateIngester();
        var filePath = CreateTestFile("paper.md", "# Research Paper\n\nContent.");
        var collectionId = await EnsureCollectionAsync();

        var result = await ingester.IngestAsync(filePath, collectionId);

        result.Success.Should().BeTrue(because: result.Message);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();
        var docId = result.DocumentId!.Value;
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == docId);

        doc.Should().NotBeNull();
        doc!.Name.Should().Be("paper");
        doc.OriginalFilename.Should().Be("paper.md");
        doc.CollectionId.Should().Be(collectionId);
        doc.Status.Should().Be(DocumentStatus.Completed);
        doc.SegmentCount.Should().Be(5);
        doc.MimeType.Should().Be("text/markdown");
    }

    [Fact]
    public async Task IngestAsync_propagates_yaml_frontmatter_metadata()
    {
        var ingester = CreateIngester();
        var content = "---\nvenue_quality: 0.85\ncitation_count: 42\nvenue: \"NeurIPS\"\nyear: 2024\n---\n# Paper with Metadata\nContent here.\n";
        var filePath = CreateTestFile("paper-with-meta.md", content);
        var collectionId = await EnsureCollectionAsync();

        var result = await ingester.IngestAsync(filePath, collectionId);

        result.Success.Should().BeTrue(because: result.Message);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();
        var docId = result.DocumentId!.Value;
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == docId);

        doc.Should().NotBeNull();
        doc!.Metadata.Should().NotBeNullOrEmpty();
        doc.Metadata.Should().Contain("venue_quality");
        doc.Metadata.Should().Contain("0.85");
        doc.Metadata.Should().Contain("NeurIPS");
    }

    [Fact]
    public async Task IngestAsync_handles_non_markdown_files()
    {
        var ingester = CreateIngester();
        var filePath = CreateTestFile("doc.txt", "Plain text content");
        var collectionId = await EnsureCollectionAsync();

        var result = await ingester.IngestAsync(filePath, collectionId);

        result.Success.Should().BeTrue(because: result.Message);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();
        var docId = result.DocumentId!.Value;
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == docId);

        doc!.MimeType.Should().Be("text/plain");
        // Non-markdown should have null metadata (frontmatter not extracted)
        doc.Metadata.Should().BeNull();
    }

    // ---- Author Promotion Tests ----

    [Fact]
    public async Task IngestAsync_propagates_authors_in_metadata()
    {
        var ingester = CreateIngester();
        var content = "---\ntitle: \"Test Paper\"\nauthors: \"Alice Smith, Bob Jones\"\nyear: 2024\n---\n# Paper\nContent.\n";
        var filePath = CreateTestFile("authors-meta.md", content);
        var collectionId = await EnsureCollectionAsync();

        var result = await ingester.IngestAsync(filePath, collectionId);

        result.Success.Should().BeTrue(because: result.Message);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == result.DocumentId!.Value);

        doc.Should().NotBeNull();
        doc!.Metadata.Should().NotBeNullOrEmpty();
        doc.Metadata.Should().Contain("authors");
        doc.Metadata.Should().Contain("Alice Smith");
        doc.Metadata.Should().Contain("Bob Jones");
    }

    [Fact]
    public async Task IngestAsync_creates_author_entities()
    {
        var ingester = CreateIngester();
        var content = "---\ntitle: \"Author Entity Test\"\nauthors: \"Alice Smith, Bob Jones\"\nyear: 2024\n---\n# Paper\nContent.\n";
        var filePath = CreateTestFile("author-entities.md", content);
        var collectionId = await EnsureCollectionAsync();

        var result = await ingester.IngestAsync(filePath, collectionId);

        result.Success.Should().BeTrue(because: result.Message);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

        var authorEntities = await db.Entities
            .Where(e => e.EntityType == "author")
            .ToListAsync();

        authorEntities.Should().HaveCount(2);
        authorEntities.Select(e => e.CanonicalName).Should().Contain("alice smith");
        authorEntities.Select(e => e.CanonicalName).Should().Contain("bob jones");
    }

    [Fact]
    public async Task IngestAsync_creates_document_entity_links_for_authors()
    {
        var ingester = CreateIngester();
        var content = "---\ntitle: \"Link Test\"\nauthors: \"Alice Smith, Bob Jones\"\n---\n# Paper\nContent.\n";
        var filePath = CreateTestFile("author-links.md", content);
        var collectionId = await EnsureCollectionAsync();

        var result = await ingester.IngestAsync(filePath, collectionId);

        result.Success.Should().BeTrue(because: result.Message);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

        var links = await db.DocumentEntityLinks
            .Where(del => del.DocumentId == result.DocumentId!.Value)
            .Join(db.Entities, del => del.EntityId, e => e.Id, (del, e) => e)
            .Where(e => e.EntityType == "author")
            .ToListAsync();

        links.Should().HaveCount(2);
    }

    [Fact]
    public async Task IngestAsync_creates_coauthorship_relationships()
    {
        var ingester = CreateIngester();
        var content = "---\ntitle: \"CoAuthor Test\"\nauthors: \"Alice Smith, Bob Jones, Carol Lee\"\n---\n# Paper\nContent.\n";
        var filePath = CreateTestFile("coauthor.md", content);
        var collectionId = await EnsureCollectionAsync();

        var result = await ingester.IngestAsync(filePath, collectionId);

        result.Success.Should().BeTrue(because: result.Message);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

        // 3 authors → 3 co-author pairs (A-B, A-C, B-C)
        var relationships = await db.EntityRelationships
            .Where(r => r.RelationshipType == "co_author")
            .ToListAsync();

        relationships.Should().HaveCount(3);
    }

    [Fact]
    public async Task IngestAsync_deduplicates_authors_across_papers()
    {
        var ingester = CreateIngester();
        var collectionId = await EnsureCollectionAsync();

        var content1 = "---\ntitle: \"Paper 1\"\nauthors: \"Alice Smith, Bob Jones\"\n---\n# Paper 1\nContent one.\n";
        var content2 = "---\ntitle: \"Paper 2\"\nauthors: \"Alice Smith, Carol Lee\"\n---\n# Paper 2\nContent two.\n";

        var file1 = CreateTestFile("dedup-author-1.md", content1);
        var file2 = CreateTestFile("dedup-author-2.md", content2);

        var result1 = await ingester.IngestAsync(file1, collectionId);
        var result2 = await ingester.IngestAsync(file2, collectionId);

        result1.Success.Should().BeTrue(because: result1.Message);
        result2.Success.Should().BeTrue(because: result2.Message);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

        // "Alice Smith" appears in both papers → should only be 1 entity
        var authorEntities = await db.Entities
            .Where(e => e.EntityType == "author")
            .ToListAsync();

        // 3 unique authors total: alice smith, bob jones, carol lee
        authorEntities.Should().HaveCount(3);

        // Alice should be linked to both documents
        var aliceEntity = authorEntities.First(e => e.CanonicalName == "alice smith");
        var aliceLinks = await db.DocumentEntityLinks
            .Where(del => del.EntityId == aliceEntity.Id)
            .ToListAsync();

        aliceLinks.Should().HaveCount(2);
    }

    [Fact]
    public async Task IngestAsync_propagates_title_doi_arxiv_in_metadata()
    {
        var ingester = CreateIngester();
        var content = "---\ntitle: \"My Research Title\"\ndoi: \"10.1234/test\"\narxiv_id: \"2401.12345\"\n---\n# Paper\nContent.\n";
        var filePath = CreateTestFile("full-meta.md", content);
        var collectionId = await EnsureCollectionAsync();

        var result = await ingester.IngestAsync(filePath, collectionId);

        result.Success.Should().BeTrue(because: result.Message);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == result.DocumentId!.Value);

        doc.Should().NotBeNull();
        doc!.Metadata.Should().Contain("My Research Title");
        doc.Metadata.Should().Contain("10.1234/test");
        doc.Metadata.Should().Contain("2401.12345");
    }

    /// <summary>
    ///     Minimal fake summarizer that writes Completed progress and returns 5 chunks.
    /// </summary>
    private sealed class FakeSummarizer : IDocumentSummarizer
    {
        public SummaryTemplate Template { get; set; } = new();

        private static DocumentSummary MakeResult() =>
            new("Test summary", [], [],
                new SummarizationTrace("test", 5, 5, [], TimeSpan.FromMilliseconds(1), 1.0, 0.0));

        public Task<DocumentSummary> SummarizeMarkdownAsync(string markdown, string? documentId = null,
            string? focusQuery = null, SummarizationMode mode = SummarizationMode.Auto,
            CancellationToken cancellationToken = default)
            => Task.FromResult(MakeResult());

        public Task<DocumentSummary> SummarizeMarkdownAsync(string markdown,
            ChannelWriter<ProgressUpdate> progress, string? documentId = null,
            string? focusQuery = null, SummarizationMode mode = SummarizationMode.Auto,
            CancellationToken cancellationToken = default)
        {
            progress.TryWrite(new ProgressUpdate(ProgressType.Completed, "Done", "Done", PercentComplete: 100));
            progress.TryComplete();
            return Task.FromResult(MakeResult());
        }

        public Task<DocumentSummary> SummarizeFileAsync(string filePath,
            string? focusQuery = null, SummarizationMode mode = SummarizationMode.Auto,
            CancellationToken cancellationToken = default)
            => Task.FromResult(MakeResult());

        public Task<DocumentSummary> SummarizeFileAsync(string filePath,
            ChannelWriter<ProgressUpdate> progress, string? focusQuery = null,
            SummarizationMode mode = SummarizationMode.Auto,
            CancellationToken cancellationToken = default)
        {
            progress.TryWrite(new ProgressUpdate(ProgressType.Completed, "Done", "Done", PercentComplete: 100));
            progress.TryComplete();
            return Task.FromResult(MakeResult());
        }

        public Task<DocumentSummary> SummarizeUrlAsync(string url,
            string? focusQuery = null, SummarizationMode mode = SummarizationMode.Auto,
            bool usePlaywright = false, CancellationToken cancellationToken = default)
            => Task.FromResult(MakeResult());

        public Task<DocumentSummary> SummarizeUrlAsync(string url,
            ChannelWriter<ProgressUpdate> progress, string? focusQuery = null,
            SummarizationMode mode = SummarizationMode.Auto, bool usePlaywright = false,
            CancellationToken cancellationToken = default)
        {
            progress.TryWrite(new ProgressUpdate(ProgressType.Completed, "Done", "Done", PercentComplete: 100));
            progress.TryComplete();
            return Task.FromResult(MakeResult());
        }

        public Task<QueryAnswer> QueryAsync(string markdown, string question,
            string? documentId = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<ExtractionResult> ExtractSegmentsAsync(string markdown,
            string? documentId = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<ExtractionResult> ExtractSegmentsAsync(string markdown,
            ChannelWriter<ProgressUpdate> progress, string? documentId = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<ServiceAvailability> CheckServicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ServiceAvailability(true, false, null, true));
    }
}
