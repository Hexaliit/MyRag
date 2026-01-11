using System.CommandLine;
using AudioSummarizer.Core.Models;
using AudioSummarizer.Core.Services.Analysis;
using LucidRAG.Cli.Services;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace LucidRAG.Cli.Commands;

/// <summary>
///     Analyze audio files for forensic characterization, transcription, and speaker analysis
/// </summary>
public static class AudioCommand
{
    private static readonly Argument<string[]> AudioFilesArg = new("audio-files")
    {
        Description = "Audio files (.mp3, .wav, .m4a, .flac, .ogg) to analyze",
        Arity = ArgumentArity.OneOrMore
    };

    private static readonly Option<bool> ShowSignalsOpt = new("--signals", "-s")
    {
        Description = "Show all extracted signals with metadata",
        DefaultValueFactory = _ => false
    };

    private static readonly Option<bool> VerboseOpt = new("-v", "--verbose")
    {
        Description = "Verbose output with detailed analysis",
        DefaultValueFactory = _ => false
    };

    private static readonly Option<string?> DataDirOpt = new("--data-dir")
    {
        Description = "Data directory for models/cache"
    };

    private static readonly Option<bool> JsonOpt = new("--json", "-j")
    {
        Description = "Output as JSON",
        DefaultValueFactory = _ => false
    };

    public static Command Create()
    {
        var command = new Command("audio",
            "Analyze audio files (forensic characterization, transcription, speaker analysis)");
        command.Arguments.Add(AudioFilesArg);
        command.Options.Add(ShowSignalsOpt);
        command.Options.Add(VerboseOpt);
        command.Options.Add(DataDirOpt);
        command.Options.Add(JsonOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var audioPaths = parseResult.GetValue(AudioFilesArg) ?? [];
            var showSignals = parseResult.GetValue(ShowSignalsOpt);
            var verbose = parseResult.GetValue(VerboseOpt);
            var dataDir = parseResult.GetValue(DataDirOpt);
            var jsonOutput = parseResult.GetValue(JsonOpt);

            var config = new CliConfig
            {
                DataDirectory = Program.EnsureDataDirectory(dataDir),
                Verbose = verbose
            };

            if (!jsonOutput)
            {
                AnsiConsole.Write(new FigletText("AudioSummarizer").Color(Color.Magenta1));
                AnsiConsole.MarkupLine("[dim]Forensic audio characterization without cultural identification[/]\n");
            }

            await using var services = CliServiceRegistration.BuildServiceProvider(config, verbose);
            using var scope = services.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<AudioWaveOrchestrator>();

            foreach (var path in audioPaths)
            {
                var fullPath = Path.GetFullPath(path);

                if (!File.Exists(fullPath))
                {
                    if (!jsonOutput)
                        AnsiConsole.MarkupLine($"[red]✗ File not found:[/] {fullPath}\n");
                    else
                        Console.WriteLine($"{{\"error\": \"File not found: {fullPath}\"}}");
                    continue;
                }

                try
                {
                    AudioProfile profile;

                    if (!jsonOutput)
                    {
                        AnsiConsole.MarkupLine($"[magenta bold]═══ {Path.GetFileName(fullPath)} ═══[/]\n");

                        profile = await AnsiConsole.Status()
                            .Spinner(Spinner.Known.Dots)
                            .StartAsync("Analyzing audio...",
                                async ctx => { return await orchestrator.AnalyzeAsync(fullPath, ct); });
                    }
                    else
                    {
                        profile = await orchestrator.AnalyzeAsync(fullPath, ct);
                    }

                    if (jsonOutput)
                        Console.WriteLine(profile.ToJson());
                    else
                        DisplayProfile(profile, showSignals, verbose);
                }
                catch (Exception ex)
                {
                    if (!jsonOutput)
                        AnsiConsole.MarkupLine($"[red]✗ Analysis failed:[/] {ex.Message}\n");
                    else
                        Console.WriteLine($"{{\"error\": \"{ex.Message}\"}}");
                }
            }
        });

        return command;
    }

    private static void DisplayProfile(AudioProfile profile, bool showSignals, bool verbose)
    {
        // Display summary
        var table = new Table();
        table.Border(TableBorder.Rounded);
        table.BorderColor(Color.Magenta);
        table.AddColumn(new TableColumn("[bold]Property[/]").LeftAligned());
        table.AddColumn(new TableColumn("[bold]Value[/]").LeftAligned());

        // File info
        table.AddRow("File", Path.GetFileName(profile.AudioPath));

        // Deterministic signals
        var format = profile.GetValue<string>("audio.format");
        var duration = profile.GetValue<double>("audio.duration_seconds");
        var sampleRate = profile.GetValue<int>("audio.sample_rate");
        var channels = profile.GetValue<int>("audio.channels");
        var bitrateKbps = profile.GetValue<int>("audio.bitrate_kbps");
        var sha256 = profile.GetValue<string>("audio.hash.sha256");
        var pcmSha256 = profile.GetValue<string>("audio.hash.pcm_sha256");

        if (format != null)
            table.AddRow("Format", format.ToUpperInvariant());

        if (duration > 0)
        {
            var timeSpan = TimeSpan.FromSeconds(duration);
            table.AddRow("Duration", $"{timeSpan:mm\\:ss\\.ff}");
        }

        if (sampleRate > 0)
            table.AddRow("Sample Rate", $"{sampleRate:N0} Hz");

        if (channels > 0)
            table.AddRow("Channels", channels.ToString());

        if (bitrateKbps > 0)
            table.AddRow("Bitrate", $"{bitrateKbps} kbps");

        table.AddRow("Signals Extracted", profile.Signals.Count.ToString());
        table.AddRow("Processing Time", $"{profile.ProcessingTimeMs} ms");

        if (profile.Errors.Count > 0)
            table.AddRow("[red]Errors[/]", profile.Errors.Count.ToString());

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        // Display identity hashes
        if (verbose || showSignals)
        {
            AnsiConsole.MarkupLine("[bold underline]Identity (Cryptographic)[/]");
            if (sha256 != null)
                AnsiConsole.MarkupLine($"  [dim]SHA-256:[/] {sha256}");
            if (pcmSha256 != null)
                AnsiConsole.MarkupLine($"  [dim]PCM SHA-256:[/] {pcmSha256}");
            AnsiConsole.WriteLine();
        }

        // Display all signals
        if (showSignals)
        {
            AnsiConsole.MarkupLine("[bold underline]All Signals[/]");

            var signalTable = new Table();
            signalTable.Border(TableBorder.Simple);
            signalTable.AddColumn("[bold]Signal[/]");
            signalTable.AddColumn("[bold]Value[/]");
            signalTable.AddColumn("[bold]Type[/]");
            signalTable.AddColumn("[bold]Source[/]");

            foreach (var signal in profile.Signals.Values.OrderBy(s => s.Name))
            {
                var valueStr = signal.Value?.ToString() ?? "";
                if (valueStr.Length > 60)
                    valueStr = valueStr[..57] + "...";

                var typeColor = signal.Type switch
                {
                    SignalType.Identity => "cyan",
                    SignalType.Metadata => "blue",
                    SignalType.Acoustic => "green",
                    SignalType.Classification => "yellow",
                    SignalType.Transcription => "magenta",
                    SignalType.Speaker => "red",
                    _ => "white"
                };

                signalTable.AddRow(
                    signal.Name,
                    valueStr,
                    $"[{typeColor}]{signal.Type}[/]",
                    signal.Source ?? "-"
                );
            }

            AnsiConsole.Write(signalTable);
            AnsiConsole.WriteLine();
        }

        // Display errors
        if (profile.Errors.Count > 0)
        {
            AnsiConsole.MarkupLine("[bold red underline]Errors[/]");
            foreach (var error in profile.Errors) AnsiConsole.MarkupLine($"  [red]•[/] {error}");
            AnsiConsole.WriteLine();
        }
    }
}