using System.Diagnostics;
using DoomSummarizer.Plugins.Subtitles;
using DoomSummarizer.Sources.YouTube;
using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Tests.Benchmarks;

/// <summary>
/// Benchmarks for subtitle processing and YouTube chapter detection.
/// Validates allocation efficiency and throughput for hot paths.
/// </summary>
public class SubtitleBenchmarks(Xunit.Abstractions.ITestOutputHelper output)
{
    // ── B1: ChapterDetector throughput ─────────────────────────────────────

    [Fact]
    public void B1_ChapterDetector_10000Entries()
    {
        const int entryCount = 10_000;
        var entries = GenerateSubtitleEntries(entryCount, chaptersEvery: 100);

        // Warm up
        ChapterDetector.DetectChapters(entries);

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();

        var chapters = ChapterDetector.DetectChapters(entries);

        sw.Stop();
        var allocAfter = GC.GetAllocatedBytesForCurrentThread();

        output.WriteLine($"ChapterDetector ({entryCount} entries): {sw.ElapsedMilliseconds}ms, " +
                         $"{chapters.Count} chapters, {allocAfter - allocBefore:N0} bytes");

        chapters.Count.Should().BeGreaterThan(0);
        sw.ElapsedMilliseconds.Should().BeLessThan(100, "10K entries should process in under 100ms");
    }

    [Fact]
    public void B1_ChapterDetector_Throughput()
    {
        const int iterations = 1000;
        var entries = GenerateSubtitleEntries(500, chaptersEvery: 50);

        // Warm up
        ChapterDetector.DetectChapters(entries);

        var sw = Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
            ChapterDetector.DetectChapters(entries);

        sw.Stop();

        var opsPerSec = iterations / sw.Elapsed.TotalSeconds;
        output.WriteLine($"ChapterDetector throughput: {opsPerSec:N0} ops/sec " +
                         $"(500 entries × {iterations} iterations in {sw.ElapsedMilliseconds}ms)");

        opsPerSec.Should().BeGreaterThan(500, "chapter detection should exceed 500 ops/sec");
    }

    // ── B2: IsLikelyTitle allocation ──────────────────────────────────────

    [Fact]
    public void B2_IsLikelyTitle_NoLINQAllocation()
    {
        var entries = new SubtitleEntry[]
        {
            new(TimeSpan.Zero, TimeSpan.FromSeconds(4), "Normal text here"),
            new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(9), "INTRODUCTION"),
            new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(14), "More normal text"),
        };

        // Warm up
        ChapterDetector.DetectChapters(entries, gapThresholdSeconds: 100);

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 1000; i++)
            ChapterDetector.DetectChapters(entries, gapThresholdSeconds: 100);

        var allocAfter = GC.GetAllocatedBytesForCurrentThread();
        var allocBytes = allocAfter - allocBefore;

        output.WriteLine($"ChapterDetector (3 entries × 1000): {allocBytes:N0} bytes " +
                         $"({allocBytes / 1000.0:F0} bytes/op)");

        // Should allocate minimal per-op (mainly List + SubtitleChapter records)
        var bytesPerOp = allocBytes / 1000.0;
        bytesPerOp.Should().BeLessThan(2048, "should avoid LINQ ToArray/ToList in hot path");
    }

    // ── B3: DescriptionChapterParser throughput ───────────────────────────

    [Fact]
    public void B3_DescriptionChapterParser_Throughput()
    {
        const int iterations = 10_000;
        var description = GenerateDescription(20);

        // Warm up
        DescriptionChapterParser.Parse(description);

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
            DescriptionChapterParser.Parse(description);

        sw.Stop();
        var allocAfter = GC.GetAllocatedBytesForCurrentThread();

        var opsPerSec = iterations / sw.Elapsed.TotalSeconds;
        output.WriteLine($"DescriptionChapterParser: {opsPerSec:N0} ops/sec " +
                         $"({allocAfter - allocBefore:N0} bytes for {iterations} ops, " +
                         $"{(allocAfter - allocBefore) / iterations:N0} bytes/op)");

        opsPerSec.Should().BeGreaterThan(10_000, "regex-based parser should be fast");
    }

    // ── B4: SubtitleDocumentHandler end-to-end ────────────────────────────

    [Fact]
    public async Task B4_SubtitleDocumentHandler_SrtParsing()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "Subtitles", "chapters.srt");
        if (!File.Exists(path)) return;

        var handler = new SubtitleDocumentHandler();
        var options = new DocumentHandlerOptions();

        // Warm up
        await handler.ProcessAsync(path, options);

        var sw = Stopwatch.StartNew();

        for (var i = 0; i < 100; i++)
            await handler.ProcessAsync(path, options);

        sw.Stop();

        var opsPerSec = 100 / sw.Elapsed.TotalSeconds;
        output.WriteLine($"SubtitleDocumentHandler (chapters.srt): {opsPerSec:N0} files/sec " +
                         $"(100 iterations in {sw.ElapsedMilliseconds}ms)");

        opsPerSec.Should().BeGreaterThan(100, "SRT parsing should exceed 100 files/sec");
    }

    // ── B5: FormatTimestamp allocation ─────────────────────────────────────

    [Fact]
    public void B5_FormatTimestamp_Throughput()
    {
        const int iterations = 100_000;
        var ts = new TimeSpan(1, 23, 45);

        // Warm up
        ChapterDetector.FormatTimestamp(ts);

        var sw = Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
            ChapterDetector.FormatTimestamp(ts);

        sw.Stop();

        var opsPerSec = iterations / sw.Elapsed.TotalSeconds;
        output.WriteLine($"FormatTimestamp: {opsPerSec:N0} ops/sec");

        opsPerSec.Should().BeGreaterThan(1_000_000, "simple string format should exceed 1M ops/sec");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static List<SubtitleEntry> GenerateSubtitleEntries(int count, int chaptersEvery = 100)
    {
        var entries = new List<SubtitleEntry>(count);
        var currentTime = TimeSpan.Zero;

        for (var i = 0; i < count; i++)
        {
            var duration = TimeSpan.FromSeconds(3);
            var gap = i % chaptersEvery == 0 && i > 0
                ? TimeSpan.FromSeconds(8) // Chapter gap
                : TimeSpan.FromSeconds(0.5);

            var start = currentTime + gap;
            var end = start + duration;
            var text = i % chaptersEvery == 0 && i > 0
                ? $"CHAPTER {i / chaptersEvery}"
                : $"This is subtitle entry number {i} with some text content.";

            entries.Add(new SubtitleEntry(start, end, text));
            currentTime = end;
        }

        return entries;
    }

    private static string GenerateDescription(int chapterCount)
    {
        var lines = new List<string> { "Welcome to this video!\n\nChapters:" };
        for (var i = 0; i < chapterCount; i++)
        {
            var minutes = i * 5;
            var seconds = (i * 17) % 60;
            lines.Add($"{minutes}:{seconds:D2} Chapter {i + 1}: Topic about item {i}");
        }
        lines.Add("\nPlease like and subscribe!");
        return string.Join("\n", lines);
    }
}
