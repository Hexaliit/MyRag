using LucidRAG.Services;

namespace DomainClassifier.Tests;

public class LinkGraphExtractorTests
{
    [Fact]
    public void Extract_HttpUrl_ReturnsUrlEntity()
    {
        var text = "Visit https://example.net/page for more info.";

        var links = LinkGraphExtractor.Extract(text);

        Assert.Single(links);
        Assert.Equal(LinkType.Url, links[0].Type);
        Assert.Contains("example.net", links[0].Value);
    }

    [Fact]
    public void Extract_Doi_ReturnsDoi()
    {
        var text = "The paper (10.1038/s41586-023-06647-8) describes the method.";

        var links = LinkGraphExtractor.Extract(text);

        Assert.Single(links);
        Assert.Equal(LinkType.Doi, links[0].Type);
        Assert.Equal("10.1038/s41586-023-06647-8", links[0].Value);
    }

    [Fact]
    public void Extract_DoiUrl_ReturnsDoi()
    {
        var text = "See https://doi.org/10.1038/s41586-023-06647-8 for the full paper.";

        var links = LinkGraphExtractor.Extract(text);

        Assert.Single(links);
        Assert.Equal(LinkType.Doi, links[0].Type);
        Assert.Equal("10.1038/s41586-023-06647-8", links[0].Value);
    }

    [Fact]
    public void Extract_ArxivId_ReturnsArxiv()
    {
        var text = "Building on arXiv: 2301.12345, we improve the approach.";

        var links = LinkGraphExtractor.Extract(text);

        Assert.Single(links);
        Assert.Equal(LinkType.ArxivId, links[0].Type);
        Assert.Equal("2301.12345", links[0].Value);
    }

    [Fact]
    public void Extract_ArxivUrl_ReturnsArxiv()
    {
        var text = "See https://arxiv.org/abs/2301.12345 for details.";

        var links = LinkGraphExtractor.Extract(text);

        Assert.Single(links);
        Assert.Equal(LinkType.ArxivId, links[0].Type);
        Assert.Equal("2301.12345", links[0].Value);
    }

    [Fact]
    public void Extract_MultipleTypes_ReturnsAll()
    {
        var text = """
            Visit https://github.com/project for the code.
            The paper DOI is 10.1234/test.5678.
            Also see arXiv: 2301.12345 for the preprint.
            """;

        var links = LinkGraphExtractor.Extract(text);

        Assert.Equal(3, links.Count);
        Assert.Contains(links, l => l.Type == LinkType.Url);
        Assert.Contains(links, l => l.Type == LinkType.Doi);
        Assert.Contains(links, l => l.Type == LinkType.ArxivId);
    }

    [Fact]
    public void Extract_SkipsSchemaOrg_Urls()
    {
        var text = "The schema is at https://schema.org/Thing and https://www.w3.org/TR/html.";

        var links = LinkGraphExtractor.Extract(text);

        Assert.Empty(links);
    }

    [Fact]
    public void Extract_EmptyText_ReturnsEmpty()
    {
        Assert.Empty(LinkGraphExtractor.Extract(""));
    }

    [Fact]
    public void Extract_NoDuplicates_ForSameUrl()
    {
        var text = "See https://github.com/project and also https://github.com/project again.";

        var links = LinkGraphExtractor.Extract(text);

        Assert.Single(links);
    }

    [Fact]
    public void Extract_DoiWithDoiPrefix_NormalizesCorrectly()
    {
        var text = "doi: 10.1038/test123";

        var links = LinkGraphExtractor.Extract(text);

        Assert.Single(links);
        Assert.Equal(LinkType.Doi, links[0].Type);
        Assert.Equal("10.1038/test123", links[0].Value);
    }

    [Fact]
    public void Extract_ArxivWithVersion_ReturnsId()
    {
        var text = "See arXiv:2301.12345v2 for the updated version.";

        var links = LinkGraphExtractor.Extract(text);

        Assert.Single(links);
        Assert.Equal(LinkType.ArxivId, links[0].Type);
        Assert.Equal("2301.12345v2", links[0].Value);
    }

    [Fact]
    public void Extract_ProvidesContext()
    {
        var text = "The important paper at 10.1038/test123 describes breakthrough results.";

        var links = LinkGraphExtractor.Extract(text);

        Assert.Single(links);
        Assert.NotEmpty(links[0].Context);
        Assert.Contains("important paper", links[0].Context);
    }
}
