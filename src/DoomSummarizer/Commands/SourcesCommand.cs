using System.ComponentModel;
using DoomSummarizer.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DoomSummarizer.Commands;

public sealed class SourcesCommand : Command<SourcesCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    public override int Execute(CommandContext context, Settings settings)
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

        // News sources
        table.AddRow("[blue]bbc[/]", "BBC News Tech", "-s bbc");
        table.AddRow("[blue]guardian[/]", "The Guardian Tech", "-s guardian");
        table.AddRow("[blue]ars[/]", "Ars Technica", "-s ars");
        table.AddRow("[blue]verge[/]", "The Verge", "-s verge");
        table.AddRow("[blue]wired[/]", "Wired", "-s wired");
        table.AddRow("[blue]techcrunch[/]", "TechCrunch", "-s techcrunch");
        table.AddRow("[blue]source:query[/]", "Filter by topic", "-s bbc:AI, -s guardian:climate");

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
        AnsiConsole.MarkupLine("  doomsummarizer scroll \"see what bbc says about AI\"");
        AnsiConsole.MarkupLine("  doomsummarizer scroll -s so:csharp -s reddit:dotnet --entities");
        AnsiConsole.MarkupLine("  doomsummarizer scroll -s reddit:pics --images");

        return 0;
    }
}
