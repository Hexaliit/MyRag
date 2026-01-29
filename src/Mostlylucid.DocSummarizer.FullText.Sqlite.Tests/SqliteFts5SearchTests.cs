using FluentAssertions;
using Microsoft.Data.Sqlite;
using Mostlylucid.DocSummarizer.FullText.Sqlite;
using Mostlylucid.DocSummarizer.Search;
using Xunit;

namespace Mostlylucid.DocSummarizer.FullText.Sqlite.Tests;

public class SqliteFts5SearchTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly SqliteFts5SearchService _sut;

    public SqliteFts5SearchTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db");
        _sut = new SqliteFts5SearchService(_dbPath);
    }

    public async Task InitializeAsync()
    {
        await _sut.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _sut.DisposeAsync();

        // SQLite on Windows keeps the file locked via connection pooling.
        // Clear the pool so the file handle is released before deletion.
        SqliteConnection.ClearAllPools();

        // Clean up the temp database file
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Fact]
    public async Task IndexAndSearch_FindsDocument()
    {
        // Arrange
        await _sut.IndexDocumentAsync("doc-1", "Quantum Physics", "Introduction to quantum mechanics and wave functions");

        // Act
        var results = await _sut.SearchAsync("quantum");

        // Assert
        results.Should().NotBeEmpty();
        results.Should().Contain(r => r.Id == "doc-1");
    }

    [Fact]
    public async Task Search_ReturnsEmpty_WhenNoMatch()
    {
        // Arrange
        await _sut.IndexDocumentAsync("doc-1", "Cooking Recipes", "How to make a delicious pasta carbonara");

        // Act
        var results = await _sut.SearchAsync("xylophone");

        // Assert
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task IdfAwareRanking_DistinctiveTermRanksHigher()
    {
        // Arrange: Index 10 docs, all containing "the", but only doc-quantum contains "quantum"
        for (var i = 0; i < 10; i++)
        {
            var id = $"doc-{i}";
            var title = $"Document {i}";
            var content = i == 5
                ? "The quantum entanglement experiment revealed surprising results about the universe"
                : $"The document number {i} contains general information about the world";
            var keywords = i == 5
                ? new[] { "the", "quantum" }
                : new[] { "the" };

            await _sut.IndexDocumentAsync(id, title, content, keywords);
        }

        // Act: Search for "the quantum" -- "the" appears in >50% so it's OR'd, "quantum" is AND'd
        var results = await _sut.SearchAsync("the quantum");

        // Assert: doc-5 (the one with "quantum") should rank highest
        results.Should().NotBeEmpty();
        results.First().Id.Should().Be("doc-5");
    }

    [Fact]
    public async Task DeleteDocument_RemovesFromIndex()
    {
        // Arrange
        await _sut.IndexDocumentAsync("doc-1", "Test Document", "Unique content about neural networks");

        // Verify it is found before deletion
        var beforeDelete = await _sut.SearchAsync("neural networks");
        beforeDelete.Should().NotBeEmpty();

        // Act
        await _sut.DeleteDocumentAsync("doc-1");

        // Assert
        var afterDelete = await _sut.SearchAsync("neural networks");
        afterDelete.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAll_ClearsIndex()
    {
        // Arrange
        await _sut.IndexDocumentAsync("doc-1", "First", "Alpha content about machine learning");
        await _sut.IndexDocumentAsync("doc-2", "Second", "Beta content about deep learning");
        await _sut.IndexDocumentAsync("doc-3", "Third", "Gamma content about reinforcement learning");

        // Verify documents are indexed
        var beforeDelete = await _sut.SearchAsync("learning");
        beforeDelete.Should().HaveCountGreaterThanOrEqualTo(3);

        // Act
        await _sut.DeleteAllAsync();

        // Assert
        var afterDelete = await _sut.SearchAsync("learning");
        afterDelete.Should().BeEmpty();
    }

    [Fact]
    public async Task KeywordCorpus_TracksTermFrequency()
    {
        // Arrange: Index documents with overlapping keywords
        await _sut.IndexDocumentAsync("doc-1", "AI Basics", "Introduction to artificial intelligence",
            new[] { "ai", "introduction", "basics" });
        await _sut.IndexDocumentAsync("doc-2", "AI Advanced", "Advanced artificial intelligence techniques",
            new[] { "ai", "advanced", "techniques" });
        await _sut.IndexDocumentAsync("doc-3", "ML Intro", "Introduction to machine learning",
            new[] { "ml", "introduction", "basics" });

        // Act: Search for documents with the keyword "introduction"
        var results = await _sut.SearchAsync("introduction");

        // Assert: Should find documents that contain "introduction"
        results.Should().NotBeEmpty();
        results.Should().Contain(r => r.Id == "doc-1" || r.Id == "doc-3");
    }

    [Fact]
    public async Task PorterStemming_FindsStemmedTerms()
    {
        // Arrange: Index with "programming"
        await _sut.IndexDocumentAsync("doc-1", "Programming Guide",
            "Advanced programming techniques for modern software development");

        // Act: Search for "programs" -- porter stemming should match "programming" via stem "program"
        var results = await _sut.SearchAsync("programs");

        // Assert: Should find the document through stemming
        results.Should().NotBeEmpty();
        results.Should().Contain(r => r.Id == "doc-1");
    }

    [Fact]
    public async Task MultipleDocuments_ReturnsRanked()
    {
        // Arrange: Index 3 docs with varying relevance to "machine learning"
        await _sut.IndexDocumentAsync("doc-high", "Machine Learning",
            "Machine learning is a subset of artificial intelligence. Machine learning algorithms learn from data. Machine learning models improve over time.");
        await _sut.IndexDocumentAsync("doc-medium", "Data Science",
            "Data science often uses machine learning techniques for analysis and prediction.");
        await _sut.IndexDocumentAsync("doc-low", "Software Engineering",
            "Software engineering principles guide the development of reliable systems.");

        // Act
        var results = await _sut.SearchAsync("machine learning");

        // Assert: Should return results in ranked order (most relevant first)
        results.Should().NotBeEmpty();

        // doc-high should rank higher than doc-medium because it has more occurrences of "machine learning"
        var highIndex = results.FindIndex(r => r.Id == "doc-high");
        var mediumIndex = results.FindIndex(r => r.Id == "doc-medium");

        highIndex.Should().BeGreaterThanOrEqualTo(0, "doc-high should be in results");
        mediumIndex.Should().BeGreaterThanOrEqualTo(0, "doc-medium should be in results");
        highIndex.Should().BeLessThan(mediumIndex, "doc-high should rank higher (lower index) than doc-medium");

        // doc-low should not appear since it does not contain "machine" or "learning"
        results.Should().NotContain(r => r.Id == "doc-low");
    }
}
