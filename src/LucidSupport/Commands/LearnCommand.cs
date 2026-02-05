using LucidSupport.Services.Learning;
using Spectre.Console;
using Spectre.Console.Cli;

namespace LucidSupport.Commands;

/// <summary>
///     CLI command: lucidsupport learn [url]
///     Learns a web page and outputs a .support.md file.
/// </summary>
internal sealed class LearnCommand : AsyncCommand<LearnCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<url>")]
        public string Url { get; init; } = "";

        [CommandOption("-o|--output")]
        public string? OutputDir { get; init; }

        [CommandOption("--follow-nav")]
        public bool FollowNav { get; init; }

        [CommandOption("--headless")]
        public bool Headless { get; init; } = true;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        await using var learner = new PageLearnerService();

        if (settings.FollowNav)
        {
            AnsiConsole.MarkupLine($"[blue]Learning flow starting at:[/] {settings.Url}");

            var paths = await learner.LearnFlowAndWriteAsync(
                settings.Url,
                settings.OutputDir,
                ct);

            AnsiConsole.MarkupLine($"[green]Learned {paths.Count} pages:[/]");
            foreach (var p in paths)
                AnsiConsole.MarkupLine($"  {p}");
        }
        else
        {
            AnsiConsole.MarkupLine($"[blue]Learning page:[/] {settings.Url}");

            var path = await learner.LearnAndWriteAsync(
                settings.Url,
                settings.OutputDir,
                ct);

            AnsiConsole.MarkupLine($"[green]Wrote:[/] {path}");
        }

        return 0;
    }
}
