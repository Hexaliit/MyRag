using System.ComponentModel;
using DoomSummarizer.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DoomSummarizer.Commands;

public sealed class SourcesCommand : AsyncCommand<SourcesCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
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

        // Search APIs (cascading — uses best available key)
        table.AddRow("[grey]search:q[/]", "Web search (auto-selects best API)", "-s \"search:rust programming\"");
        table.AddRow("[grey]brave / brave_search[/]", "Brave Search", "-s brave, -s \"brave:AI agents\"");
        table.AddRow("[grey]bravenews / brave_news[/]", "Brave News search", "-s bravenews");
        table.AddRow("[grey]serper[/]", "Serper (Google SERP)", "-s serper, -s \"serper:AI news\"");
        table.AddRow("[grey]serpernews / serper_news[/]", "Serper news search", "-s serpernews");
        table.AddRow("[grey]tavily[/]", "Tavily AI search", "-s tavily");
        table.AddRow("[grey]newsapi / news_api[/]", "NewsAPI.org", "-s newsapi");
        table.AddRow("[grey]newsdata / news_data[/]", "NewsData.io", "-s newsdata");
        table.AddRow("[grey]jina[/]", "Jina AI search", "-s jina");
        table.AddRow("[grey]gsearch[/]", "Google Custom Search", "-s \"gsearch:query\"");
        table.AddRow("[grey]gplaces[/]", "Google Places", "-s \"gplaces:restaurant\"");

        table.AddEmptyRow();

        // Specialty sources
        table.AddRow("[dim]arxiv[/]", "arXiv papers", "-s arxiv, -s \"arxiv:machine learning\"");
        table.AddRow($"[dim]{Markup.Escape("arxiv:cat:X")}[/]", "arXiv category", Markup.Escape("-s arxiv:cat:cs.AI"));
        table.AddRow("[dim]wiki / wikipedia[/]", "Wikipedia current events", "-s wiki");
        table.AddRow("[dim]spaceflight / space[/]", "Spaceflight News API", "-s spaceflight");
        table.AddRow("[dim]earthquake[/]", "USGS earthquake data", "-s earthquake");
        table.AddRow("[dim]factcheck[/]", "Fact-checking sites", "-s factcheck, -s factcheck:snopes");

        table.AddEmptyRow();

        // URLs and crawl
        table.AddRow("[grey]http://url[/]", "Any RSS/website URL", "-s https://example.com/feed");
        table.AddRow("[grey]crawl:name[/]", "Local crawled knowledge base", "-s crawl:docs");

        AnsiConsole.Write(table);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Vibes:[/] doom, hopeful, snarky, funny, upbeat, friendly, neutral");
        AnsiConsole.MarkupLine("[bold]Flags:[/] --images, --entities, --graph, --json, --raw, --no-llm, --local, --debug");

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
        var apiKeys = ApiKeyService.Load(config);
        apiKeys.PrintStatus();

        return 0;
    }
}
