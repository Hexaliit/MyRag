using DoomSummarizer.Models;
using DoomSummarizer.Plugins;
using Mostlylucid.DoomSummarizer.Plugin.Audio.Commands;
using Spectre.Console.Cli;

namespace Mostlylucid.DoomSummarizer.Plugin.Audio;

/// <summary>
/// Audio analysis plugin — speech-to-text transcription and speaker diarization.
/// </summary>
public class AudioProcessorPlugin : IProcessorPlugin, ICliPlugin
{
    public ProcessorPluginMetadata Metadata { get; } = new()
    {
        Name = "audio",
        DisplayName = "Audio Analyzer",
        Description = "Speech-to-text transcription and speaker diarization for audio files",
        Version = "1.0.0",
        SupportedExtensions = [".mp3", ".wav", ".flac", ".ogg", ".m4a", ".opus"],
        MinActivationWords = 0,
        DocumentTypes = ["audio"]
    };

    public PluginCliMetadata CliMetadata { get; } = new()
    {
        CommandName = "audio",
        Description = "Audio analysis: transcription, speaker diarization",
        Examples = ["audio transcribe podcast.mp3", "audio speakers meeting.wav"]
    };

    public void ConfigureCommands(IConfigurator config)
    {
        config.AddBranch("audio", audio =>
        {
            audio.SetDescription(CliMetadata.Description);
            audio.AddCommand<AudioTranscribeCommand>("transcribe")
                .WithDescription("Transcribe audio to text using speech-to-text")
                .WithExample("audio", "transcribe", "podcast.mp3");
            audio.AddCommand<AudioSpeakersCommand>("speakers")
                .WithDescription("Identify and separate speakers (diarization)")
                .WithExample("audio", "speakers", "meeting.wav");
        });
    }

    public IReadOnlyList<IDocumentReader> Readers => [];
    public IReadOnlyList<IDocumentSplitter> Splitters => [];
    public IReadOnlyList<TemplateDefinition> Templates => [];

    public Task InitializeAsync(ProcessorPluginServices services, CancellationToken ct = default)
        => Task.CompletedTask;

    public bool CanProcess(string markdown, ProcessingContext context)
    {
        if (context.FileName == null) return false;
        var ext = Path.GetExtension(context.FileName);
        return Metadata.SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    public Task<ProcessorResult> ProcessAsync(string markdown, ProcessorOptions options, CancellationToken ct = default)
        => throw new NotSupportedException("Audio processing uses IPipeline. Use CLI commands for direct audio analysis.");
}
