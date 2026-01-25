using DoomSummarizer.Commands;
using Spectre.Console.Cli;

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("doomsummarizer");

    config.AddCommand<ScrollCommand>("scroll")
        .WithDescription("Doom-scroll sources and generate summary (accepts natural language)")
        .WithExample("scroll")
        .WithExample("scroll", "summarize bbc news and hacker news")
        .WithExample("scroll", "snarky take on AI news")
        .WithExample("scroll", "--vibe", "doom")
        .WithExample("scroll", "-s", "search:rust programming")
        .WithExample("scroll", "https://techcrunch.com", "-o", "digest.md");

    config.AddCommand<SetupCommand>("setup")
        .WithDescription("Download required models and setup Playwright")
        .WithExample("setup")
        .WithExample("setup", "--playwright");

    config.AddCommand<TrendsCommand>("trends")
        .WithDescription("Show trends and tone changes over time")
        .WithExample("trends")
        .WithExample("trends", "--days", "14");

    config.AddCommand<ConfigCommand>("config")
        .WithDescription("Show or edit configuration")
        .WithExample("config", "--show")
        .WithExample("config", "--init");

    config.AddCommand<SourcesCommand>("sources")
        .WithDescription("List available sources and examples")
        .WithExample("sources");
});

return await app.RunAsync(args);
