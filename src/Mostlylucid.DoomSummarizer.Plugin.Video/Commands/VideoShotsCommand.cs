using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Mostlylucid.DoomSummarizer.Plugin.Video.Commands;

public sealed class VideoShotsCommand : AsyncCommand<VideoShotsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Path to the video file")]
        [CommandArgument(0, "<file>")]
        public string FilePath { get; set; } = "";
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (!File.Exists(settings.FilePath))
        {
            AnsiConsole.MarkupLine($"[red]File not found:[/] {Markup.Escape(settings.FilePath)}");
            return Task.FromResult(1);
        }

        AnsiConsole.MarkupLine($"[cyan]Detecting shot boundaries:[/] {Markup.Escape(Path.GetFileName(settings.FilePath))}");
        AnsiConsole.MarkupLine("[yellow]Shot boundary detection not yet implemented.[/]");
        return Task.FromResult(0);
    }
}
