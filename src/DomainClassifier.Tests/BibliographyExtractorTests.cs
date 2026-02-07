using DomainClassifier.Technical.Extractors;

namespace DomainClassifier.Tests;

public class BibliographyExtractorTests
{
    [Fact]
    public void Extract_NumberedReferences_ParsesEntries()
    {
        var text = """
            Some document content here.

            References
            [1] Smith, J., Jones, K. (2020). A study of things. Journal of Stuff, 15(3), 123-145.
            [2] Brown, A. et al. (2019). Another paper. arXiv: 2301.12345. Proceedings of NeurIPS.
            [3] Wilson, R. (2021). Yet another paper. doi: 10.1038/s41586-023-06647-8.
            """;

        var entities = BibliographyExtractor.Extract(text);

        Assert.Equal(3, entities.Count);
        Assert.All(entities, e => Assert.Equal("bibliography_entry", e.EntityType));
    }

    [Fact]
    public void Extract_WithDoi_ExtractsDoi()
    {
        var text = """
            References
            [1] Author (2021). Title. 10.1038/s41586-023-06647-8.
            """;

        var entities = BibliographyExtractor.Extract(text);

        Assert.Single(entities);
        Assert.NotNull(entities[0].Metadata!["doi"]);
    }

    [Fact]
    public void Extract_WithArxiv_ExtractsArxivId()
    {
        var text = """
            References
            [1] Author (2021). A paper. arXiv: 2301.12345v2.
            """;

        var entities = BibliographyExtractor.Extract(text);

        Assert.Single(entities);
        Assert.NotNull(entities[0].Metadata!["arxiv_id"]);
    }

    [Fact]
    public void Extract_WithYear_ExtractsYear()
    {
        var text = """
            Bibliography
            [1] Smith, J. (2020). Title of the paper.
            """;

        var entities = BibliographyExtractor.Extract(text);

        Assert.Single(entities);
        Assert.Equal("2020", entities[0].Metadata!["year"]);
    }

    [Fact]
    public void Extract_NoBibliographySection_ReturnsEmpty()
    {
        var text = "This is just regular text without a bibliography section.";

        var entities = BibliographyExtractor.Extract(text);

        Assert.Empty(entities);
    }

    [Fact]
    public void Extract_EmptyText_ReturnsEmpty()
    {
        Assert.Empty(BibliographyExtractor.Extract(""));
    }

    [Fact]
    public void HasBibliography_WithSection_ReturnsTrue()
    {
        var text = """
            Some text here.

            References
            [1] A reference.
            """;

        Assert.True(BibliographyExtractor.HasBibliography(text));
    }

    [Fact]
    public void HasBibliography_WorksCited_ReturnsTrue()
    {
        var text = """
            Content.

            Works Cited
            Entry 1.
            """;

        Assert.True(BibliographyExtractor.HasBibliography(text));
    }

    [Fact]
    public void HasBibliography_NoSection_ReturnsFalse()
    {
        Assert.False(BibliographyExtractor.HasBibliography("Just regular text."));
    }
}
