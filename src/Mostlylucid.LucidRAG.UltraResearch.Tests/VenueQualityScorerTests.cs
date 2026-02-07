using DoomSummarizer.Models;
using DoomSummarizer.Services;
using LucidRAG.UltraResearch;

namespace Mostlylucid.LucidRAG.UltraResearch.Tests;

public class VenueQualityScorerTests
{
    [Fact]
    public void NaturePaper_HighQuality()
    {
        // Nature paper: journal, 500 citations, 50 influential
        var s2Paper = new S2Paper
        {
            CitationCount = 500,
            InfluentialCitationCount = 50,
            PublicationVenue = new S2PublicationVenue { Name = "Nature", Type = "Journal" },
            ExternalIds = new S2ExternalIds { DOI = "10.1038/s41586-024-00001-0" }
        };

        var score = VenueQualityScorer.ComputeVenueQuality(s2Paper, null);

        Assert.True(score >= 0.85, $"Nature paper should score >= 0.85, got {score:F3}");
        Assert.True(score <= 1.0, $"Score should be <= 1.0, got {score:F3}");
    }

    [Fact]
    public void NeurIPSPaper_HighQuality()
    {
        // NeurIPS paper: conference, 100 citations, 20 influential
        var s2Paper = new S2Paper
        {
            CitationCount = 100,
            InfluentialCitationCount = 20,
            PublicationVenue = new S2PublicationVenue { Name = "NeurIPS", Type = "Conference" },
            ExternalIds = new S2ExternalIds { DOI = "10.5555/1234567890" }
        };

        var score = VenueQualityScorer.ComputeVenueQuality(s2Paper, null);

        Assert.True(score >= 0.70, $"NeurIPS paper should score >= 0.70, got {score:F3}");
        Assert.True(score <= 1.0, $"Score should be <= 1.0, got {score:F3}");
    }

    [Fact]
    public void ArxivPreprint_LowQuality()
    {
        // arXiv preprint: no venue, 5 citations, 0 influential
        var s2Paper = new S2Paper
        {
            CitationCount = 5,
            InfluentialCitationCount = 0,
            Venue = "ArXiv"
        };

        var score = VenueQualityScorer.ComputeVenueQuality(s2Paper, null);

        Assert.True(score >= 0.10, $"arXiv preprint should score >= 0.10, got {score:F3}");
        Assert.True(score <= 0.40, $"arXiv preprint should score <= 0.40, got {score:F3}");
    }

    [Fact]
    public void UnknownPaper_NoData_VeryLow()
    {
        // No S2 data at all — gets small nonzero from default venueType (0.4) and publication (0.3) signals
        var score = VenueQualityScorer.ComputeVenueQuality(null, null);

        Assert.True(score >= 0.0, $"Score should be >= 0, got {score:F3}");
        Assert.True(score <= 0.20, $"Score with no data should be very low (<= 0.20), got {score:F3}");
    }

    [Fact]
    public void CrossRefVenue_UsedAsFallback()
    {
        // S2 paper with no venue, but CrossRef has it
        var s2Paper = new S2Paper
        {
            CitationCount = 200,
            InfluentialCitationCount = 30,
            ExternalIds = new S2ExternalIds { DOI = "10.1038/s41586-024-00001-0" }
        };
        var crossRef = new CitationMetadata(
            "Some Paper", ["Author"], 2024,
            "Nature", null, "10.1038/s41586-024-00001-0", null, null);

        var score = VenueQualityScorer.ComputeVenueQuality(s2Paper, crossRef);

        // Should use Nature from CrossRef and get a high score
        Assert.True(score >= 0.75, $"CrossRef Nature fallback should score >= 0.75, got {score:F3}");
    }

    [Fact]
    public void VenueTypeSignal_Journal_HigherThanConference()
    {
        var journal = VenueQualityScorer.GetVenueTypeSignal("Journal");
        var conference = VenueQualityScorer.GetVenueTypeSignal("Conference");

        Assert.True(journal > conference, "Journal should score higher than Conference");
    }

    [Fact]
    public void VenueTypeSignal_UnknownType_DefaultScore()
    {
        var unknown = VenueQualityScorer.GetVenueTypeSignal(null);
        var alsoUnknown = VenueQualityScorer.GetVenueTypeSignal("Workshop");

        Assert.Equal(0.4, unknown);
        Assert.Equal(0.4, alsoUnknown);
    }

    [Fact]
    public void MatchVenueTier_DirectMatch()
    {
        var result = VenueQualityScorer.MatchVenueTier("Nature");
        Assert.NotNull(result);
        Assert.Equal(1.0, result.Value);
    }

    [Fact]
    public void MatchVenueTier_CaseInsensitive()
    {
        var result = VenueQualityScorer.MatchVenueTier("neurips");
        Assert.NotNull(result);
        Assert.Equal(0.90, result.Value);
    }

    [Fact]
    public void MatchVenueTier_WithPrefix()
    {
        var result = VenueQualityScorer.MatchVenueTier("Proceedings of the NeurIPS");
        Assert.NotNull(result);
    }

    [Fact]
    public void MatchVenueTier_NoMatch_ReturnsNull()
    {
        var result = VenueQualityScorer.MatchVenueTier("International Journal of Obscure Studies");
        Assert.Null(result);
    }

    [Fact]
    public void NormalizeVenueName_StripsCommonPrefixes()
    {
        Assert.Equal("NeurIPS", VenueQualityScorer.NormalizeVenueName("Proceedings of the NeurIPS"));
        Assert.Equal("ICML", VenueQualityScorer.NormalizeVenueName("International Conference on ICML"));
        Assert.Equal("Lancet", VenueQualityScorer.NormalizeVenueName("The Lancet"));
    }

    [Fact]
    public void ScoreAlwaysInRange()
    {
        // Test with extreme values
        var extremePaper = new S2Paper
        {
            CitationCount = 1_000_000,
            InfluentialCitationCount = 100_000,
            PublicationVenue = new S2PublicationVenue { Name = "Nature", Type = "Journal" },
            ExternalIds = new S2ExternalIds { DOI = "10.1234/test" }
        };

        var score = VenueQualityScorer.ComputeVenueQuality(extremePaper, null);
        Assert.True(score >= 0.0 && score <= 1.0, $"Score must be in [0, 1], got {score:F3}");
    }

    [Fact]
    public void S2Paper_Deserializes_VenueFields()
    {
        var json = """
        {
            "paperId": "abc123",
            "title": "Test Paper",
            "venue": "NeurIPS",
            "publicationVenue": {
                "name": "Neural Information Processing Systems",
                "type": "Conference",
                "issn": "1049-5258"
            },
            "citationCount": 100,
            "influentialCitationCount": 20
        }
        """;

        var paper = System.Text.Json.JsonSerializer.Deserialize<S2Paper>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(paper);
        Assert.Equal("NeurIPS", paper.Venue);
        Assert.NotNull(paper.PublicationVenue);
        Assert.Equal("Neural Information Processing Systems", paper.PublicationVenue.Name);
        Assert.Equal("Conference", paper.PublicationVenue.Type);
        Assert.Equal("1049-5258", paper.PublicationVenue.Issn);
    }
}
