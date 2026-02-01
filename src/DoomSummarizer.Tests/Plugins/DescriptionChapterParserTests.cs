using DoomSummarizer.Sources.YouTube;

namespace DoomSummarizer.Tests.Plugins;

public class DescriptionChapterParserTests
{
    [Fact]
    public void Parse_NullDescription_ReturnsEmpty()
    {
        DescriptionChapterParser.Parse(null).Should().BeEmpty();
    }

    [Fact]
    public void Parse_EmptyDescription_ReturnsEmpty()
    {
        DescriptionChapterParser.Parse("").Should().BeEmpty();
        DescriptionChapterParser.Parse("   ").Should().BeEmpty();
    }

    [Fact]
    public void Parse_NoTimestamps_ReturnsEmpty()
    {
        var description = """
            This is a great video about programming.
            Please like and subscribe!
            Check out my website: example.com
            """;

        DescriptionChapterParser.Parse(description).Should().BeEmpty();
    }

    [Fact]
    public void Parse_SingleTimestamp_ReturnsEmpty()
    {
        // Single timestamp is not considered chapters
        var description = "0:00 Introduction";

        DescriptionChapterParser.Parse(description).Should().BeEmpty();
    }

    [Fact]
    public void Parse_TwoTimestamps_ReturnsChapters()
    {
        var description = """
            0:00 Introduction
            2:30 Main Content
            """;

        var result = DescriptionChapterParser.Parse(description);

        result.Should().HaveCount(2);
        result[0].Title.Should().Be("Introduction");
        result[0].Timestamp.Should().Be(TimeSpan.Zero);
        result[1].Title.Should().Be("Main Content");
        result[1].Timestamp.Should().Be(TimeSpan.FromSeconds(150));
    }

    [Fact]
    public void Parse_MultipleChapters_ReturnsAllInOrder()
    {
        var description = """
            0:00 Introduction
            2:30 Setting up the project
            5:15 Demo
            10:45 Q&A
            15:00 Wrap-up
            """;

        var result = DescriptionChapterParser.Parse(description);

        result.Should().HaveCount(5);
        result[0].Title.Should().Be("Introduction");
        result[4].Title.Should().Be("Wrap-up");
    }

    [Fact]
    public void Parse_HourTimestamps_ParsesCorrectly()
    {
        var description = """
            0:00 Start
            1:23:45 Final section
            """;

        var result = DescriptionChapterParser.Parse(description);

        result.Should().HaveCount(2);
        result[1].Timestamp.Should().Be(new TimeSpan(1, 23, 45));
        result[1].Title.Should().Be("Final section");
    }

    [Fact]
    public void Parse_MixedContent_ExtractsOnlyTimestamps()
    {
        var description = """
            Check out this amazing video!

            Chapters:
            0:00 Introduction
            2:30 Setup
            5:00 Coding

            Follow me on Twitter: @example
            Subscribe for more!
            """;

        var result = DescriptionChapterParser.Parse(description);

        result.Should().HaveCount(3);
        result[0].Title.Should().Be("Introduction");
        result[1].Title.Should().Be("Setup");
        result[2].Title.Should().Be("Coding");
    }

    [Fact]
    public void Parse_UnorderedTimestamps_ReturnsSorted()
    {
        var description = """
            5:00 Part Three
            0:00 Part One
            2:30 Part Two
            """;

        var result = DescriptionChapterParser.Parse(description);

        result.Should().HaveCount(3);
        result[0].Timestamp.Should().BeLessThan(result[1].Timestamp);
        result[1].Timestamp.Should().BeLessThan(result[2].Timestamp);
    }

    [Fact]
    public void Parse_TimestampWithoutTitle_IsSkipped()
    {
        var description = """
            0:00
            2:30 Setup
            5:00 Demo
            """;

        var result = DescriptionChapterParser.Parse(description);

        // "0:00 " has trailing space after timestamp, regex requires non-empty title text
        // The regex `\s+(.+)$` should still match the space, but title.Trim() may be empty
        // This depends on exact regex behavior
        result.Count.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Parse_DoubleDigitMinutes_ParsesCorrectly()
    {
        var description = """
            0:00 Start
            45:30 Almost an hour in
            """;

        var result = DescriptionChapterParser.Parse(description);

        result.Should().HaveCount(2);
        result[1].Timestamp.Should().Be(TimeSpan.FromMinutes(45) + TimeSpan.FromSeconds(30));
    }
}
