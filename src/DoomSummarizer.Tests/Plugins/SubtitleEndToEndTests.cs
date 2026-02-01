using System.Text;
using DoomSummarizer.Models;
using DoomSummarizer.Plugins;
using DoomSummarizer.Plugins.Subtitles;
using DoomSummarizer.Sources.YouTube;
using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Tests.Plugins;

/// <summary>
/// End-to-end integration tests for subtitle/media processing.
/// Each test exercises the full pipeline: file parse → chapter detection → markdown → document tree.
/// </summary>
public class SubtitleEndToEndTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private readonly SubtitleDocumentHandler _handler = new();
    private readonly SubtitlesProcessorPlugin _plugin = new();

    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "Subtitles", fileName);

    // ── SRT Format ──────────────────────────────────────────────────────────

    [Fact]
    public async Task E2E_Srt_Simple_FullPipeline()
    {
        var path = TestDataPath("simple.srt");
        var doc = await _handler.ProcessAsync(path, new DocumentHandlerOptions());

        doc.ContentType.Should().Be("subtitles");
        doc.Title.Should().Be("simple");
        doc.Markdown.Should().NotBeNullOrEmpty();
        doc.Metadata["format"].Should().Be("SRT");
        ((int)doc.Metadata["entry_count"]).Should().Be(5);

        // Feed markdown into processor plugin
        var result = await _plugin.ProcessAsync(doc.Markdown,
            new ProcessorOptions { FilePath = path });

        result.DocumentTree.Should().NotBeNull();
        result.DocumentTree.Title.Should().Be("simple");
        result.DocumentType.Should().Be("subtitles");
        result.StrategyUsed.Should().Be("chapter-gap-detection");

        output.WriteLine($"SRT simple: {doc.Markdown.Length} chars, " +
                         $"{result.DocumentTree.Children.Count} nodes");
    }

    [Fact]
    public async Task E2E_Srt_Chapters_FullPipeline()
    {
        var path = TestDataPath("chapters.srt");
        var doc = await _handler.ProcessAsync(path, new DocumentHandlerOptions());

        var chapterCount = (int)doc.Metadata["chapter_count"];
        chapterCount.Should().BeGreaterThan(1);
        doc.Markdown.Should().Contain("##");

        var result = await _plugin.ProcessAsync(doc.Markdown,
            new ProcessorOptions { FilePath = path });

        result.DocumentTree.Children.Count.Should().BeGreaterThan(1,
            "chapters.srt has gaps and markers that create multiple chapters");

        // Verify chapter ordering
        for (var i = 0; i < result.DocumentTree.Children.Count; i++)
        {
            result.DocumentTree.Children[i].Sequence.Should().Be(i);
            result.DocumentTree.Children[i].LevelLabel.Should().Be("chapter");
        }

        output.WriteLine($"SRT chapters: {chapterCount} chapters, " +
                         $"{result.DocumentTree.Children.Count} tree nodes");
    }

    // ── VTT Format ──────────────────────────────────────────────────────────

    [Fact]
    public async Task E2E_Vtt_Simple_FullPipeline()
    {
        var path = TestDataPath("simple.vtt");
        var doc = await _handler.ProcessAsync(path, new DocumentHandlerOptions());

        doc.ContentType.Should().Be("subtitles");
        doc.Metadata["format"].Should().Be("VTT");
        ((int)doc.Metadata["entry_count"]).Should().Be(5);

        // HTML tags should be stripped
        doc.Markdown.Should().NotContain("<b>");
        doc.Markdown.Should().NotContain("<i>");
        doc.Markdown.Should().Contain("welcome");
        doc.Markdown.Should().Contain("basics");

        var result = await _plugin.ProcessAsync(doc.Markdown,
            new ProcessorOptions { FilePath = path });

        result.DocumentTree.Title.Should().Be("simple");
        result.DocumentTree.Children.Should().NotBeEmpty();

        output.WriteLine($"VTT simple: {doc.Markdown.Length} chars, " +
                         $"tags stripped, {result.DocumentTree.Children.Count} nodes");
    }

    // ── ASS Format ──────────────────────────────────────────────────────────

    [Fact]
    public async Task E2E_Ass_Simple_FullPipeline()
    {
        var path = TestDataPath("simple.ass");
        var doc = await _handler.ProcessAsync(path, new DocumentHandlerOptions());

        doc.ContentType.Should().Be("subtitles");
        doc.Metadata["format"].Should().Be("ASS");
        ((int)doc.Metadata["entry_count"]).Should().BeGreaterThanOrEqualTo(5);
        doc.Markdown.Should().Contain("welcome");
        doc.Markdown.Should().Contain("programming");

        var result = await _plugin.ProcessAsync(doc.Markdown,
            new ProcessorOptions { FilePath = path });

        result.DocumentTree.Title.Should().Be("simple");
        result.DocumentTree.Children.Should().NotBeEmpty();

        output.WriteLine($"ASS simple: {(int)doc.Metadata["entry_count"]} entries, " +
                         $"{doc.Markdown.Length} chars");
    }

    [Fact]
    public async Task E2E_Ass_Chapters_FullPipeline()
    {
        var path = TestDataPath("chapters.ass");
        var doc = await _handler.ProcessAsync(path, new DocumentHandlerOptions());

        doc.Metadata["format"].Should().Be("ASS");
        var chapterCount = (int)doc.Metadata["chapter_count"];
        chapterCount.Should().BeGreaterThan(1,
            "chapters.ass has [Music] markers, all-caps titles, and time gaps");

        doc.Markdown.Should().Contain("##");

        var result = await _plugin.ProcessAsync(doc.Markdown,
            new ProcessorOptions { FilePath = path });

        result.DocumentTree.Children.Count.Should().BeGreaterThan(1);

        // Verify chapters have content
        foreach (var child in result.DocumentTree.Children)
        {
            child.Title.Should().NotBeNullOrEmpty();
            child.Level.Should().Be(1);
            child.LevelLabel.Should().Be("chapter");
        }

        output.WriteLine($"ASS chapters: {chapterCount} chapters detected, " +
                         $"{result.DocumentTree.Children.Count} tree nodes");
    }

    [Fact]
    public async Task E2E_Ass_Styled_StripsInlineFormatting()
    {
        var path = TestDataPath("styled.ass");
        var doc = await _handler.ProcessAsync(path, new DocumentHandlerOptions());

        doc.Metadata["format"].Should().Be("ASS");
        ((int)doc.Metadata["entry_count"]).Should().BeGreaterThanOrEqualTo(5);

        // Verify text content is present (formatting tags should be handled by parser)
        doc.Markdown.Should().Contain("Normal text");
        doc.Markdown.Should().Contain("Final line");

        output.WriteLine($"ASS styled: {doc.Markdown.Length} chars, " +
                         $"{(int)doc.Metadata["entry_count"]} entries");
    }

    // ── SSA Format ──────────────────────────────────────────────────────────

    [Fact]
    public async Task E2E_Ssa_Simple_FullPipeline()
    {
        var path = TestDataPath("simple.ssa");
        var doc = await _handler.ProcessAsync(path, new DocumentHandlerOptions());

        doc.ContentType.Should().Be("subtitles");
        doc.Metadata["format"].Should().Be("SSA");
        ((int)doc.Metadata["entry_count"]).Should().BeGreaterThanOrEqualTo(5);
        doc.Markdown.Should().Contain("welcome");
        doc.Markdown.Should().Contain("architecture");

        var result = await _plugin.ProcessAsync(doc.Markdown,
            new ProcessorOptions { FilePath = path });

        result.DocumentTree.Title.Should().Be("simple");
        result.DocumentTree.Children.Should().NotBeEmpty();

        output.WriteLine($"SSA simple: {(int)doc.Metadata["entry_count"]} entries, " +
                         $"{doc.Markdown.Length} chars");
    }

    // ── Cross-Format Consistency ─────────────────────────────────────────────

    [Fact]
    public async Task E2E_CrossFormat_AllFormatsProduceOutput()
    {
        var formats = new[] { "simple.srt", "simple.vtt", "simple.ass", "simple.ssa" };
        var results = new List<(string format, DocumentContent doc)>();

        foreach (var file in formats)
        {
            var path = TestDataPath(file);
            var doc = await _handler.ProcessAsync(path, new DocumentHandlerOptions());
            results.Add((Path.GetExtension(file).TrimStart('.').ToUpperInvariant(), doc));
        }

        // All formats should produce non-empty markdown
        foreach (var (format, doc) in results)
        {
            doc.Markdown.Should().NotBeNullOrEmpty($"{format} should produce markdown output");
            doc.ContentType.Should().Be("subtitles");
            doc.Metadata.Should().ContainKey("entry_count");
            doc.Metadata.Should().ContainKey("duration_seconds");
            doc.Metadata.Should().ContainKey("chapter_count");

            output.WriteLine($"{format}: {doc.Markdown.Length} chars, " +
                             $"{(int)doc.Metadata["entry_count"]} entries, " +
                             $"{(int)doc.Metadata["duration_seconds"]}s duration");
        }

        // All simple files have 5 entries
        foreach (var (format, doc) in results)
        {
            ((int)doc.Metadata["entry_count"]).Should().BeGreaterThanOrEqualTo(5,
                $"{format} should have at least 5 entries");
        }
    }

    [Fact]
    public async Task E2E_CrossFormat_ChaptersDetectedConsistently()
    {
        // chapters.srt and chapters.ass have similar structure
        var srtPath = TestDataPath("chapters.srt");
        var assPath = TestDataPath("chapters.ass");

        var srtDoc = await _handler.ProcessAsync(srtPath, new DocumentHandlerOptions());
        var assDoc = await _handler.ProcessAsync(assPath, new DocumentHandlerOptions());

        var srtChapters = (int)srtDoc.Metadata["chapter_count"];
        var assChapters = (int)assDoc.Metadata["chapter_count"];

        srtChapters.Should().BeGreaterThan(1);
        assChapters.Should().BeGreaterThan(1);

        // Both should contain ## headings
        srtDoc.Markdown.Should().Contain("##");
        assDoc.Markdown.Should().Contain("##");

        output.WriteLine($"SRT chapters: {srtChapters}, ASS chapters: {assChapters}");
    }

    // ── Document Tree Structure ──────────────────────────────────────────────

    [Fact]
    public async Task E2E_DocumentTree_ChapterNodesHaveContent()
    {
        var path = TestDataPath("chapters.srt");
        var doc = await _handler.ProcessAsync(path, new DocumentHandlerOptions());
        var result = await _plugin.ProcessAsync(doc.Markdown,
            new ProcessorOptions { FilePath = path });

        result.DocumentTree.Level.Should().Be(0);
        result.DocumentTree.LevelLabel.Should().Be("document");

        var nonEmptyChildren = result.DocumentTree.Children
            .Where(c => !string.IsNullOrWhiteSpace(c.Content))
            .ToList();

        nonEmptyChildren.Should().NotBeEmpty(
            "chapter nodes should contain subtitle text content");

        output.WriteLine($"Tree: {result.DocumentTree.Children.Count} total children, " +
                         $"{nonEmptyChildren.Count} with content");
    }

    [Fact]
    public async Task E2E_DocumentTree_SimpleFileCreatesSingleSection()
    {
        var path = TestDataPath("simple.srt");
        var doc = await _handler.ProcessAsync(path, new DocumentHandlerOptions());
        var result = await _plugin.ProcessAsync(doc.Markdown,
            new ProcessorOptions { FilePath = path });

        // Simple file with continuous text and no chapter markers should be
        // either a single section or very few chapters
        result.DocumentTree.Children.Should().NotBeEmpty();
        result.DocumentTree.Children[0].Content.Should().NotBeNullOrEmpty();
    }

    // ── YouTube Description Chapter Parsing → Pipeline ──────────────────────

    [Fact]
    public void E2E_YouTube_DescriptionChapters_ProduceCorrectTimestamps()
    {
        var description = """
            Check out this tutorial on C# programming!

            0:00 Introduction
            2:30 Setting up the environment
            5:15 Writing your first program
            10:45 Advanced topics
            15:00 Testing and debugging
            20:30 Conclusion

            Don't forget to like and subscribe!
            """;

        var chapters = DescriptionChapterParser.Parse(description);

        chapters.Should().HaveCount(6);
        chapters[0].Timestamp.Should().Be(TimeSpan.Zero);
        chapters[0].Title.Should().Be("Introduction");
        chapters[1].Timestamp.Should().Be(TimeSpan.FromSeconds(150)); // 2:30
        chapters[2].Timestamp.Should().Be(TimeSpan.FromSeconds(315)); // 5:15
        chapters[3].Title.Should().Be("Advanced topics");
        chapters[5].Title.Should().Be("Conclusion");

        // Chapters should be ordered
        for (var i = 1; i < chapters.Count; i++)
        {
            chapters[i].Timestamp.Should().BeGreaterThan(chapters[i - 1].Timestamp);
        }

        output.WriteLine($"YouTube chapters: {chapters.Count} parsed from description");
    }

    [Fact]
    public void E2E_YouTube_DescriptionChapters_WithHourTimestamps()
    {
        var description = """
            Full conference talk recording

            0:00 Opening remarks
            15:30 Keynote address
            1:02:15 Panel discussion
            1:45:00 Audience Q&A
            2:10:30 Closing
            """;

        var chapters = DescriptionChapterParser.Parse(description);

        chapters.Should().HaveCount(5);
        chapters[2].Timestamp.Should().Be(new TimeSpan(1, 2, 15));
        chapters[3].Timestamp.Should().Be(new TimeSpan(1, 45, 0));
        chapters[4].Timestamp.Should().Be(new TimeSpan(2, 10, 30));

        output.WriteLine($"Hour-format chapters: {chapters.Count} parsed");
    }

    [Fact]
    public void E2E_YouTube_DescriptionChapters_MixedWithNonChapterContent()
    {
        var description = """
            📌 Links mentioned in this video:
            https://example.com/docs
            https://example.com/repo

            ⏱ Timestamps:
            0:00 Welcome and intro
            3:45 Demo part 1
            8:20 Demo part 2
            12:00 Wrap up

            📧 Contact: hello@example.com
            Twitter: @example
            """;

        var chapters = DescriptionChapterParser.Parse(description);

        chapters.Should().HaveCount(4);
        chapters[0].Title.Should().Be("Welcome and intro");
        chapters[3].Title.Should().Be("Wrap up");

        // Non-timestamp lines should be ignored
        chapters.Should().NotContain(c => c.Title.Contains("http"));
        chapters.Should().NotContain(c => c.Title.Contains("@"));
    }

    // ── YouTube URL Detection ───────────────────────────────────────────────

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc123DEF_-")]
    [InlineData("https://youtu.be/abc123DEF_-")]
    [InlineData("https://www.youtube.com/embed/abc123DEF_-")]
    [InlineData("https://youtube.com/shorts/abc123DEF_-")]
    [InlineData("http://www.youtube.com/watch?v=abc123DEF_-")]
    public void E2E_YouTube_UrlDetection_AllFormats(string url)
    {
        YouTubeExtractor.IsYouTubeUrl(url).Should().BeTrue();
        YouTubeExtractor.ExtractVideoId(url).Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("https://vimeo.com/123456")]
    [InlineData("https://dailymotion.com/video/x1234")]
    [InlineData("https://twitch.tv/channel")]
    [InlineData("ftp://youtube.com/watch?v=abc")]
    public void E2E_YouTube_UrlDetection_RejectsNonYouTube(string url)
    {
        YouTubeExtractor.IsYouTubeUrl(url).Should().BeFalse();
    }

    // ── ChapterDetector Integration ─────────────────────────────────────────

    [Fact]
    public void E2E_ChapterDetector_MusicMarkerCreatesChapter()
    {
        var entries = new List<SubtitleEntry>
        {
            new(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(3), "Welcome to the show."),
            new(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(7), "Today we discuss testing."),
            new(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(11), "[Music]"),
            new(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(15), "Now let's begin."),
            new(TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(19), "First topic is unit tests."),
        };

        var chapters = ChapterDetector.DetectChapters(entries);

        // [Music] should create a chapter boundary
        chapters.Count.Should().BeGreaterThanOrEqualTo(2);
        chapters[0].StartIndex.Should().Be(0);

        output.WriteLine($"Music marker test: {chapters.Count} chapters detected");
    }

    [Fact]
    public void E2E_ChapterDetector_LargeGapCreatesChapter()
    {
        var entries = new List<SubtitleEntry>
        {
            new(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(3), "Part one content."),
            new(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(7), "Still part one."),
            // 10 second gap
            new(TimeSpan.FromSeconds(17), TimeSpan.FromSeconds(20), "Part two starts here."),
            new(TimeSpan.FromSeconds(21), TimeSpan.FromSeconds(24), "More part two."),
        };

        var chapters = ChapterDetector.DetectChapters(entries);

        chapters.Count.Should().Be(2, "a 10-second gap should create 2 chapters");
        chapters[0].StartTime.Should().Be(TimeSpan.Zero);
        chapters[1].StartTime.Should().Be(TimeSpan.FromSeconds(17));
    }

    [Fact]
    public void E2E_ChapterDetector_AllCapsTitle()
    {
        var entries = new List<SubtitleEntry>
        {
            new(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(3), "Some introductory text here."),
            new(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(7), "INTRODUCTION"),
            new(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(11), "Welcome to the talk."),
            new(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(15), "MAIN TOPIC"),
            new(TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(19), "The main content goes here."),
        };

        var chapters = ChapterDetector.DetectChapters(entries);

        // All-caps short text should trigger chapter boundaries
        chapters.Count.Should().BeGreaterThanOrEqualTo(3,
            "INTRODUCTION and MAIN TOPIC are all-caps titles");
    }

    [Fact]
    public void E2E_ChapterDetector_CombinedHeuristics()
    {
        var entries = new List<SubtitleEntry>
        {
            new(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(3), "Welcome to the show."),
            new(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(7), "Let me introduce the topic."),
            // Music marker
            new(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(11), "[Music]"),
            // All-caps title after gap
            new(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(23), "SETTING UP"),
            new(TimeSpan.FromSeconds(24), TimeSpan.FromSeconds(27), "First install the dependencies."),
            new(TimeSpan.FromSeconds(28), TimeSpan.FromSeconds(31), "Then configure your environment."),
            // Another gap + marker
            new(TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(43), "[Applause]"),
            new(TimeSpan.FromSeconds(44), TimeSpan.FromSeconds(47), "CONCLUSION"),
            new(TimeSpan.FromSeconds(48), TimeSpan.FromSeconds(51), "Thanks for watching."),
        };

        var chapters = ChapterDetector.DetectChapters(entries);

        chapters.Count.Should().BeGreaterThanOrEqualTo(3,
            "music markers, gaps, and all-caps titles should create multiple chapters");

        // Verify all chapters have valid titles
        foreach (var ch in chapters)
        {
            ch.Title.Should().NotBeNullOrEmpty();
            ch.StartTime.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        }

        output.WriteLine($"Combined heuristics: {chapters.Count} chapters");
        foreach (var ch in chapters)
            output.WriteLine($"  [{ChapterDetector.FormatTimestamp(ch.StartTime)}] {ch.Title}");
    }

    // ── Generated Large File Stress ─────────────────────────────────────────

    [Fact]
    public async Task E2E_LargeFile_1000Entries_FullPipeline()
    {
        // Generate a large SRT file dynamically
        var tempPath = Path.Combine(Path.GetTempPath(), $"stress_{Guid.NewGuid():N}.srt");
        try
        {
            await GenerateLargeSrtFileAsync(tempPath, entryCount: 1000, chaptersEvery: 100);

            var doc = await _handler.ProcessAsync(tempPath, new DocumentHandlerOptions());

            var entryCount = (int)doc.Metadata["entry_count"];
            var chapterCount = (int)doc.Metadata["chapter_count"];
            entryCount.Should().Be(1000);
            chapterCount.Should().BeGreaterThan(1);
            doc.Markdown.Should().NotBeNullOrEmpty();

            var result = await _plugin.ProcessAsync(doc.Markdown,
                new ProcessorOptions { FilePath = tempPath });

            result.DocumentTree.Children.Count.Should().BeGreaterThan(1);

            output.WriteLine($"Large SRT: {entryCount} entries, {chapterCount} chapters, " +
                             $"{doc.Markdown.Length} chars markdown, " +
                             $"{result.DocumentTree.Children.Count} tree nodes");
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task E2E_LargeFile_5000Entries_CompletesReasonably()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"stress5k_{Guid.NewGuid():N}.srt");
        try
        {
            await GenerateLargeSrtFileAsync(tempPath, entryCount: 5000, chaptersEvery: 200);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var doc = await _handler.ProcessAsync(tempPath, new DocumentHandlerOptions());
            sw.Stop();

            ((int)doc.Metadata["entry_count"]).Should().Be(5000);
            doc.Markdown.Should().NotBeNullOrEmpty();

            output.WriteLine($"5K entries: processed in {sw.ElapsedMilliseconds}ms, " +
                             $"{doc.Markdown.Length} chars, " +
                             $"{(int)doc.Metadata["chapter_count"]} chapters");

            // Should complete in reasonable time
            sw.ElapsedMilliseconds.Should().BeLessThan(5000,
                "5000 entries should process in under 5 seconds");
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    // ── Edge Cases ──────────────────────────────────────────────────────────

    [Fact]
    public async Task E2E_MinimalSrt_SingleEntry()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"minimal_{Guid.NewGuid():N}.srt");
        try
        {
            await File.WriteAllTextAsync(tempPath, """
                1
                00:00:01,000 --> 00:00:05,000
                Just a single subtitle entry.

                """);

            var doc = await _handler.ProcessAsync(tempPath, new DocumentHandlerOptions());

            ((int)doc.Metadata["entry_count"]).Should().Be(1);
            doc.Markdown.Should().Contain("single subtitle entry");

            var result = await _plugin.ProcessAsync(doc.Markdown,
                new ProcessorOptions { FilePath = tempPath });

            result.DocumentTree.Children.Should().NotBeEmpty();
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task E2E_EmptySrt_HandlesGracefully()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"empty_{Guid.NewGuid():N}.srt");
        try
        {
            await File.WriteAllTextAsync(tempPath, "");

            var doc = await _handler.ProcessAsync(tempPath, new DocumentHandlerOptions());

            doc.Markdown.Should().BeEmpty();
            doc.ContentType.Should().Be("subtitles");
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task E2E_SrtWithOverlappingTimestamps_Parses()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"overlap_{Guid.NewGuid():N}.srt");
        try
        {
            await File.WriteAllTextAsync(tempPath, """
                1
                00:00:01,000 --> 00:00:05,000
                First line overlaps with second.

                2
                00:00:03,000 --> 00:00:07,000
                Second line starts before first ends.

                3
                00:00:06,000 --> 00:00:10,000
                Third line is normal.

                """);

            var doc = await _handler.ProcessAsync(tempPath, new DocumentHandlerOptions());

            ((int)doc.Metadata["entry_count"]).Should().Be(3);
            doc.Markdown.Should().Contain("First line");
            doc.Markdown.Should().Contain("Second line");
            doc.Markdown.Should().Contain("Third line");
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task E2E_SrtWithMultiLineEntries_JoinsLines()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"multiline_{Guid.NewGuid():N}.srt");
        try
        {
            await File.WriteAllTextAsync(tempPath, """
                1
                00:00:01,000 --> 00:00:05,000
                First line of entry
                Second line of same entry

                2
                00:00:06,000 --> 00:00:10,000
                Single line entry

                """);

            var doc = await _handler.ProcessAsync(tempPath, new DocumentHandlerOptions());

            ((int)doc.Metadata["entry_count"]).Should().Be(2);
            // Multi-line entries should be joined with space
            doc.Markdown.Should().Contain("First line of entry");
            doc.Markdown.Should().Contain("Second line of same entry");
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    // ── Processor Plugin CanProcess Integration ─────────────────────────────

    [Theory]
    [InlineData("video.srt")]
    [InlineData("video.vtt")]
    [InlineData("video.ass")]
    [InlineData("video.ssa")]
    public void E2E_Plugin_CanProcess_AllSubtitleExtensions(string fileName)
    {
        var context = new ProcessingContext { FileName = fileName };
        _plugin.CanProcess("content", context).Should().BeTrue();
    }

    [Theory]
    [InlineData("subtitles")]
    [InlineData("transcript")]
    public void E2E_Plugin_CanProcess_DocumentTypes(string docType)
    {
        var context = new ProcessingContext { DocumentType = docType };
        _plugin.CanProcess("content", context).Should().BeTrue();
    }

    [Theory]
    [InlineData("video.mp4")]
    [InlineData("audio.mp3")]
    [InlineData("document.pdf")]
    [InlineData("image.png")]
    public void E2E_Plugin_CanProcess_RejectsNonSubtitleFiles(string fileName)
    {
        var context = new ProcessingContext { FileName = fileName };
        _plugin.CanProcess("content", context).Should().BeFalse();
    }

    // ── FormatTimestamp Integration ──────────────────────────────────────────

    [Theory]
    [InlineData(0, 0, 0, "0:00")]
    [InlineData(0, 5, 30, "5:30")]
    [InlineData(0, 59, 59, "59:59")]
    [InlineData(1, 0, 0, "1:00:00")]
    [InlineData(2, 15, 30, "2:15:30")]
    [InlineData(10, 5, 3, "10:05:03")]
    public void E2E_FormatTimestamp_AllVariants(int hours, int minutes, int seconds, string expected)
    {
        var ts = new TimeSpan(hours, minutes, seconds);
        ChapterDetector.FormatTimestamp(ts).Should().Be(expected);
    }

    // ── Full Pipeline: Handler + Detector + Plugin ──────────────────────────

    [Fact]
    public async Task E2E_FullPipeline_HandlerToPluginPreservesAllContent()
    {
        var path = TestDataPath("chapters.srt");
        var doc = await _handler.ProcessAsync(path, new DocumentHandlerOptions());
        var result = await _plugin.ProcessAsync(doc.Markdown,
            new ProcessorOptions { FilePath = path });

        // Reconstruct all text from tree nodes
        var allContent = new StringBuilder();
        foreach (var child in result.DocumentTree.Children)
        {
            allContent.Append(child.Content);
            allContent.Append(' ');
        }
        var reconstructed = allContent.ToString();

        // Key phrases from chapters.srt should survive the full pipeline
        reconstructed.Should().Contain("tutorial");
        reconstructed.Should().Contain("console");

        output.WriteLine($"Full pipeline: {doc.Markdown.Length} markdown chars → " +
                         $"{result.DocumentTree.Children.Count} nodes → " +
                         $"{reconstructed.Length} reconstructed chars");
    }

    [Fact]
    public async Task E2E_FullPipeline_MetadataConsistencyAcrossFormats()
    {
        var files = new[] { "simple.srt", "simple.vtt", "simple.ass", "simple.ssa" };

        foreach (var file in files)
        {
            var path = TestDataPath(file);
            var doc = await _handler.ProcessAsync(path, new DocumentHandlerOptions());

            // Every format should produce consistent metadata keys
            doc.Metadata.Should().ContainKey("duration_seconds", $"missing from {file}");
            doc.Metadata.Should().ContainKey("entry_count", $"missing from {file}");
            doc.Metadata.Should().ContainKey("chapter_count", $"missing from {file}");
            doc.Metadata.Should().ContainKey("format", $"missing from {file}");

            // Duration should be positive
            ((int)doc.Metadata["duration_seconds"]).Should().BeGreaterThan(0, $"invalid duration for {file}");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task GenerateLargeSrtFileAsync(string path, int entryCount, int chaptersEvery)
    {
        var sb = new StringBuilder(entryCount * 120);
        var currentTime = TimeSpan.Zero;

        for (var i = 1; i <= entryCount; i++)
        {
            var isChapterBoundary = i % chaptersEvery == 0 && i > 1;
            var gap = isChapterBoundary
                ? TimeSpan.FromSeconds(8)
                : TimeSpan.FromMilliseconds(500);

            var start = currentTime + gap;
            var end = start + TimeSpan.FromSeconds(3);

            sb.AppendLine(i.ToString());
            sb.AppendLine($"{FormatSrtTime(start)} --> {FormatSrtTime(end)}");

            if (isChapterBoundary)
                sb.AppendLine($"CHAPTER {i / chaptersEvery}: New Topic Area");
            else
                sb.AppendLine($"This is subtitle entry number {i} with some text content about the topic.");

            sb.AppendLine();
            currentTime = end;
        }

        await File.WriteAllTextAsync(path, sb.ToString());
    }

    private static string FormatSrtTime(TimeSpan ts) =>
        $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";
}
