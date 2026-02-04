using System.ComponentModel;
using DoomSummarizer.Services;
using Spectre.Console.Cli;

namespace DoomSummarizer.Commands;

/// <summary>
///     Built-in manual: Q&amp;A over DoomSummarizer's own documentation.
///     Auto-downloads docs from GitHub on first use and indexes them
///     under the reserved "manual" source tag.
/// </summary>
public sealed class ManCommand : AsyncCommand<ManCommand.Settings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings,
        CancellationToken cancellationToken)
    {
        if (settings.ListGpus) { await CommandBootstrap.ListGpusAsync(); return 0; }

        await using var boot = await CommandBootstrap.CreateAsync(settings.GpuDeviceId, cancellationToken);

        // Auto-load manual if not present (or if --refresh/--load-manual)
        var loader = new ManualLoader(boot.Storage, boot.Embedding);
        var needsLoad = settings.Refresh || settings.LoadManual
                                         || !await loader.IsManualLoadedAsync();

        if (needsLoad)
        {
            if (boot.EntityStore == null)
                await boot.InitializeEntityStoresAsync();

            using var processor = await ItemProcessor.CreateAsync(
                boot.Embedding, boot.Storage, boot.EntityStore, "default", cancellationToken);
            await loader.LoadManualAsync(processor, settings.Refresh || settings.LoadManual, cancellationToken);
        }

        return await boot.StartAskLoopAsync(new InteractiveAskOptions(
            ManualLoader.ManualSource,
            null,
            0,
            settings.TopK,
            settings.Once,
            settings.Quiet,
            settings.Question,
            PromptTemplate: "manual-answer"), cancellationToken);
    }

    public sealed class Settings : InteractiveSettings
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
    }
}