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

    config.AddCommand<CrawlCommand>("crawl")
        .WithDescription("Crawl a website to build a searchable knowledge base")
        .WithExample("crawl", "https://docs.example.com")
        .WithExample("crawl", "https://intranet.company.com", "--name", "intranet", "--depth", "5")
        .WithExample("crawl", "https://wiki.local", "-n", "wiki", "-m", "500");

    config.AddCommand<SourcesCommand>("sources")
        .WithDescription("List available sources and examples")
        .WithExample("sources");

    config.AddCommand<AskCommand>("ask")
        .WithDescription("Interactive Q&A over your stored knowledge base")
        .WithExample("ask", "What happened with the SSH vulnerability?")
        .WithExample("ask", "--source", "crawl:docs", "how does authentication work?")
        .WithExample("ask", "--once", "latest AI news");

    config.AddCommand<BenchmarkCommand>("benchmark")
        .WithDescription("Benchmark Ollama models for speed and quality")
        .WithExample("benchmark")
        .WithExample("benchmark", "qwen3:4b,gemma3:4b,phi4-mini")
        .WithExample("benchmark", "--role", "sentinel", "--rounds", "3")
        .WithExample("benchmark", "qwen3:4b", "--pull");

    config.AddCommand<PageCommand>("page")
        .WithDescription("Download and summarize a single web page")
        .WithExample("page", "https://example.com/article")
        .WithExample("page", "https://example.com/article", "--template", "blog-article")
        .WithExample("page", "https://example.com/article", "-o", "summary.md");
});

return await app.RunAsync(args);
