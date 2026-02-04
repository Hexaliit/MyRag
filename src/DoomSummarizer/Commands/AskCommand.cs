using System.ComponentModel;
using DoomSummarizer.Services;
using Spectre.Console.Cli;

namespace DoomSummarizer.Commands;

/// <summary>
///     Interactive Q&A over stored evidence. Provides a chat loop with multi-turn
///     conversation, semantic search, and evidence-grounded answers.
/// </summary>
public sealed class AskCommand : AsyncCommand<AskCommand.Settings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings,
        CancellationToken cancellationToken)
    {
        await using var boot = await CommandBootstrap.CreateAsync(cancellationToken);

        return await boot.StartAskLoopAsync(new InteractiveAskOptions(
            settings.Sources,
            settings.Name,
            settings.Days,
            settings.TopK,
            settings.Once,
            settings.Quiet,
            settings.Question), cancellationToken);
    }

    public sealed class Settings : InteractiveSettings
    {
        [CommandArgument(0, "[question]")]
        [Description("Initial question to ask (enters interactive mode after answering)")]
        public string? Question { get; init; }

        [CommandOption("-s|--source")]
        [Description("Filter to source(s) — URL, source name, or KB name (repeatable: -s hn -s reddit)")]
        public string[]? Sources { get; init; }

        [CommandOption("--days <DAYS>")]
        [Description("How far back to search (default: 30 for general, 365 for crawl sources)")]
        [DefaultValue(0)]
        public int Days { get; init; }

        [CommandOption("--top <N>")]
        [Description("Number of evidence items to use (default: 10)")]
        [DefaultValue(10)]
        public int TopK { get; init; } = 10;
    }
}