using System.ComponentModel;
using DoomSummarizer.Helpers;
using DoomSummarizer.Models;
using DoomSummarizer.Services;
using Mostlylucid.DoomSummarizer.Plugin.Books.Detection;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Mostlylucid.DoomSummarizer.Plugin.Books.Commands;

/// <summary>
/// Detect the book type of a file (Fiction, NonFiction, Academic, Play, etc.).
/// When heuristic confidence is low and a sentinel LLM is available, automatically
/// falls back to LLM classification for better accuracy.
/// </summary>
public sealed class BooksDetectCommand : AsyncCommand<BooksDetectCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Path to the book file")]
        [CommandArgument(0, "<file>")]
        public string FilePath { get; set; } = "";

        [Description("Skip LLM fallback (heuristic only)")]
        [CommandOption("--no-llm")]
        public bool NoLlm { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (!File.Exists(settings.FilePath))
        {
            AnsiConsole.MarkupLine($"[red]File not found:[/] {Markup.Escape(settings.FilePath)}");
            return 1;
        }

        var content = await File.ReadAllTextAsync(settings.FilePath, ct);
        var wordCount = WordCounter.Count(content);

        var detection = BookTypeDetector.Detect(
            content,
            Path.GetFileName(settings.FilePath),
            new FileInfo(settings.FilePath).Length,
            wordCount);

        // Header
        AnsiConsole.MarkupLine($"[bold cyan]Book Type Detection[/]");
        AnsiConsole.MarkupLine($"[cyan]File:[/] {Markup.Escape(Path.GetFileName(settings.FilePath))}");
        AnsiConsole.MarkupLine($"[cyan]Words:[/] {wordCount:N0}");
        AnsiConsole.WriteLine();

        // Primary heuristic result
        var typeColor = GetTypeColor(detection.Type);
        AnsiConsole.MarkupLine($"[bold {typeColor}]{detection.Type.ToUpperInvariant()}[/] ({detection.Confidence:P0} confidence) [dim]\\[heuristic][/]");

        // Sentinel fallback when confidence is low
        DocumentTypeResult? sentinelResult = null;
        if (!settings.NoLlm && detection.Confidence < 0.45)
        {
            sentinelResult = await TrySentinelClassifyAsync(
                content, detection, wordCount,
                Path.GetFileName(settings.FilePath), ct);

            if (sentinelResult is { Source: "sentinel" })
            {
                var sentinelColor = GetTypeColor(sentinelResult.Type);
                AnsiConsole.MarkupLine($"[bold {sentinelColor}]{sentinelResult.Type.ToUpperInvariant()}[/] ({sentinelResult.Confidence:P0} confidence) [dim]\\[sentinel][/]");
            }
        }
        AnsiConsole.WriteLine();

        // Score breakdown
        var scoreTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Type")
            .AddColumn("Score");

        foreach (var (type, score) in detection.TypeScores.OrderByDescending(kv => kv.Value))
        {
            var bar = new string('#', (int)(score * 20));
            var isWinner = type == detection.Type;
            scoreTable.AddRow(
                isWinner ? $"[bold]{type}[/]" : type,
                isWinner ? $"[bold]{score:F2}[/] {bar}" : $"{score:F2} [dim]{bar}[/]");
        }
        AnsiConsole.Write(scoreTable);

        // Signals
        if (detection.Signals.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Detection Signals:[/]");

            var signalTable = new Table()
                .Border(TableBorder.Simple)
                .AddColumn("Signal")
                .AddColumn("Category")
                .AddColumn("Voted")
                .AddColumn("Weight")
                .AddColumn("Reason");

            foreach (var signal in detection.Signals)
            {
                signalTable.AddRow(
                    signal.Name,
                    $"[dim]{signal.Category}[/]",
                    signal.VotedType,
                    $"{signal.Weight:F2}",
                    Markup.Escape(signal.Reason));
            }
            AnsiConsole.Write(signalTable);
        }

        return 0;
    }

    private static async Task<DocumentTypeResult?> TrySentinelClassifyAsync(
        string content,
        BookTypeResult heuristic,
        int wordCount,
        string fileName,
        CancellationToken ct)
    {
        try
        {
            var config = await ConfigService.LoadAsync();
            var ollama = new OllamaService(config.Ollama);

            var detector = new DocumentTypeDetector(ollama);
            var signals = heuristic.Signals
                .Select(s => $"{s.Name}→{s.VotedType}")
                .ToList();

            return await detector.ClassifyAsync(
                content,
                heuristic.Type,
                heuristic.Confidence,
                signals,
                fileName,
                wordCount,
                ct);
        }
        catch
        {
            // Config missing or Ollama unreachable — no sentinel available
            return null;
        }
    }

    private static string GetTypeColor(string type) => type switch
    {
        BookTypeDetector.Fiction or "fiction" => "green",
        BookTypeDetector.NonFiction or "nonfiction" => "blue",
        BookTypeDetector.Academic or "academic" => "yellow",
        BookTypeDetector.Technical or "technical" => "magenta",
        BookTypeDetector.Play or "play" => "cyan",
        BookTypeDetector.Anthology or "anthology" => "orange1",
        BookTypeDetector.Collection or "collection" => "orange1",
        _ => "grey"
    };
}
