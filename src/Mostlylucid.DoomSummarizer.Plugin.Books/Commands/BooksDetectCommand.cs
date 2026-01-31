using System.ComponentModel;
using DoomSummarizer.Helpers;
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
        var fileName = Path.GetFileName(settings.FilePath);

        var detection = BookTypeDetector.Detect(
            content, fileName,
            new FileInfo(settings.FilePath).Length,
            wordCount);

        // Header
        AnsiConsole.MarkupLine("[bold cyan]Book Type Detection[/]");
        AnsiConsole.MarkupLine($"[cyan]File:[/] {Markup.Escape(fileName)}");
        AnsiConsole.MarkupLine($"[cyan]Words:[/] {wordCount:N0}");
        AnsiConsole.WriteLine();

        // Heuristic result
        var typeColor = GetTypeColor(detection.Type);
        AnsiConsole.MarkupLine($"[bold {typeColor}]{detection.Type.ToUpperInvariant()}[/] ({detection.Confidence:P0} confidence) [dim]\\[heuristic][/]");

        // Classify with sentinel fallback (detector owns the threshold decision)
        var result = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("[dim]Checking sentinel...[/]", async _ =>
                await SentinelHelper.ClassifyAsync(
                    content, detection, wordCount, fileName, settings.NoLlm, ct));

        if (result.Source == "sentinel")
        {
            var sentinelColor = GetTypeColor(result.Type);
            AnsiConsole.MarkupLine($"[bold {sentinelColor}]{result.Type.ToUpperInvariant()}[/] ({result.Confidence:P0} confidence) [dim]\\[sentinel][/]");
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

    private static string GetTypeColor(string type) => type switch
    {
        "fiction" => "green",
        "nonfiction" => "blue",
        "academic" => "yellow",
        "technical" => "magenta",
        "play" => "cyan",
        "anthology" or "collection" => "orange1",
        _ => "grey"
    };
}
