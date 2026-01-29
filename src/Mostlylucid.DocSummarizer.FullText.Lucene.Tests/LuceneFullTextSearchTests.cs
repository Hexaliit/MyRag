using FluentAssertions;
using Mostlylucid.DocSummarizer.FullText.Lucene;
using Mostlylucid.DocSummarizer.Search;
using Xunit;

namespace Mostlylucid.DocSummarizer.FullText.Lucene.Tests;

public class LuceneFullTextSearchTests : IAsyncLifetime
{
    private readonly string _indexPath;
    private readonly LuceneFullTextSearch _search;

    public LuceneFullTextSearchTests()
    {
        _indexPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _search = new LuceneFullTextSearch(_indexPath);
    }

    public async Task InitializeAsync()
    {
        await _search.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        _search.Dispose();

        if (Directory.Exists(_indexPath))
        {
            Directory.Delete(_indexPath, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task IndexAndSearch_FindsDocument()
    {
        // Arrange
        await _search.IndexDocumentAsync("doc1", "Introduction to Machine Learning", "This document covers supervised and unsupervised learning algorithms.");

        // Act
        var results = await _search.SearchAsync("Machine Learning");

        // Assert
        results.Should().NotBeEmpty();
        results.Should().Contain(r => r.Id == "doc1");
    }

    [Fact]
    public async Task Search_ReturnsEmpty_WhenNoMatch()
    {
        // Arrange
        await _search.IndexDocumentAsync("doc1", "Cooking Recipes", "A guide to Italian pasta dishes.");

        // Act
        var results = await _search.SearchAsync("quantum physics");

        // Assert
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task TitleBoost_RanksHigher()
    {
        // Arrange - "architecture" appears in the title of doc1 but only in content of doc2
        await _search.IndexDocumentAsync("title-match", "Software Architecture Patterns", "This document discusses various design approaches for building systems.");
        await _search.IndexDocumentAsync("content-match", "Development Guide", "This document explains software architecture in modern applications.");

        // Act
        var results = await _search.SearchAsync("architecture");

        // Assert
        results.Should().HaveCountGreaterThanOrEqualTo(2);
        var titleMatch = results.First(r => r.Id == "title-match");
        var contentMatch = results.First(r => r.Id == "content-match");
        titleMatch.Score.Should().BeGreaterThan(contentMatch.Score, "title matches should be boosted higher than content-only matches");
    }

    [Fact]
    public async Task KeywordBoost_RanksHigher()
    {
        // Arrange - "elasticsearch" appears in keywords of doc1 but only in content of doc2
        await _search.IndexDocumentAsync("keyword-match", "Search Engine Overview", "A general overview of search technologies.", new[] { "elasticsearch", "lucene", "solr" });
        await _search.IndexDocumentAsync("content-match", "Database Technologies", "This guide covers elasticsearch and other data storage solutions.");

        // Act
        var results = await _search.SearchAsync("elasticsearch");

        // Assert
        results.Should().HaveCountGreaterThanOrEqualTo(2);
        var keywordMatch = results.First(r => r.Id == "keyword-match");
        var contentMatch = results.First(r => r.Id == "content-match");
        keywordMatch.Score.Should().BeGreaterThan(contentMatch.Score, "keyword matches should be boosted higher than content-only matches");
    }

    [Fact]
    public async Task DeleteDocument_RemovesFromIndex()
    {
        // Arrange
        await _search.IndexDocumentAsync("doc-to-delete", "Temporary Document", "This document will be deleted from the index.");

        // Verify it exists first
        var beforeDelete = await _search.SearchAsync("Temporary Document");
        beforeDelete.Should().NotBeEmpty();

        // Act
        await _search.DeleteDocumentAsync("doc-to-delete");

        // Assert
        var afterDelete = await _search.SearchAsync("Temporary Document");
        afterDelete.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAll_ClearsIndex()
    {
        // Arrange
        await _search.IndexDocumentAsync("doc1", "First Document", "Content of the first document.");
        await _search.IndexDocumentAsync("doc2", "Second Document", "Content of the second document.");
        await _search.IndexDocumentAsync("doc3", "Third Document", "Content of the third document.");

        // Verify they exist first
        var beforeDelete = await _search.SearchAsync("document");
        beforeDelete.Should().NotBeEmpty();

        // Act
        await _search.DeleteAllAsync();

        // Assert
        var afterDelete = await _search.SearchAsync("document");
        afterDelete.Should().BeEmpty();
    }

    [Fact]
    public async Task MultiTermSearch_FindsRelevant()
    {
        // Arrange
        await _search.IndexDocumentAsync("relevant", "Cloud Computing Fundamentals", "An introduction to distributed systems and cloud native infrastructure.");
        await _search.IndexDocumentAsync("partial", "Desktop Applications", "Building native applications for Windows and macOS.");
        await _search.IndexDocumentAsync("unrelated", "Gardening Tips", "How to grow tomatoes in your backyard.");

        // Act
        var results = await _search.SearchAsync("cloud native infrastructure");

        // Assert
        results.Should().NotBeEmpty();
        results.Should().Contain(r => r.Id == "relevant");
        // The unrelated gardening doc should not appear
        results.Should().NotContain(r => r.Id == "unrelated");
    }

    [Fact]
    public async Task FuzzySearch_FindsSimilarTerms()
    {
        // Arrange - index with correct spelling
        await _search.IndexDocumentAsync("doc1", "Programming Languages", "A comprehensive guide to programming paradigms and language design.");

        // Act - search with fuzzy operator (~) and a typo: "programing" instead of "programming"
        // Lucene's MultiFieldQueryParser uses FuzzyMinSim=0.7 when the ~ operator is present
        var results = await _search.SearchAsync("programing~");

        // Assert - fuzzy matching (0.7 similarity) should still find the document
        results.Should().NotBeEmpty();
        results.Should().Contain(r => r.Id == "doc1");
    }
}
