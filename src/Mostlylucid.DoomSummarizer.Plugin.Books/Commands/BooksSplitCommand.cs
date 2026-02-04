using System.ComponentModel;
using DoomSummarizer.Helpers;
using DoomSummarizer.Models;
using DoomSummarizer.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.DoomSummarizer.Plugin.Books.Detection;
using Mostlylucid.DoomSummarizer.Plugin.Books.Splitters;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Mostlylucid.DoomSummarizer.Plugin.Books.Commands;

/// <summary>
///     Show the chapter/section structure of a book file as a tree.
/// </summary>
public sealed class BooksSplitCommand : AsyncCommand<BooksSplitCommand.Settings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (!File.Exists(settings.FilePath))
        {
            AnsiConsole.MarkupLine($"[red]File not found:[/] {Markup.Escape(settings.FilePath)}");
            return 1;
        }

        var markdown = await File.ReadAllTextAsync(settings.FilePath, ct);
        var wordCount = WordCounter.Count(markdown);
        var fileName = Path.GetFileName(settings.FilePath);

        AnsiConsole.MarkupLine($"[cyan]File:[/] {Markup.Escape(fileName)}");
        AnsiConsole.MarkupLine($"[cyan]Words:[/] {wordCount:N0}");

        // Detect book type (heuristic)
        var detection = BookTypeDetector.Detect(
            markdown, fileName,
            new FileInfo(settings.FilePath).Length,
            wordCount);

        // Classify with sentinel fallback — detector owns the threshold decision.
        // Skip sentinel entirely if the user explicitly chose a pattern.
        var noLlm = settings.NoLlm || settings.Pattern != null;
        var result = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("[dim]Detecting type...[/]", async _ =>
                await SentinelHelper.ClassifyAsync(
                    markdown, detection, wordCount, fileName, noLlm, ct));

        var pattern = settings.Pattern ?? result.Type switch
        {
            "play" => "play",
            "anthology" or "collection" => "anthology",
            "academic" or "technical" => "academic",
            _ => "novel"
        };

        AnsiConsole.MarkupLine(
            $"[cyan]Type:[/] {result.Type} ({result.Confidence:P0} confidence) [dim]\\[{result.Source}][/]");
        AnsiConsole.MarkupLine($"[cyan]Pattern:[/] {pattern}");
        AnsiConsole.WriteLine();

        // Split
        var splitter = new HierarchicalBookSplitter(NullLogger<HierarchicalBookSplitter>.Instance);
        var splitOptions = new SplitOptions
        {
            MaxDepth = settings.MaxDepth,
            PatternName = pattern
        };

        var tree = await splitter.SplitAsync(markdown, splitOptions, ct);

        // Render as Spectre tree
        var root = new Tree(
            $"[bold]{Markup.Escape(tree.Title)}[/] [dim]({tree.TotalWordCount:N0} words, {tree.LeafCount} leaves)[/]");
        RenderNode(root, tree);

        AnsiConsole.Write(root);
        return 0;
    }

    private static void RenderNode(IHasTreeNodes parent, DocumentNode node)
    {
        foreach (var child in node.Children)
        {
            var label = child.IsLeaf
                ? $"{Markup.Escape(child.Title)} [dim]({child.WordCount:N0} words)[/]"
                : $"[bold]{Markup.Escape(child.Title)}[/] [dim]({child.TotalWordCount:N0} words, {child.LeafCount} leaves)[/]";

            var childNode = parent.AddNode(label);

            if (!child.IsLeaf)
                RenderNode(childNode, child);
        }
    }

    public sealed class Settings : CommandSettings
    {
        [Description("Path to the book file (.pdf, .docx, .txt, .md, .zip)")]
        [CommandArgument(0, "<file>")]
        public string FilePath { get; set; } = "";

        [Description("Splitting pattern: novel, play, anthology, academic (default: auto-detect)")]
        [CommandOption("-p|--pattern")]
        public string? Pattern { get; set; }

        [Description("Maximum tree depth")]
        [CommandOption("-d|--depth")]
        public int MaxDepth { get; set; } = 4;

        [Description("Skip LLM fallback (heuristic only)")]
        [CommandOption("--no-llm")]
        public bool NoLlm { get; set; }
    }
}