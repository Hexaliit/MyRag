using DoomSummarizer.Commands;
using DoomSummarizer.Models;

namespace DoomSummarizer.Tests;

/// <summary>
/// Tests for ScrollCommand static helper methods:
/// StripMarkdownForLlm, ApplySourceDomainFilter, ApplySourceWeights.
/// </summary>
public class ScrollCommandHelperTests
{
    private static ContentItem MakeItem(string id, string source = "test", string? url = null, double relevance = 0.5)
    {
        return new ContentItem
        {
            Id = id,
            Source = source,
            Title = $"Item {id}",
            Url = url ?? $"https://{source}.com/article/{id}",
            RelevanceScore = relevance,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    #region StripMarkdownForLlm

    [Fact]
    public void StripMarkdown_RemovesHeaders()
    {
        var result = ScrollCommand.StripMarkdownForLlm("# Hello\n## World\n### Subheading\nContent here");
        result.Should().NotContain("#");
        result.Should().Contain("Hello");
        result.Should().Contain("Content here");
    }

    [Fact]
    public void StripMarkdown_RemovesCompleteImageSyntax()
    {
        var result = ScrollCommand.StripMarkdownForLlm("![Alt text](https://img.com/photo.jpg)");
        result.Should().NotContain("![");
        result.Should().NotContain("](");
        result.Should().Contain("Alt text");
    }

    [Fact]
    public void StripMarkdown_RemovesTruncatedImageSyntax()
    {
        // Truncated image: URL cut off without closing paren
        var result = ScrollCommand.StripMarkdownForLlm("![Gemini 3 decoded](https://example.com/very-long-url-that-gets-truncated-befo");
        result.Should().NotContain("![");
        result.Should().NotContain("](");
        result.Should().Contain("Gemini 3 decoded");
    }

    [Fact]
    public void StripMarkdown_RemovesBareImageStart()
    {
        var result = ScrollCommand.StripMarkdownForLlm("![Some image at start of line");
        result.Should().NotStartWith("![");
    }

    [Fact]
    public void StripMarkdown_ConvertsLinksToText()
    {
        var result = ScrollCommand.StripMarkdownForLlm("Check out [this article](https://example.com) for details.");
        result.Should().NotContain("[");
        result.Should().NotContain("](");
        result.Should().Contain("this article");
        result.Should().Contain("for details");
    }

    [Fact]
    public void StripMarkdown_RemovesTruncatedLinks()
    {
        var result = ScrollCommand.StripMarkdownForLlm("[link text](https://example.com/truncated-ur");
        result.Should().NotContain("[");
        result.Should().Contain("link text");
    }

    [Fact]
    public void StripMarkdown_RemovesEmphasis()
    {
        var result = ScrollCommand.StripMarkdownForLlm("This is **bold** and *italic* and __underline__.");
        result.Should().NotContain("**");
        result.Should().NotContain("*italic*");
        result.Should().Contain("bold");
        result.Should().Contain("italic");
    }

    [Fact]
    public void StripMarkdown_RemovesCodeFences()
    {
        var result = ScrollCommand.StripMarkdownForLlm("```csharp\nvar x = 1;\n```");
        result.Should().NotContain("```");
        result.Should().Contain("var x = 1;");
    }

    [Fact]
    public void StripMarkdown_RemovesInlineCode()
    {
        var result = ScrollCommand.StripMarkdownForLlm("Use the `dotnet build` command.");
        result.Should().NotContain("`");
        result.Should().Contain("dotnet build");
    }

    [Fact]
    public void StripMarkdown_RemovesListMarkers()
    {
        var result = ScrollCommand.StripMarkdownForLlm("- First item\n* Second item\n1. Third item");
        result.Should().NotContain("- ");
        result.Should().NotContain("* ");
        result.Should().Contain("First item");
        result.Should().Contain("Third item");
    }

    [Fact]
    public void StripMarkdown_CollapsesWhitespace()
    {
        var result = ScrollCommand.StripMarkdownForLlm("Hello    world\n\n\nmore   text");
        result.Should().Be("Hello world more text");
    }

    [Fact]
    public void StripMarkdown_HandlesEmptyAndNull()
    {
        ScrollCommand.StripMarkdownForLlm("").Should().BeEmpty();
        ScrollCommand.StripMarkdownForLlm("   ").Should().BeEmpty();
    }

    [Fact]
    public void StripMarkdown_RemovesEscapedChars()
    {
        var result = ScrollCommand.StripMarkdownForLlm(@"Escaped \( parentheses \) and \[ brackets \]");
        result.Should().Contain("( parentheses )");
        result.Should().Contain("[ brackets ]");
        result.Should().NotContain(@"\(");
    }

    #endregion

    #region ApplySourceDomainFilter

    [Fact]
    public void DomainFilter_AllowList_KeepsOnlyMatching()
    {
        var items = new List<ContentItem>
        {
            MakeItem("1", url: "https://www.bbc.com/news/article"),
            MakeItem("2", url: "https://cnn.com/story"),
            MakeItem("3", url: "https://www.bbc.co.uk/news")
        };
        var filter = new SourceFilterConfig
        {
            AllowedDomains = ["bbc.com", "bbc.co.uk"]
        };

        var result = ScrollCommand.ApplySourceDomainFilter(items, filter);
        result.Should().HaveCount(2);
        result.Select(i => i.Id).Should().BeEquivalentTo(["1", "3"]);
    }

    [Fact]
    public void DomainFilter_BlockList_RemovesMatching()
    {
        var items = new List<ContentItem>
        {
            MakeItem("1", url: "https://bbc.com/article"),
            MakeItem("2", url: "https://facebook.com/post"),
            MakeItem("3", url: "https://reuters.com/news")
        };
        var filter = new SourceFilterConfig
        {
            BlockedDomains = ["facebook.com"]
        };

        var result = ScrollCommand.ApplySourceDomainFilter(items, filter);
        result.Should().HaveCount(2);
        result.Select(i => i.Id).Should().BeEquivalentTo(["1", "3"]);
    }

    [Fact]
    public void DomainFilter_KeepsItemsWithoutUrl()
    {
        var item = new ContentItem
        {
            Id = "no-url",
            Source = "test",
            Title = "Local item",
            Url = null,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var filter = new SourceFilterConfig
        {
            AllowedDomains = ["bbc.com"]
        };

        var result = ScrollCommand.ApplySourceDomainFilter([item], filter);
        result.Should().HaveCount(1);
    }

    [Fact]
    public void DomainFilter_EmptyConfig_KeepsAll()
    {
        var items = new List<ContentItem>
        {
            MakeItem("1", url: "https://bbc.com/a"),
            MakeItem("2", url: "https://cnn.com/b")
        };
        var filter = new SourceFilterConfig();

        var result = ScrollCommand.ApplySourceDomainFilter(items, filter);
        result.Should().HaveCount(2);
    }

    #endregion

    #region ApplySourceWeights

    [Fact]
    public void SourceWeights_MatchesBySourceName()
    {
        var items = new List<ContentItem>
        {
            MakeItem("1", source: "hn", relevance: 0.5),
            MakeItem("2", source: "reddit", relevance: 0.5)
        };
        var filter = new SourceFilterConfig
        {
            Weights = new Dictionary<string, double>
            {
                ["hn"] = 1.4,
                ["reddit"] = 0.8
            }
        };

        var adjusted = ScrollCommand.ApplySourceWeights(items, filter);
        adjusted.Should().Be(2);
        items[0].RelevanceScore.Should().BeApproximately(0.7, 0.01); // 0.5 * 1.4
        items[1].RelevanceScore.Should().BeApproximately(0.4, 0.01); // 0.5 * 0.8
    }

    [Fact]
    public void SourceWeights_FallsBackToDomainMatch()
    {
        var items = new List<ContentItem>
        {
            MakeItem("1", source: "search", url: "https://www.reuters.com/article/123", relevance: 0.5)
        };
        var filter = new SourceFilterConfig
        {
            Weights = new Dictionary<string, double>
            {
                ["search"] = 0.8 // Source name matches first
            }
        };

        ScrollCommand.ApplySourceWeights(items, filter);
        items[0].RelevanceScore.Should().BeApproximately(0.4, 0.01); // 0.5 * 0.8
    }

    [Fact]
    public void SourceWeights_CapsAtOne()
    {
        var items = new List<ContentItem>
        {
            MakeItem("1", source: "bbc", relevance: 0.9)
        };
        var filter = new SourceFilterConfig
        {
            Weights = new Dictionary<string, double> { ["bbc"] = 1.5 }
        };

        ScrollCommand.ApplySourceWeights(items, filter);
        items[0].RelevanceScore.Should().Be(1.0); // Capped at 1.0
    }

    [Fact]
    public void SourceWeights_IgnoresWeight1()
    {
        var items = new List<ContentItem>
        {
            MakeItem("1", source: "neutral", relevance: 0.5)
        };
        var filter = new SourceFilterConfig
        {
            Weights = new Dictionary<string, double> { ["neutral"] = 1.0 }
        };

        var adjusted = ScrollCommand.ApplySourceWeights(items, filter);
        adjusted.Should().Be(0); // Weight 1.0 = no adjustment
    }

    #endregion
}
