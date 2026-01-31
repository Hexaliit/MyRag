using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Mostlylucid.DoomSummarizer.Plugin.Image.Commands;

public sealed class ImageCaptionCommand : AsyncCommand<ImageCaptionCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Path to the image file")]
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

        AnsiConsole.MarkupLine($"[cyan]Vision model captioning:[/] {Markup.Escape(Path.GetFileName(settings.FilePath))}");
        AnsiConsole.MarkupLine("[yellow]Vision model captioning not yet implemented. Install ImageSummarizer.Core for full functionality.[/]");
        return Task.FromResult(0);
    }
}
