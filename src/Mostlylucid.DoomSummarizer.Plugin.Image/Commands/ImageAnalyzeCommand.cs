using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Mostlylucid.DoomSummarizer.Plugin.Image.Commands;

public sealed class ImageAnalyzeCommand : AsyncCommand<ImageAnalyzeCommand.Settings>
{
    public override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (!File.Exists(settings.FilePath))
        {
            AnsiConsole.MarkupLine($"[red]File not found:[/] {Markup.Escape(settings.FilePath)}");
            return Task.FromResult(1);
        }

        AnsiConsole.MarkupLine($"[cyan]Analyzing image:[/] {Markup.Escape(Path.GetFileName(settings.FilePath))}");
        AnsiConsole.MarkupLine(
            "[yellow]Image analysis pipeline not yet implemented. Install ImageSummarizer.Core for full functionality.[/]");
        return Task.FromResult(0);
    }

    public sealed class Settings : CommandSettings
    {
        [Description("Path to the image file")]
        [CommandArgument(0, "<file>")]
        public string FilePath { get; set; } = "";
    }
}