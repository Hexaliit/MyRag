using System.ComponentModel;
using DoomSummarizer.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DoomSummarizer.Commands;

/// <summary>
/// Interactive Q&A over stored evidence. Provides a chat loop with multi-turn
/// conversation, semantic search, and evidence-grounded answers.
/// </summary>
public sealed class AskCommand : AsyncCommand<AskCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[question]")]
        [Description("Initial question to ask (enters interactive mode after answering)")]
        public string? Question { get; init; }

        [CommandOption("-s|--source")]
        [Description("Filter to source(s) — URL, source name, or KB name (repeatable: -s hn -s reddit)")]
        public string[]? Sources { get; init; }

        [CommandOption("-n|--name")]
        [Description("Query a named knowledge base collection (shorthand for --source crawl:<name>)")]
        public string? Name { get; init; }

        [CommandOption("--days <DAYS>")]
        [Description("How far back to search (default: 30 for general, 365 for crawl sources)")]
        [DefaultValue(0)]
        public int Days { get; init; }

        [CommandOption("--top <N>")]
        [Description("Number of evidence items to use (default: 10)")]
        [DefaultValue(10)]
        public int TopK { get; init; } = 10;

        [CommandOption("--once")]
        [Description("Answer once and exit (no interactive loop)")]
        public bool Once { get; init; }

        [CommandOption("-q|--quiet")]
        [Description("Hide evidence details, show only the answer")]
        public bool Quiet { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        await using var boot = await CommandBootstrap.CreateAsync(cancellationToken);
        var ollama = boot.CreateOllama();
        var llmRouter = await boot.InitializeLlmStackAsync(ct: cancellationToken);

        try { await boot.InitializeEntityGraphStoreAsync(); }
        catch { /* Entity store is optional */ }

        var ollamaAvailable = await ollama.IsAvailableAsync();
        var hasCloudLlm = llmRouter.HasCloudProvider;
        if (!ollamaAvailable && !hasCloudLlm)
        {
            AnsiConsole.MarkupLine("[yellow]No LLM available (Ollama down, no cloud keys).[/] Answers will be limited to evidence listing.");
            AnsiConsole.MarkupLine("[grey]Start Ollama: ollama serve  —or—  set OPENAI_API_KEY / ANTHROPIC_API_KEY[/]");
        }
        else if (!ollamaAvailable && hasCloudLlm)
        {
            AnsiConsole.MarkupLine("[cyan]Ollama not available — using cloud LLM provider[/]");
        }

        var options = new InteractiveAskOptions(
            Sources: settings.Sources,
            Name: settings.Name,
            Days: settings.Days,
            TopK: settings.TopK,
            Once: settings.Once,
            Quiet: settings.Quiet,
            InitialQuestion: settings.Question);

        var loop = new InteractiveAskLoop(boot, ollama, llmRouter, ollamaAvailable, options);
        return await loop.RunAsync(cancellationToken);
    }
}
