using DomainClassifier.Technical.Extractors;

namespace DomainClassifier.Tests;

public class CitationExtractorTests
{
    [Fact]
    public void Extract_SingleNumbered_ReturnsOne()
    {
        var entities = CitationExtractor.Extract("As shown in [1], the method works.");

        Assert.Single(entities);
        Assert.Equal("citation_numbered", entities[0].EntityType);
        Assert.Equal("1", entities[0].Metadata!["citation_key"]);
    }

    [Fact]
    public void Extract_MultipleNumbered_ReturnsAll()
    {
        var entities = CitationExtractor.Extract("Several studies [1,2,3] confirm this.");

        Assert.Equal(3, entities.Count);
        Assert.All(entities, e => Assert.Equal("citation_numbered", e.EntityType));
    }

    [Fact]
    public void Extract_NumberedRange_ExpandsRange()
    {
        var entities = CitationExtractor.Extract("As shown in [1-4], the results are clear.");

        Assert.Equal(4, entities.Count);
        var keys = entities.Select(e => e.Metadata!["citation_key"]?.ToString()).ToList();
        Assert.Contains("1", keys);
        Assert.Contains("2", keys);
        Assert.Contains("3", keys);
        Assert.Contains("4", keys);
    }

    [Fact]
    public void Extract_AuthorYearParens_ReturnsEntity()
    {
        var entities = CitationExtractor.Extract("According to (Smith, 2020), the approach is valid.");

        Assert.Single(entities);
        Assert.Equal("citation_author_year", entities[0].EntityType);
        Assert.Equal("Smith", entities[0].Metadata!["author"]);
        Assert.Equal("2020", entities[0].Metadata!["year"]);
    }

    [Fact]
    public void Extract_AuthorYearEtAl_ReturnsEntity()
    {
        var entities = CitationExtractor.Extract("(Smith et al., 2020) demonstrated this.");

        Assert.Single(entities);
        Assert.Equal("citation_author_year", entities[0].EntityType);
        Assert.Contains("Smith", entities[0].Metadata!["author"]!.ToString()!);
    }

    [Fact]
    public void Extract_TextualAuthorYear_ReturnsEntity()
    {
        var entities = CitationExtractor.Extract("Smith et al. (2020) found that...");

        Assert.NotEmpty(entities);
        Assert.Contains(entities, e => e.EntityType == "citation_author_year");
    }

    [Fact]
    public void Extract_MixedCitations_ReturnsBothTypes()
    {
        var text = "Smith (2020) showed that [1] and (Jones, 2021) confirmed [2,3].";

        var entities = CitationExtractor.Extract(text);

        Assert.Contains(entities, e => e.EntityType == "citation_numbered");
        Assert.Contains(entities, e => e.EntityType == "citation_author_year");
    }

    [Fact]
    public void Extract_EmptyText_ReturnsEmpty()
    {
        var entities = CitationExtractor.Extract("");
        Assert.Empty(entities);
    }

    [Fact]
    public void Extract_NoCitations_ReturnsEmpty()
    {
        var entities = CitationExtractor.Extract("This is a regular sentence without any citations.");
        Assert.Empty(entities);
    }

    [Fact]
    public void ExpandCitationRange_Simple_Works()
    {
        var result = CitationExtractor.ExpandCitationRange("1-3");
        Assert.Equal([1, 2, 3], result);
    }

    [Fact]
    public void ExpandCitationRange_CommaSeparated_Works()
    {
        var result = CitationExtractor.ExpandCitationRange("1, 3, 5");
        Assert.Equal([1, 3, 5], result);
    }

    [Fact]
    public void ExpandCitationRange_Mixed_Works()
    {
        var result = CitationExtractor.ExpandCitationRange("1-3, 5, 7-8");
        Assert.Equal([1, 2, 3, 5, 7, 8], result);
    }

    [Fact]
    public void Extract_AuthorAmpersand_ReturnsEntity()
    {
        var entities = CitationExtractor.Extract("(Smith & Jones, 2019) reported findings.");

        Assert.NotEmpty(entities);
        Assert.Contains(entities, e => e.EntityType == "citation_author_year");
    }

    [Fact]
    public void Extract_YearWithLetter_ReturnsEntity()
    {
        var entities = CitationExtractor.Extract("As noted by (Smith, 2020a), this is important.");

        Assert.NotEmpty(entities);
        Assert.Contains(entities, e =>
            e.EntityType == "citation_author_year" &&
            e.Metadata!["year"]?.ToString() == "2020a");
    }
}
