using System.ComponentModel;
using DoomSummarizer.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DoomSummarizer.Commands;

public sealed class SetupCommand : AsyncCommand<SetupCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--playwright")]
        [Description("Also install Playwright browsers")]
        public bool Playwright { get; init; }

        [CommandOption("--ner")]
        [Description("Download NER model for entity extraction (~430MB)")]
        public bool Ner { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        AnsiConsole.Write(new FigletText("DoomSummarizer").Color(Color.Red));
        AnsiConsole.MarkupLine("[grey]Setting up your doom-scrolling agent...[/]");
        AnsiConsole.WriteLine();

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("green"))
            .StartAsync("Setting up...", async ctx =>
            {
                // 1. Initialize config
                ctx.Status("Initializing configuration...");
                var config = await ConfigService.LoadAsync();
                var dbPath = ConfigService.GetDbPath(config);
                AnsiConsole.MarkupLine($"[green]\u2713[/] Config directory: {Path.GetDirectoryName(dbPath)}");

                // 2. Download ONNX models
                ctx.Status("Setting up ONNX embedding model...");
                using var embedding = new EmbeddingService();
                await embedding.SetupAsync(new Progress<string>(msg => ctx.Status(msg)));
                AnsiConsole.MarkupLine("[green]\u2713[/] ONNX model (all-MiniLM-L6-v2) ready");

                // 3. Initialize database
                ctx.Status("Initializing database...");
                await using var storage = new StorageService(dbPath);
                await storage.InitializeAsync();
                AnsiConsole.MarkupLine($"[green]\u2713[/] Database initialized: {dbPath}");

                // 4. Check Ollama
                ctx.Status("Checking Ollama availability...");
                var ollama = new OllamaService(config.Ollama);
                if (await ollama.IsAvailableAsync())
                {
                    AnsiConsole.MarkupLine($"[green]\u2713[/] Ollama available at {config.Ollama.BaseUrl}");
                    AnsiConsole.MarkupLine($"   Model: {config.Ollama.Model}");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[yellow]\u26a0[/] Ollama not running at {config.Ollama.BaseUrl}");
                    AnsiConsole.MarkupLine("   Summaries will be basic. Start Ollama for full features:");
                    AnsiConsole.MarkupLine("   [grey]ollama serve[/]");
                    AnsiConsole.MarkupLine($"   [grey]ollama pull {config.Ollama.Model}[/]");
                }

                // 5. Download NER model if requested
                if (settings.Ner)
                {
                    ctx.Status("Downloading NER model...");
                    using var ner = new NerService();
                    var success = await ner.EnsureModelAsync(msg => ctx.Status(msg));
                    if (success)
                        AnsiConsole.MarkupLine("[green]\u2713[/] NER model (BERT-NER) ready");
                    else
                        AnsiConsole.MarkupLine("[yellow]\u26a0[/] NER model download failed");
                }
                else
                {
                    using var ner = new NerService();
                    if (ner.IsAvailable)
                        AnsiConsole.MarkupLine("[green]\u2713[/] NER model already available");
                    else
                        AnsiConsole.MarkupLine("[grey]-[/] NER skipped (use --ner to download, needed for --entities)");
                }

                // 6. Install Playwright if requested
                if (settings.Playwright)
                {
                    ctx.Status("Installing Playwright browsers...");
                    try
                    {
                        var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
                        if (exitCode == 0)
                        {
                            AnsiConsole.MarkupLine("[green]\u2713[/] Playwright Chromium installed");
                        }
                        else
                        {
                            AnsiConsole.MarkupLine("[yellow]\u26a0[/] Playwright installation returned non-zero exit code");
                        }
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[yellow]\u26a0[/] Playwright installation failed: {ex.Message}");
                        AnsiConsole.MarkupLine("   Run manually: [grey]playwright install chromium[/]");
                    }
                }
                else
                {
                    AnsiConsole.MarkupLine("[grey]-[/] Playwright skipped (use --playwright to install)");
                }
            });

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold green]Setup complete![/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Quick start:");
        AnsiConsole.MarkupLine("  [cyan]doomsummarizer scroll[/]              - Fetch and summarize (neutral vibe)");
        AnsiConsole.MarkupLine("  [cyan]doomsummarizer scroll --vibe doom[/]  - Pessimistic summary");
        AnsiConsole.MarkupLine("  [cyan]doomsummarizer scroll --vibe hopeful[/] - Optimistic summary");
        AnsiConsole.MarkupLine("  [cyan]doomsummarizer scroll --vibe snarky[/] - Witty commentary");
        AnsiConsole.MarkupLine("  [cyan]doomsummarizer trends[/]              - View historical trends");
        AnsiConsole.MarkupLine("  [cyan]doomsummarizer config --show[/]       - View configuration");

        return 0;
    }
}
