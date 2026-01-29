using System.ComponentModel;
using DoomSummarizer.Helpers;
using DoomSummarizer.Models;
using DoomSummarizer.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DoomSummarizer.Commands;

public sealed class ConfigCommand : AsyncCommand<ConfigCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--init")]
        [Description("Create default config file")]
        public bool Init { get; init; }

        [CommandOption("--show")]
        [Description("Show current configuration")]
        public bool Show { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Init)
        {
            await ConfigService.InitializeDefaultConfigAsync();
            return 0;
        }

        var config = await ConfigService.LoadAsync();

        if (settings.Show || !settings.Init)
        {
            ShowConfig(config);
        }

        return 0;
    }

    private static void ShowConfig(DoomConfig config)
    {
        AnsiConsole.Write(new Rule("[bold cyan]DoomSummarizer Configuration[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        // Sources
        var sourcesTree = new Tree("[bold]Sources[/]");

        var hnNode = sourcesTree.AddNode(config.Sources.HackerNews.Enabled
            ? "[green]>[/] Hacker News"
            : "[grey]x[/] Hacker News");
        hnNode.AddNode($"Sections: {string.Join(", ", config.Sources.HackerNews.Sections)}");
        hnNode.AddNode($"Max stories: {config.Sources.HackerNews.MaxStories}");
        hnNode.AddNode($"Min score: {config.Sources.HackerNews.MinScore}");

        var redditNode = sourcesTree.AddNode(config.Sources.Reddit.Enabled
            ? "[green]>[/] Reddit"
            : "[grey]x[/] Reddit");
        redditNode.AddNode($"Subreddits: {string.Join(", ", config.Sources.Reddit.Subreddits)}");
        redditNode.AddNode($"Sort: {config.Sources.Reddit.Sort}");
        redditNode.AddNode($"Max posts: {config.Sources.Reddit.MaxPosts}");
        redditNode.AddNode($"Min score: {config.Sources.Reddit.MinScore}");

        if (config.Sources.Websites.Count > 0)
        {
            var webNode = sourcesTree.AddNode("[green]>[/] Custom Websites");
            foreach (var site in config.Sources.Websites)
            {
                webNode.AddNode($"{site.Url} {(site.UsePlaywright ? "[grey](Playwright)[/]" : "")}");
            }
        }

        AnsiConsole.Write(sourcesTree);
        AnsiConsole.WriteLine();

        // Ollama
        var ollamaTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Ollama Configuration[/]")
            .AddColumn("Setting")
            .AddColumn("Value");

        ollamaTable.AddRow("Base URL", config.Ollama.BaseUrl);
        ollamaTable.AddRow("Model", config.Ollama.Model);
        ollamaTable.AddRow("Embed Model", config.Ollama.EmbedModel);
        ollamaTable.AddRow("Temperature", config.Ollama.Temperature.ToString("F1"));
        ollamaTable.AddRow("Timeout", $"{config.Ollama.TimeoutSeconds}s");

        AnsiConsole.Write(ollamaTable);
        AnsiConsole.WriteLine();

        // Embedding
        var embedTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Embedding Configuration[/]")
            .AddColumn("Setting")
            .AddColumn("Value");

        embedTable.AddRow("Backend", config.Embedding.Backend);
        embedTable.AddRow("Model", config.Embedding.Model);
        embedTable.AddRow("Similarity Threshold", config.Embedding.SimilarityThreshold.ToString("F2"));

        AnsiConsole.Write(embedTable);
        AnsiConsole.WriteLine();

        // Vibes
        AnsiConsole.Write(new Rule("[bold]Available Vibes[/]").RuleStyle("grey").LeftJustified());

        foreach (var (name, prompt) in config.Vibes)
        {
            var color = name switch
            {
                "doom" => "red",
                "hopeful" => "green",
                "snarky" => "yellow",
                _ => "grey"
            };
            AnsiConsole.MarkupLine($"  [{color}]{name}[/]: [grey]{FormattingHelpers.Truncate(prompt, 60)}[/]");
        }

        AnsiConsole.WriteLine();

        // Storage
        var storagePath = ConfigService.GetDbPath(config);
        AnsiConsole.MarkupLine($"[bold]Storage:[/] {storagePath}");
        AnsiConsole.MarkupLine($"[bold]Retention:[/] {config.Storage.RetentionDays} days");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Edit config: ~/.doomsummarizer/config.json[/]");
    }

}
