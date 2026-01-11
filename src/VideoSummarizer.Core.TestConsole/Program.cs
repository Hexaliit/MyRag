using AudioSummarizer.Core.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Extensions;
using VideoSummarizer.Core.Extensions;
using VideoSummarizer.Core.Models;
using VideoSummarizer.Core.Pipeline;
using VideoSummarizer.Core.Services;
using VideoSummarizer.Core.Waves;
using System.Text.Json;
using System.Text.Json.Serialization;

// Parse command line arguments
var fullMode = args.Contains("--full");
var videoPath = args.FirstOrDefault(a => !a.StartsWith("--"))
    ?? @"C:\Users\scott\OneDrive\Videos\Count.Arthur.Strong.S02E03.HDTV.x264-TASTETV.mp4";

if (!File.Exists(videoPath))
{
    Console.WriteLine($"Video file not found: {videoPath}");
    return 1;
}

Console.WriteLine($"Testing VideoSummarizer with: {videoPath}");
Console.WriteLine($"Mode: {(fullMode ? "Full (with ImageSummarizer/AudioSummarizer)" : "Basic (fast)")}");
Console.WriteLine(new string('=', 60));

// Build services
var services = new ServiceCollection();

// Add logging
services.AddLogging(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

// Add FFmpeg service
services.AddSingleton<FFmpegAnalysisService>();

if (fullMode)
{
    // Full mode: Register ImageSummarizer and AudioSummarizer for embeddings
    services.AddDocSummarizerImages();
    services.AddAudioSummarizer();

    // Register all video waves including KeyframeExtractionWave and TranscriptionWave
    services.AddVideoSummarizer();
}
else
{
    // Basic mode: Fast test without external dependencies
    services.AddTransient<IVideoWave, NormalizeWave>();
    services.AddTransient<IVideoWave, ShotDetectionWave>();
    services.AddTransient<IVideoWave, SubtitleExtractionWave>();
    services.AddTransient<IVideoWave, ChapterExtractionWave>();
    services.AddTransient<IVideoWave, SceneClusteringWave>();
    services.AddTransient<IVideoWave, EvidenceGenerationWave>();
    services.AddTransient<VideoWaveCoordinator>();
}

var provider = services.BuildServiceProvider();
var coordinator = provider.GetRequiredService<VideoWaveCoordinator>();
var logger = provider.GetRequiredService<ILogger<Program>>();

// Create working directory
var workingDir = Path.Combine(Path.GetTempPath(), "VideoSummarizer", Path.GetRandomFileName());
Directory.CreateDirectory(workingDir);

Console.WriteLine($"Working directory: {workingDir}");
Console.WriteLine();

try
{
    // Create video context
    var context = new VideoContext
    {
        VideoPath = videoPath,
        WorkingDirectory = workingDir,
        OnProgress = (stage, percent) =>
        {
            Console.WriteLine($"[{percent:F0}%] {stage}");
        }
    };

    // Run pipeline
    Console.WriteLine("Running video analysis pipeline...");
    Console.WriteLine();

    var sw = System.Diagnostics.Stopwatch.StartNew();

    await coordinator.ProcessAsync(context);

    sw.Stop();

    Console.WriteLine();
    Console.WriteLine(new string('=', 60));
    Console.WriteLine($"Analysis completed in {sw.Elapsed.TotalSeconds:F1} seconds");
    Console.WriteLine();

    // Print results
    Console.WriteLine("=== VIDEO METADATA ===");
    if (context.Metadata != null)
    {
        Console.WriteLine($"  Duration: {TimeSpan.FromSeconds(context.Metadata.Duration):hh\\:mm\\:ss}");
        Console.WriteLine($"  Resolution: {context.Metadata.Width}x{context.Metadata.Height}");
        Console.WriteLine($"  FPS: {context.Metadata.Fps:F2}");
        Console.WriteLine($"  Video codec: {context.Metadata.VideoCodec}");
        Console.WriteLine($"  Audio codec: {context.Metadata.AudioCodec}");
    }

    Console.WriteLine();
    Console.WriteLine("=== SHOTS ===");
    Console.WriteLine($"  Total: {context.Shots.Count}");
    if (context.Shots.Count > 0)
    {
        var avgDuration = context.Shots.Average(s => s.EndTime - s.StartTime);
        Console.WriteLine($"  Average duration: {avgDuration:F2}s");

        var cutTypes = context.Shots.GroupBy(s => s.CutType).OrderByDescending(g => g.Count());
        Console.WriteLine($"  Cut types: {string.Join(", ", cutTypes.Select(g => $"{g.Key}:{g.Count()}"))}");

        Console.WriteLine("\n  First 5 shots:");
        foreach (var shot in context.Shots.Take(5))
        {
            Console.WriteLine($"    [{FormatTime(shot.StartTime)} - {FormatTime(shot.EndTime)}] {shot.CutType}, motion={shot.MotionScore:F2}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("=== SCENES ===");
    Console.WriteLine($"  Total: {context.Scenes.Count}");
    if (context.Scenes.Count > 0)
    {
        Console.WriteLine("\n  Scenes:");
        foreach (var scene in context.Scenes.Take(10))
        {
            var label = !string.IsNullOrEmpty(scene.Label) ? $" ({scene.Label})" : "";
            Console.WriteLine($"    [{FormatTime(scene.StartTime)} - {FormatTime(scene.EndTime)}]{label}");
            Console.WriteLine($"      Shots: {scene.ShotIds.Count}, Confidence: {scene.Confidence:F2}");
            if (scene.KeyTerms.Count > 0)
            {
                Console.WriteLine($"      Terms: {string.Join(", ", scene.KeyTerms.Take(5))}");
            }
        }
    }

    Console.WriteLine();
    Console.WriteLine("=== UTTERANCES ===");
    Console.WriteLine($"  Total: {context.Utterances.Count}");
    if (context.Utterances.Count > 0)
    {
        var totalWords = context.Utterances.Sum(u => u.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        Console.WriteLine($"  Total words: {totalWords}");

        Console.WriteLine("\n  First 5 utterances:");
        foreach (var utterance in context.Utterances.Take(5))
        {
            var text = utterance.Text.Length > 60 ? utterance.Text[..60] + "..." : utterance.Text;
            Console.WriteLine($"    [{FormatTime(utterance.StartTime)}] {text}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("=== TEXT TRACKS ===");
    Console.WriteLine($"  Total: {context.TextTracks.Count}");
    if (context.TextTracks.Count > 0)
    {
        Console.WriteLine("\n  Text tracks:");
        foreach (var track in context.TextTracks.Take(10))
        {
            var text = track.Text.Length > 50 ? track.Text[..50] + "..." : track.Text;
            Console.WriteLine($"    [{FormatTime(track.StartTime)} - {FormatTime(track.EndTime)}] {track.TextType}: {text}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("=== KEYFRAMES ===");
    Console.WriteLine($"  Extracted: {context.Keyframes.Count}");
    Console.WriteLine($"  With embeddings: {context.KeyframeEmbeddings.Count}");
    Console.WriteLine($"  With perceptual hash: {context.PerceptualHashes.Count}");

    Console.WriteLine();
    Console.WriteLine("=== EVIDENCE ===");
    Console.WriteLine($"  Total: {context.Evidence.Count}");
    if (context.Evidence.Count > 0)
    {
        var byType = context.Evidence.GroupBy(e => e.Type).OrderByDescending(g => g.Count());
        foreach (var group in byType)
        {
            var withEmbed = group.Count(e => e.Embedding != null);
            Console.WriteLine($"    {group.Key}: {group.Count()} ({withEmbed} with embeddings)");
        }

        Console.WriteLine("\n  Sample evidence items:");
        foreach (var evidence in context.Evidence.Take(5))
        {
            var text = evidence.TextContent?.Length > 40
                ? evidence.TextContent[..40] + "..."
                : evidence.TextContent ?? "(no text)";
            Console.WriteLine($"    [{evidence.Type}] [{FormatTime(evidence.StartTime)}] {text}");
            Console.WriteLine($"      Embedding: {(evidence.Embedding != null ? $"{evidence.Embedding.Length}D" : "none")}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("=== SIGNALS ===");
    var allSignals = context.GetAllSignals().ToList();
    Console.WriteLine($"  Total signals: {allSignals.Count}");
    Console.WriteLine("\n  Key signals:");
    var keySignals = new[]
    {
        "video.duration", "shots.count", "scene.count",
        "subtitle.utterance_count", "chapter.count",
        "evidence.total_count", "evidence.with_embeddings"
    };
    foreach (var key in keySignals)
    {
        var signal = context.GetBestSignal(key);
        if (signal != null)
        {
            Console.WriteLine($"    {key}: {signal.Value}");
        }
    }

    // Save summary to JSON
    var summaryPath = Path.Combine(workingDir, "summary.json");
    var summary = new
    {
        VideoPath = videoPath,
        Duration = context.Metadata?.Duration,
        Resolution = $"{context.Metadata?.Width}x{context.Metadata?.Height}",
        ShotCount = context.Shots.Count,
        SceneCount = context.Scenes.Count,
        UtteranceCount = context.Utterances.Count,
        TextTrackCount = context.TextTracks.Count,
        EvidenceCount = context.Evidence.Count,
        EvidenceWithEmbeddings = context.Evidence.Count(e => e.Embedding != null),
        Shots = context.Shots.Select(s => new
        {
            s.StartTime,
            s.EndTime,
            s.CutType,
            s.MotionScore,
            s.Confidence
        }).ToList(),
        Scenes = context.Scenes.Select(s => new
        {
            s.StartTime,
            s.EndTime,
            s.Label,
            s.KeyTerms,
            ShotCount = s.ShotIds.Count,
            s.Confidence
        }).ToList(),
        Utterances = context.Utterances.Select(u => new
        {
            u.StartTime,
            u.EndTime,
            u.Text,
            u.Confidence,
            u.Language
        }).ToList()
    };

    var jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, jsonOptions));
    Console.WriteLine();
    Console.WriteLine($"Summary saved to: {summaryPath}");

    return 0;
}
catch (Exception ex)
{
    logger.LogError(ex, "Pipeline failed");
    Console.WriteLine($"\nError: {ex.Message}");
    return 1;
}
finally
{
    // Don't delete working directory so we can inspect results
    Console.WriteLine();
    Console.WriteLine($"Working directory preserved at: {workingDir}");
}

static string FormatTime(double seconds)
{
    var ts = TimeSpan.FromSeconds(seconds);
    return ts.TotalHours >= 1
        ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
        : $"{ts.Minutes}:{ts.Seconds:D2}";
}

public partial class Program { }
