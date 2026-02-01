using DoomSummarizer.Plugins.Subtitles;

namespace DoomSummarizer.Tests.Plugins;

public class ChapterDetectorTests
{
    [Fact]
    public void DetectChapters_EmptyEntries_ReturnsEmpty()
    {
        var result = ChapterDetector.DetectChapters([]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void DetectChapters_SingleEntry_ReturnsSingleChapter()
    {
        var entries = new[]
        {
            new SubtitleEntry(TimeSpan.Zero, TimeSpan.FromSeconds(4), "Hello world")
        };

        var result = ChapterDetector.DetectChapters(entries);

        result.Should().HaveCount(1);
        result[0].StartTime.Should().Be(TimeSpan.Zero);
        result[0].StartIndex.Should().Be(0);
    }

    [Fact]
    public void DetectChapters_ContinuousEntries_ReturnsSingleChapter()
    {
        // Entries with small gaps (< 5 seconds) — no chapter boundaries
        var entries = new[]
        {
            new SubtitleEntry(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(3), "Hello"),
            new SubtitleEntry(TimeSpan.FromSeconds(3.5), TimeSpan.FromSeconds(7), "World"),
            new SubtitleEntry(TimeSpan.FromSeconds(7.2), TimeSpan.FromSeconds(11), "Test"),
        };

        var result = ChapterDetector.DetectChapters(entries);

        result.Should().HaveCount(1, "no gaps exceed the 5-second threshold");
    }

    [Fact]
    public void DetectChapters_LargeGap_CreatesNewChapter()
    {
        var entries = new[]
        {
            new SubtitleEntry(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(4), "First section content"),
            new SubtitleEntry(TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(8), "More first section"),
            // 10-second gap here
            new SubtitleEntry(TimeSpan.FromSeconds(18), TimeSpan.FromSeconds(22), "Second section content"),
            new SubtitleEntry(TimeSpan.FromSeconds(22.5), TimeSpan.FromSeconds(26), "More second section"),
        };

        var result = ChapterDetector.DetectChapters(entries);

        result.Should().HaveCount(2);
        result[0].StartIndex.Should().Be(0);
        result[1].StartIndex.Should().Be(2);
        result[1].StartTime.Should().Be(TimeSpan.FromSeconds(18));
    }

    [Fact]
    public void DetectChapters_MusicMarker_CreatesNewChapter()
    {
        var entries = new[]
        {
            new SubtitleEntry(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(4), "Welcome to the show"),
            new SubtitleEntry(TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(8), "[Music]"),
            new SubtitleEntry(TimeSpan.FromSeconds(8.5), TimeSpan.FromSeconds(12), "Now for the next topic"),
        };

        var result = ChapterDetector.DetectChapters(entries);

        result.Should().HaveCount(2, "[Music] marker should trigger a chapter boundary");
        result[1].StartIndex.Should().Be(2);
    }

    [Fact]
    public void DetectChapters_AllCapsTitle_CreatesNewChapter()
    {
        var entries = new[]
        {
            new SubtitleEntry(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(4), "Some normal content here"),
            new SubtitleEntry(TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(8), "INTRODUCTION"),
            new SubtitleEntry(TimeSpan.FromSeconds(8.5), TimeSpan.FromSeconds(12), "More regular content"),
        };

        var result = ChapterDetector.DetectChapters(entries);

        result.Should().HaveCount(2, "all-caps title should trigger a chapter boundary");
        result[1].Title.Should().Be("INTRODUCTION");
    }

    [Fact]
    public void DetectChapters_AllCapsLongText_DoesNotTrigger()
    {
        var entries = new[]
        {
            new SubtitleEntry(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(4), "Hello"),
            new SubtitleEntry(TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(8),
                "THIS IS A VERY LONG ALL CAPS SENTENCE THAT HAS MORE THAN TEN WORDS IN IT"),
        };

        var result = ChapterDetector.DetectChapters(entries);

        result.Should().HaveCount(1, "all-caps text with > 10 words is not a title");
    }

    [Fact]
    public void DetectChapters_CustomGapThreshold_UsesCustomValue()
    {
        var entries = new[]
        {
            new SubtitleEntry(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(4), "First"),
            // 3-second gap
            new SubtitleEntry(TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(11), "Second"),
        };

        // Default 5-second threshold — no chapter break
        var resultDefault = ChapterDetector.DetectChapters(entries, gapThresholdSeconds: 5.0);
        resultDefault.Should().HaveCount(1);

        // 2-second threshold — should detect chapter
        var resultCustom = ChapterDetector.DetectChapters(entries, gapThresholdSeconds: 2.0);
        resultCustom.Should().HaveCount(2);
    }

    [Fact]
    public void DetectChapters_MultipleMarkerTypes_DetectsAll()
    {
        var entries = new[]
        {
            new SubtitleEntry(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(4), "Intro content"),
            new SubtitleEntry(TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(8), "[Applause]"),
            new SubtitleEntry(TimeSpan.FromSeconds(8.5), TimeSpan.FromSeconds(12), "After applause"),
            // Large gap
            new SubtitleEntry(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(24), "After big gap"),
            new SubtitleEntry(TimeSpan.FromSeconds(24.5), TimeSpan.FromSeconds(28), "FINAL SECTION"),
            new SubtitleEntry(TimeSpan.FromSeconds(28.5), TimeSpan.FromSeconds(32), "Final content"),
        };

        var result = ChapterDetector.DetectChapters(entries);

        result.Count.Should().BeGreaterThanOrEqualTo(3, "gap, marker, and title all trigger boundaries");
    }

    [Fact]
    public void FormatTimestamp_UnderOneHour_OmitsHours()
    {
        var ts = TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(30));

        var result = ChapterDetector.FormatTimestamp(ts);

        result.Should().Be("5:30");
    }

    [Fact]
    public void FormatTimestamp_OverOneHour_IncludesHours()
    {
        var ts = new TimeSpan(1, 23, 45);

        var result = ChapterDetector.FormatTimestamp(ts);

        result.Should().Be("1:23:45");
    }

    [Fact]
    public void FormatTimestamp_Zero_ReturnsZero()
    {
        var result = ChapterDetector.FormatTimestamp(TimeSpan.Zero);

        result.Should().Be("0:00");
    }
}
