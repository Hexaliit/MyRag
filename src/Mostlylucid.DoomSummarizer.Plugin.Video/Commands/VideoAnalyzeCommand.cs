using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Mostlylucid.DoomSummarizer.Plugin.Video.Commands;

public sealed class VideoAnalyzeCommand : AsyncCommand<VideoAnalyzeCommand.Settings>
{
    public override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (!File.Exists(settings.FilePath))
        {
            AnsiConsole.MarkupLine($"[red]File not found:[/] {Markup.Escape(settings.FilePath)}");
            return Task.FromResult(1);
        }

        AnsiConsole.MarkupLine($"[cyan]Analyzing video:[/] {Markup.Escape(Path.GetFileName(settings.FilePath))}");
        AnsiConsole.MarkupLine(
            "[yellow]Video analysis pipeline not yet implemented. Install VideoSummarizer.Core for full functionality.[/]");
        return Task.FromResult(0);
    }

    public sealed class Settings : CommandSettings
    {
        [Description("Path to the video file")]
        [CommandArgument(0, "<file>")]
        public string FilePath { get; set; } = "";
    }
}