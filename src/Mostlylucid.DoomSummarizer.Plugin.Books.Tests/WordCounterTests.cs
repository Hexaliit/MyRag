using DoomSummarizer.Helpers;

namespace Mostlylucid.DoomSummarizer.Plugin.Books.Tests;

public class WordCounterTests
{
    [Fact]
    public void Count_EmptyString_ReturnsZero()
    {
        WordCounter.Count("").Should().Be(0);
    }

    [Fact]
    public void Count_Null_ReturnsZero()
    {
        WordCounter.Count((string?)null).Should().Be(0);
    }

    [Fact]
    public void Count_SingleWord_ReturnsOne()
    {
        WordCounter.Count("hello").Should().Be(1);
    }

    [Fact]
    public void Count_MultipleWords_ReturnsCorrectCount()
    {
        WordCounter.Count("the quick brown fox jumps").Should().Be(5);
    }

    [Fact]
    public void Count_MultipleSpaces_IgnoresExtra()
    {
        WordCounter.Count("hello   world").Should().Be(2);
    }

    [Fact]
    public void Count_TabsAndNewlines_TreatedAsSeparators()
    {
        WordCounter.Count("hello\tworld\nfoo\rbar").Should().Be(4);
    }

    [Fact]
    public void Count_LeadingTrailingWhitespace_Ignored()
    {
        WordCounter.Count("  hello world  ").Should().Be(2);
    }

    [Fact]
    public void Count_LargeText_HandlesEfficiently()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 100_000));
        WordCounter.Count(text).Should().Be(100_000);
    }

    [Fact]
    public void Count_MatchesSplitBehavior()
    {
        var samples = new[]
        {
            "Hello world",
            "  leading spaces",
            "trailing spaces  ",
            "multiple   spaces   between",
            "tabs\there",
            "newlines\nhere\ntoo",
        };

        foreach (var sample in samples)
        {
            var expected = sample.Split(
                [' ', '\t', '\n', '\r'],
                StringSplitOptions.RemoveEmptyEntries).Length;

            WordCounter.Count(sample).Should().Be(expected, because: $"sample: '{sample}'");
        }
    }
}
