using System.ComponentModel;
using DoomSummarizer.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DoomSummarizer.Commands;

/// <summary>
///     List knowledge base collections and their contents.
///     Without arguments, lists all collections with stats.
///     With a name argument, lists documents in that collection.
/// </summary>
public sealed class ShowCommand : AsyncCommand<ShowCommand.Settings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var config = await ConfigService.LoadAsync();
        var dbPath = ConfigService.GetDbPath(config);

        await using var storage = new StorageService(dbPath);
        await storage.InitializeAsync();

        if (string.IsNullOrWhiteSpace(settings.Name))
        {
            // List all collections
            var collections = await storage.GetCollectionsAsync();

            if (collections.Count == 0)
            {
                AnsiConsole.MarkupLine(
                    "[yellow]No collections found. Use 'crawl' or 'scroll' to populate the knowledge base.[/]");
                return 0;
            }

            var table = new Table()
                .Title("[bold cyan]Knowledge Base Collections[/]")
                .Border(TableBorder.Rounded)
                .Width(Math.Min(AnsiConsole.Profile.Width, 100))
                .AddColumn("[cyan]Collection[/]")
                .AddColumn(new TableColumn("[cyan]Items[/]").RightAligned())
                .AddColumn(new TableColumn("[cyan]Embedded[/]").RightAligned())
                .AddColumn("[cyan]Latest[/]")
                .AddColumn(new TableColumn("[cyan]Avg Len[/]").RightAligned());

            foreach (var c in collections)
            {
                var age = DateTimeOffset.UtcNow - c.Latest;
                var ageStr = age.TotalHours < 1 ? $"{age.Minutes}m ago"
                    : age.TotalDays < 1 ? $"{age.Hours}h ago"
                    : age.TotalDays < 30 ? $"{age.Days}d ago"
                    : c.Latest.ToString("yyyy-MM-dd");

                table.AddRow(
                    $"[bold]{Markup.Escape(c.Source)}[/]",
                    c.ItemCount.ToString(),
                    $"{c.WithEmbeddings}",
                    $"[grey]{ageStr}[/]",
                    $"{c.AvgContentLength:N0}");
            }

            AnsiConsole.Write(table);

            var totalItems = collections.Sum(c => c.ItemCount);
            var totalEmbedded = collections.Sum(c => c.WithEmbeddings);
            AnsiConsole.MarkupLine(
                $"\n[grey]Total: {totalItems} items, {totalEmbedded} with embeddings, {collections.Count} collections[/]");
            AnsiConsole.MarkupLine("[grey]Inspect a collection: doomsummarizer show <name>[/]");
            AnsiConsole.MarkupLine(
                "[grey]Query a collection: doomsummarizer scroll \"your question\" --name <name>[/]");
        }
        else
        {
            // Show items in a specific collection
            // Support both "docs" and "crawl:docs" formats
            var source = settings.Name.Contains(':') ? settings.Name : $"crawl:{settings.Name}";
            var items = await storage.GetItemsBySourceAsync(source, settings.Limit);

            if (items.Count == 0)
                // Try exact match in case the source isn't a crawl: prefix
                items = await storage.GetItemsBySourceAsync(settings.Name, settings.Limit);

            if (items.Count == 0)
            {
                AnsiConsole.MarkupLine($"[yellow]No items found for collection '{Markup.Escape(settings.Name)}'.[/]");
                AnsiConsole.MarkupLine("[grey]Use 'show' without arguments to list available collections.[/]");
                return 1;
            }

            var table = new Table()
                .Title($"[bold cyan]Collection: {Markup.Escape(source)}[/] ({items.Count} items)")
                .Border(TableBorder.Rounded)
                .Width(Math.Min(AnsiConsole.Profile.Width, 100))
                .AddColumn(new TableColumn("[cyan]#[/]").Width(3))
                .AddColumn(new TableColumn("[cyan]Title[/]").Width(40))
                .AddColumn("[cyan]Topic[/]")
                .AddColumn(new TableColumn("[cyan]Sent[/]").Width(5))
                .AddColumn(new TableColumn("[cyan]Emb[/]").Width(3))
                .AddColumn("[cyan]Fetched[/]");

            var idx = 0;
            foreach (var item in items)
            {
                idx++;
                var title = item.Title.Length > 38 ? item.Title[..35] + "..." : item.Title;
                var topic = item.DetectedTopic ?? "-";
                var sentiment = item.SentimentScore.ToString("F1");
                var hasEmbed = item.Embedding != null ? "[green]Y[/]" : "[red]N[/]";
                var age = DateTimeOffset.UtcNow - item.FetchedAt;
                var ageStr = age.TotalHours < 1 ? $"{age.Minutes}m"
                    : age.TotalDays < 1 ? $"{age.Hours}h"
                    : $"{age.Days}d";

                table.AddRow(
                    $"[grey]{idx}[/]",
                    Markup.Escape(title),
                    $"[grey]{Markup.Escape(topic)}[/]",
                    sentiment,
                    hasEmbed,
                    $"[grey]{ageStr}[/]");

                if (settings.Full && !string.IsNullOrEmpty(item.Content))
                {
                    var preview = item.Content.Length > 200
                        ? item.Content[..197] + "..."
                        : item.Content;
                    table.AddRow("", $"[dim]{Markup.Escape(preview)}[/]", "", "", "", "");
                }
            }

            AnsiConsole.Write(table);

            var withEmbeddings = items.Count(i => i.Embedding != null);
            var topTopics = items
                .Where(i => !string.IsNullOrEmpty(i.DetectedTopic))
                .GroupBy(i => i.DetectedTopic!)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => $"{g.Key} ({g.Count()})");

            AnsiConsole.MarkupLine(
                $"\n[grey]Embeddings: {withEmbeddings}/{items.Count} | Topics: {string.Join(", ", topTopics)}[/]");
            AnsiConsole.MarkupLine(
                $"[grey]Query: doomsummarizer scroll \"your question\" --name {Markup.Escape(settings.Name)}[/]");
        }

        return 0;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[name]")]
        [Description("Collection name to inspect (omit to list all collections)")]
        public string? Name { get; init; }

        [CommandOption("-l|--limit")]
        [Description("Maximum items to show")]
        [DefaultValue(50)]
        public int Limit { get; init; } = 50;

        [CommandOption("--full")]
        [Description("Show full content preview for each item")]
        public bool Full { get; init; }
    }
}