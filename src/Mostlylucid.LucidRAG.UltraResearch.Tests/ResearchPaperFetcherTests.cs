using DoomSummarizer.Services;
using LucidRAG.UltraResearch;

namespace Mostlylucid.LucidRAG.UltraResearch.Tests;

public class ResearchPaperFetcherTests
{
    [Theory]
    [InlineData("arxiv", "2301.12345", "arxiv:2301.12345")]
    [InlineData("arxiv", "2301.12345v2", "arxiv:2301.12345")]
    [InlineData("doi", "10.1234/test.paper", "doi:10.1234/test.paper")]
    public void NormalizeSeenKey_ProducesConsistentKeys(string type, string id, string expected)
    {
        var key = ResearchPaperFetcher.NormalizeSeenKey(type, id);
        Assert.Equal(expected, key);
    }

    [Fact]
    public void NormalizeSeenKey_StripsArxivVersion()
    {
        var key1 = ResearchPaperFetcher.NormalizeSeenKey("arxiv", "2301.12345v1");
        var key2 = ResearchPaperFetcher.NormalizeSeenKey("arxiv", "2301.12345v2");
        var key3 = ResearchPaperFetcher.NormalizeSeenKey("arxiv", "2301.12345");

        Assert.Equal(key1, key2);
        Assert.Equal(key2, key3);
    }

    [Fact]
    public void NormalizePaperId_PrefersArxivOverDoi()
    {
        var paper = new S2Paper
        {
            PaperId = "abc123",
            ExternalIds = new S2ExternalIds
            {
                ArXiv = "2301.12345v1",
                DOI = "10.1234/test"
            }
        };

        var (id, type) = ResearchPaperFetcher.NormalizePaperId(paper);

        Assert.Equal("arxiv", type);
        Assert.Equal("2301.12345", id); // Version stripped
    }

    [Fact]
    public void NormalizePaperId_FallsBackToDoi()
    {
        var paper = new S2Paper
        {
            PaperId = "abc123",
            ExternalIds = new S2ExternalIds
            {
                DOI = "10.1234/test"
            }
        };

        var (id, type) = ResearchPaperFetcher.NormalizePaperId(paper);

        Assert.Equal("doi", type);
        Assert.Equal("10.1234/test", id);
    }

    [Fact]
    public void NormalizePaperId_ReturnsNull_WhenNoExternalIds()
    {
        var paper = new S2Paper { PaperId = "abc123" };

        var (id, type) = ResearchPaperFetcher.NormalizePaperId(paper);

        Assert.Null(id);
    }

    [Fact]
    public void SemanticScholarClient_ArxivToS2Id_FormatsCorrectly()
    {
        Assert.Equal("ARXIV:2301.12345", SemanticScholarClient.ArxivToS2Id("2301.12345"));
        Assert.Equal("ARXIV:2301.12345", SemanticScholarClient.ArxivToS2Id("2301.12345v2")); // Strips version
    }

    [Fact]
    public void SemanticScholarClient_DoiToS2Id_FormatsCorrectly()
    {
        Assert.Equal("DOI:10.1234/test", SemanticScholarClient.DoiToS2Id("10.1234/test"));
    }

    [Fact]
    public void S2Paper_ExternalIds_ExtractCorrectly()
    {
        var paper = new S2Paper
        {
            ExternalIds = new S2ExternalIds
            {
                ArXiv = "2301.12345",
                DOI = "10.1234/test",
                CorpusId = 123456
            }
        };

        Assert.Equal("2301.12345", paper.ArxivId);
        Assert.Equal("10.1234/test", paper.Doi);
    }
}
