using System.ComponentModel;
using DoomSummarizer.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DoomSummarizer.Commands;

public sealed class SourcesCommand : Command<SourcesCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[bold cyan]Available Sources[/]");
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[cyan]Source[/]")
            .AddColumn("[cyan]Description[/]")
            .AddColumn("[cyan]Examples[/]");

        // Tech aggregators
        table.AddRow("[green]hn[/]", "Hacker News", "-s hn");
        table.AddRow("[green]reddit[/]", "Reddit (default subs)", "-s reddit");
        table.AddRow("[green]reddit:sub[/]", "Specific subreddit", "-s reddit:dotnet");
        table.AddRow("[green]lobsters[/]", "Lobste.rs", "-s lobsters");
        table.AddRow("[green]slashdot[/]", "Slashdot", "-s slashdot");

        table.AddEmptyRow();

        // StackOverflow
        table.AddRow("[yellow]so[/]", "StackOverflow hot", "-s so");
        table.AddRow("[yellow]so:tag[/]", "By tag", "-s so:csharp, -s so:python");
        table.AddRow("[yellow]so:search:q[/]", "Search", "-s \"so:search:async await\"");

        table.AddEmptyRow();

        // Google News (universal search)
        table.AddRow("[red]gnews:query[/]", "Google News search", "-s \"gnews:pharmaceutical news\"");
        table.AddRow("[red]gnews_topic:T[/]", "Google News topic", "-s gnews_topic:HEALTH");

        table.AddEmptyRow();

        // News sources
        table.AddRow("[blue]bbc[/]", "BBC News", "-s bbc");
        table.AddRow("[blue]bbc:category[/]", "BBC category feed", "-s bbc:health, -s bbc:science");
        table.AddRow("[blue]guardian[/]", "The Guardian", "-s guardian");
        table.AddRow("[blue]cnn[/]", "CNN", "-s cnn");
        table.AddRow("[blue]reuters[/]", "Reuters", "-s reuters");
        table.AddRow("[blue]ars[/]", "Ars Technica", "-s ars");
        table.AddRow("[blue]verge[/]", "The Verge", "-s verge");
        table.AddRow("[blue]wired[/]", "Wired", "-s wired");
        table.AddRow("[blue]techcrunch[/]", "TechCrunch", "-s techcrunch");

        table.AddEmptyRow();

        // Tech blogs
        table.AddRow("[magenta]devto[/]", "Dev.to", "-s devto");
        table.AddRow("[magenta]hackernoon[/]", "HackerNoon", "-s hackernoon");

        table.AddEmptyRow();

        // Search and URLs
        table.AddRow("[grey]search:q[/]", "DuckDuckGo search", "-s \"search:rust programming\"");
        table.AddRow("[grey]http://url[/]", "Any RSS/website", "-s https://example.com/feed");

        AnsiConsole.Write(table);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Vibes:[/] doom, hopeful, snarky, neutral");
        AnsiConsole.MarkupLine("[bold]Flags:[/] --images, --entities, --json, --raw, --no-llm");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Examples:[/]");
        AnsiConsole.MarkupLine("  doomsummarizer scroll -s hn -s bbc --vibe doom");
        AnsiConsole.MarkupLine("  doomsummarizer scroll \"new pharmaceutical news\"");
        AnsiConsole.MarkupLine("  doomsummarizer scroll \"latest health news\" --no-llm");
        AnsiConsole.MarkupLine("  doomsummarizer scroll -s so:csharp -s reddit:dotnet --entities");
        AnsiConsole.MarkupLine("  doomsummarizer scroll -s \"gnews:climate change\" -s bbc:science");

        return 0;
    }
}
