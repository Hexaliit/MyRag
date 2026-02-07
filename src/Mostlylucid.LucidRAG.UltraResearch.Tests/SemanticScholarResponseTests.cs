using System.Text.Json;
using DoomSummarizer.Services;

namespace Mostlylucid.LucidRAG.UltraResearch.Tests;

public class SemanticScholarResponseTests
{
    [Fact]
    public void S2Paper_Deserializes_FromApiJson()
    {
        var json = """
        {
            "paperId": "abc123",
            "title": "Attention Is All You Need",
            "authors": [
                {"authorId": "1", "name": "Ashish Vaswani"},
                {"authorId": "2", "name": "Noam Shazeer"}
            ],
            "year": 2017,
            "abstract": "The dominant sequence transduction models...",
            "citationCount": 95000,
            "influentialCitationCount": 12000,
            "fieldsOfStudy": ["Computer Science"],
            "tldr": {
                "model": "tldr@v2.0.0",
                "text": "A new network architecture based solely on attention mechanisms..."
            },
            "externalIds": {
                "ArXiv": "1706.03762",
                "DOI": "10.48550/arXiv.1706.03762",
                "CorpusId": 13756489
            }
        }
        """;

        var paper = JsonSerializer.Deserialize<S2Paper>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(paper);
        Assert.Equal("abc123", paper.PaperId);
        Assert.Equal("Attention Is All You Need", paper.Title);
        Assert.Equal(2017, paper.Year);
        Assert.Equal(95000, paper.CitationCount);
        Assert.Equal(12000, paper.InfluentialCitationCount);
        Assert.NotNull(paper.Authors);
        Assert.Equal(2, paper.Authors.Count);
        Assert.Equal("Ashish Vaswani", paper.Authors[0].Name);
        Assert.Equal("1706.03762", paper.ArxivId);
        Assert.NotNull(paper.Tldr);
        Assert.Contains("attention mechanisms", paper.Tldr.Text);
    }

    [Fact]
    public void S2Paper_Deserializes_WithMinimalFields()
    {
        var json = """
        {
            "paperId": "xyz789",
            "title": "Some Paper"
        }
        """;

        var paper = JsonSerializer.Deserialize<S2Paper>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(paper);
        Assert.Equal("xyz789", paper.PaperId);
        Assert.Equal("Some Paper", paper.Title);
        Assert.Null(paper.Year);
        Assert.Null(paper.ArxivId);
        Assert.Null(paper.Doi);
        Assert.Equal(0, paper.CitationCount);
    }

    [Fact]
    public void S2SearchResponse_Deserializes()
    {
        var json = """
        {
            "total": 100,
            "data": [
                {
                    "paperId": "paper1",
                    "title": "First Paper",
                    "year": 2023,
                    "citationCount": 10
                },
                {
                    "paperId": "paper2",
                    "title": "Second Paper",
                    "year": 2024,
                    "citationCount": 5
                }
            ]
        }
        """;

        var response = JsonSerializer.Deserialize<S2SearchResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(response);
        Assert.Equal(100, response.Total);
        Assert.Equal(2, response.Data.Count);
        Assert.Equal("First Paper", response.Data[0].Title);
        Assert.Equal(2024, response.Data[1].Year);
    }

    [Fact]
    public void S2CitationResponse_Deserializes()
    {
        var json = """
        {
            "data": [
                {
                    "citingPaper": {
                        "paperId": "citer1",
                        "title": "A Paper That Cites",
                        "citationCount": 50
                    },
                    "isInfluential": true
                },
                {
                    "citingPaper": {
                        "paperId": "citer2",
                        "title": "Another Citing Paper"
                    },
                    "isInfluential": false
                }
            ]
        }
        """;

        var response = JsonSerializer.Deserialize<S2CitationResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(response);
        Assert.NotNull(response.Data);
        Assert.Equal(2, response.Data.Count);
        Assert.True(response.Data[0].IsInfluential);
        Assert.Equal("A Paper That Cites", response.Data[0].CitingPaper?.Title);
        Assert.False(response.Data[1].IsInfluential);
    }

    [Fact]
    public void S2ReferenceResponse_Deserializes()
    {
        var json = """
        {
            "data": [
                {
                    "citedPaper": {
                        "paperId": "ref1",
                        "title": "Referenced Paper",
                        "year": 2015,
                        "externalIds": {
                            "DOI": "10.1234/ref1"
                        }
                    },
                    "isInfluential": true
                }
            ]
        }
        """;

        var response = JsonSerializer.Deserialize<S2ReferenceResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(response);
        Assert.NotNull(response.Data);
        Assert.Single(response.Data);
        Assert.Equal("10.1234/ref1", response.Data[0].CitedPaper?.Doi);
    }
}
