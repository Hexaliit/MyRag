using DoomSummarizer.Plugins;
using DoomSummarizer.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DoomSummarizer.Commands;

public sealed class SourcesCommand : AsyncCommand<SourcesCommand.Settings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings,
        CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[bold cyan]Available Sources[/]");
        AnsiConsole.WriteLine();

        // Build plugin registry to list all registered sources
        var registry = new SourcePluginRegistry();
        BuiltinPlugins.RegisterAllSources(registry);

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[cyan]Source[/]")
            .AddColumn("[cyan]Description[/]")
            .AddColumn("[cyan]Keys[/]")
            .AddColumn("[cyan]Examples[/]");

        foreach (var plugin in registry.All)
        {
            var meta = plugin.Metadata;
            var auth = meta.Capabilities.HasFlag(SourceCapabilities.RequiresAuth) ? " [grey](API key)[/]" : "";
            var keys = string.Join(", ", meta.Keys);
            var examples = meta.Examples.Count > 0
                ? string.Join(", ", meta.Examples.Take(3))
                : $"-s {meta.PrimaryKey}";

            table.AddRow(
                $"[green]{Markup.Escape(meta.DisplayName)}[/]{auth}",
                Markup.Escape(meta.Description),
                $"[grey]{Markup.Escape(keys)}[/]",
                Markup.Escape(examples));
        }

        // Static entries not yet covered by plugins
        table.AddEmptyRow();
        table.AddRow("[grey]http://url[/]", "Any RSS/website URL", "[grey]web[/]", "-s https://example.com/feed");
        table.AddRow("[grey]crawl:name[/]", "Local crawled knowledge base", "[grey]crawl[/]", "-s crawl:docs");

        AnsiConsole.Write(table);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Vibes:[/] doom, hopeful, snarky, funny, upbeat, friendly, neutral");
        AnsiConsole.MarkupLine(
            "[bold]Flags:[/] --images, --entities, --graph, --json, --raw, --no-llm, --local, --debug");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Examples:[/]");
        AnsiConsole.MarkupLine("  doomsummarizer scroll -s hn -s bbc --vibe doom");
        AnsiConsole.MarkupLine("  doomsummarizer scroll \"AI agent security vulnerabilities\" --vibe snarky");
        AnsiConsole.MarkupLine("  doomsummarizer scroll -s brave -s serper -s newsapi --vibe neutral");
        AnsiConsole.MarkupLine("  doomsummarizer scroll -s so:csharp -s reddit:dotnet --entities --graph");
        AnsiConsole.MarkupLine("  doomsummarizer scroll -s \"gnews:climate change\" -s bbc:science");
        AnsiConsole.MarkupLine("  doomsummarizer ask \"What happened with the SSH vulnerability?\"");
        AnsiConsole.MarkupLine("  doomsummarizer crawl https://docs.example.com --name docs");

        // Show API key status
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold cyan]API Status[/]").LeftJustified());
        var config = await ConfigService.LoadAsync();
        var apiKeys = ApiKeyService.Load(config.Keys);
        apiKeys.PrintStatus();

        return 0;
    }

    public sealed class Settings : CommandSettings;
}