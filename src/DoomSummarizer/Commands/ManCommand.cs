using System.ComponentModel;
using DoomSummarizer.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DoomSummarizer.Commands;

/// <summary>
/// Built-in manual: Q&amp;A over DoomSummarizer's own documentation.
/// Auto-downloads docs from GitHub on first use and indexes them
/// under the reserved "manual" source tag.
/// </summary>
public sealed class ManCommand : AsyncCommand<ManCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[question]")]
        [Description("Question about DoomSummarizer")]
        public string? Question { get; init; }

        [CommandOption("--refresh")]
        [Description("Re-download manual documentation from GitHub")]
        public bool Refresh { get; init; }

        [CommandOption("--load-manual")]
        [Description("Force load/reload the manual corpus")]
        public bool LoadManual { get; init; }

        [CommandOption("--top <N>")]
        [Description("Number of evidence items to use (default: 8)")]
        [DefaultValue(8)]
        public int TopK { get; init; } = 8;

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

        // Auto-load manual if not present (or if --refresh/--load-manual)
        var loader = new ManualLoader(boot.Storage, boot.Embedding);
        var needsLoad = settings.Refresh || settings.LoadManual
            || !await loader.IsManualLoadedAsync();

        if (needsLoad)
        {
            var processor = await ItemProcessor.CreateAsync(
                boot.Embedding, boot.Storage, boot.EntityStore, cancellationToken);
            await loader.LoadManualAsync(processor, settings.Refresh || settings.LoadManual, cancellationToken);
        }

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
            Source: ManualLoader.ManualSource,
            Name: null,
            Days: 0,
            TopK: settings.TopK,
            Once: settings.Once,
            Quiet: settings.Quiet,
            InitialQuestion: settings.Question,
            PromptTemplate: "manual-answer");

        var loop = new InteractiveAskLoop(boot, ollama, llmRouter, ollamaAvailable, options);
        return await loop.RunAsync(cancellationToken);
    }
}
