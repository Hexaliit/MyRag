using LucidRAG.Data;
using LucidRAG.Entities;
using LucidRAG.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidResearch.Tests.Services;

public class EntityMatcherTests : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly string _tempDir;

    public EntityMatcherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"entitymatcher-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.ClearProviders());
        sc.AddDbContext<RagDocumentsDbContext>(options =>
            options.UseSqlite($"Data Source={Path.Combine(_tempDir, "test.db")}"));

        _services = sc.BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _services.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private (EntityMatcher matcher, RagDocumentsDbContext db) CreateMatcher()
    {
        var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();
        var matcher = new EntityMatcher(db, NullLogger<EntityMatcher>.Instance);
        return (matcher, db);
    }

    private async Task<Guid> EnsureCollectionAsync(RagDocumentsDbContext db, string name = "test")
    {
        var collection = new CollectionEntity { Id = Guid.NewGuid(), Name = name };
        db.Collections.Add(collection);
        await db.SaveChangesAsync();
        return collection.Id;
    }

    private async Task<(Guid docId, ExtractedEntity entity)> SeedEntityAsync(
        RagDocumentsDbContext db, Guid collectionId, string canonicalName, string entityType,
        string[]? aliases = null)
    {
        var doc = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            Name = "test-doc",
            CollectionId = collectionId,
            Status = DocumentStatus.Completed,
            OriginalFilename = "test.md",
            ContentHash = Guid.NewGuid().ToString()
        };
        db.Documents.Add(doc);

        var entity = new ExtractedEntity
        {
            Id = Guid.NewGuid(),
            CanonicalName = canonicalName,
            EntityType = entityType,
            Aliases = aliases ?? []
        };
        db.Entities.Add(entity);

        db.DocumentEntityLinks.Add(new DocumentEntityLink
        {
            DocumentId = doc.Id,
            EntityId = entity.Id,
            MentionCount = 1
        });

        await db.SaveChangesAsync();
        return (doc.Id, entity);
    }

    // ---- Author Normalization Tests (via public Normalize method) ----

    [Theory]
    [InlineData("Dr. John Smith", "john smith")]
    [InlineData("Prof. Jane Doe", "jane doe")]
    [InlineData("Smith, John", "john smith")]
    [InlineData("J. Smith", "j smith")]
    [InlineData("J.R. Smith", "j r smith")]
    [InlineData("John Smith Jr.", "john smith")]
    public void Normalize_author_handles_various_formats(string input, string expected)
    {
        var (matcher, _) = CreateMatcher();
        var result = matcher.Normalize(input, "author");
        result.Should().Be(expected);
    }

    // ---- Organization Normalization Tests ----

    [Theory]
    [InlineData("Google Inc.", "google")]
    [InlineData("Microsoft Corp", "microsoft")]
    [InlineData("Acme LLC", "acme")]
    public void Normalize_organization_strips_suffixes(string input, string expected)
    {
        var (matcher, _) = CreateMatcher();
        var result = matcher.Normalize(input, "organization");
        result.Should().Be(expected);
    }

    // ---- Location Normalization Tests ----

    [Theory]
    [InlineData("NY", "new york")]
    [InlineData("UK", "united kingdom")]
    [InlineData("USA", "united states")]
    public void Normalize_location_maps_abbreviations(string input, string expected)
    {
        var (matcher, _) = CreateMatcher();
        var result = matcher.Normalize(input, "location");
        result.Should().Be(expected);
    }

    // ---- Concept Normalization Tests ----

    [Theory]
    [InlineData("Python 3.11", "python")]
    [InlineData("TensorFlow v2.15", "tensorflow")]
    [InlineData("natural language processing", "natural language processing")]
    public void Normalize_concept_strips_versions(string input, string expected)
    {
        var (matcher, _) = CreateMatcher();
        var result = matcher.Normalize(input, "concept");
        result.Should().Be(expected);
    }

    // ---- FindMatchAsync Tests ----

    [Fact]
    public async Task FindMatchAsync_exact_canonical_match()
    {
        var (matcher, db) = CreateMatcher();
        var collectionId = await EnsureCollectionAsync(db);
        var (_, seeded) = await SeedEntityAsync(db, collectionId, "John Smith", "author");

        var result = await matcher.FindMatchAsync("John Smith", "author", collectionId);

        result.Should().NotBeNull();
        result!.Id.Should().Be(seeded.Id);
    }

    [Fact]
    public async Task FindMatchAsync_alias_match()
    {
        var (matcher, db) = CreateMatcher();
        var collectionId = await EnsureCollectionAsync(db);
        var (_, seeded) = await SeedEntityAsync(db, collectionId, "john smith", "author",
            aliases: ["J. Smith", "Smith, John"]);

        var result = await matcher.FindMatchAsync("J. Smith", "author", collectionId);

        result.Should().NotBeNull();
        result!.Id.Should().Be(seeded.Id);
    }

    [Fact]
    public async Task FindMatchAsync_levenshtein_match()
    {
        var (matcher, db) = CreateMatcher();
        var collectionId = await EnsureCollectionAsync(db);
        var (_, seeded) = await SeedEntityAsync(db, collectionId, "john smith", "author");

        // "John Smithh" normalizes to "john smithh" — 1 edit distance, should match
        var result = await matcher.FindMatchAsync("John Smithh", "author", collectionId);

        result.Should().NotBeNull();
        result!.Id.Should().Be(seeded.Id);
    }

    [Fact]
    public async Task FindMatchAsync_returns_null_for_no_match()
    {
        var (matcher, db) = CreateMatcher();
        var collectionId = await EnsureCollectionAsync(db);
        await SeedEntityAsync(db, collectionId, "John Smith", "author");

        var result = await matcher.FindMatchAsync("Alice Wonderland", "author", collectionId);

        result.Should().BeNull();
    }

    [Fact]
    public async Task FindMatchAsync_returns_null_for_empty_collection()
    {
        var (matcher, db) = CreateMatcher();
        var collectionId = await EnsureCollectionAsync(db);

        var result = await matcher.FindMatchAsync("John Smith", "author", collectionId);

        result.Should().BeNull();
    }

    [Fact]
    public async Task FindMatchAsync_author_with_title_matches_plain_name()
    {
        var (matcher, db) = CreateMatcher();
        var collectionId = await EnsureCollectionAsync(db);
        await SeedEntityAsync(db, collectionId, "john smith", "author");

        // "Dr. John Smith" normalizes to "john smith" — should match exactly
        var result = await matcher.FindMatchAsync("Dr. John Smith", "author", collectionId);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task FindMatchAsync_last_first_format_matches_first_last()
    {
        var (matcher, db) = CreateMatcher();
        var collectionId = await EnsureCollectionAsync(db);
        await SeedEntityAsync(db, collectionId, "john smith", "author");

        // "Smith, John" normalizes to "john smith" — should match
        var result = await matcher.FindMatchAsync("Smith, John", "author", collectionId);

        result.Should().NotBeNull();
    }
}
