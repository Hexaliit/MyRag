using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Mostlylucid.DoomSummarizer.Plugin.Audio.Commands;

public sealed class AudioSpeakersCommand : AsyncCommand<AudioSpeakersCommand.Settings>
{
    public override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (!File.Exists(settings.FilePath))
        {
            AnsiConsole.MarkupLine($"[red]File not found:[/] {Markup.Escape(settings.FilePath)}");
            return Task.FromResult(1);
        }

        AnsiConsole.MarkupLine($"[cyan]Analyzing speakers:[/] {Markup.Escape(Path.GetFileName(settings.FilePath))}");
        AnsiConsole.MarkupLine(
            "[yellow]Speaker diarization not yet implemented. Install AudioSummarizer.Core for full functionality.[/]");
        return Task.FromResult(0);
    }

    public sealed class Settings : CommandSettings
    {
        [Description("Path to the audio file")]
        [CommandArgument(0, "<file>")]
        public string FilePath { get; set; } = "";
    }
}