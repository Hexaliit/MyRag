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

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
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

                // 4. Check Ollama availability and models
                ctx.Status("Checking Ollama availability...");
                var ollama = new OllamaService(config.Ollama);
                if (await ollama.IsAvailableAsync())
                {
                    AnsiConsole.MarkupLine($"[green]\u2713[/] Ollama available at {config.Ollama.BaseUrl}");

                    var models = await ollama.GetAvailableModelsAsync();
                    var requiredModels = new[] { config.Ollama.Model, config.Ollama.SentinelModel };
                    foreach (var required in requiredModels.Distinct())
                    {
                        var found = models.Any(m => m.StartsWith(required.Split(':')[0], StringComparison.OrdinalIgnoreCase));
                        if (found)
                        {
                            AnsiConsole.MarkupLine($"   [green]\u2713[/] Model [bold]{required}[/] available");
                        }
                        else
                        {
                            AnsiConsole.MarkupLine($"   [yellow]\u26a0[/] Model [bold]{required}[/] not found — pull it:");
                            AnsiConsole.MarkupLine($"     [grey]ollama pull {required}[/]");
                        }
                    }

                    if (models.Count > 0)
                    {
                        var otherModels = models.Where(m => !requiredModels.Any(r =>
                            m.StartsWith(r.Split(':')[0], StringComparison.OrdinalIgnoreCase))).Take(5).ToList();
                        if (otherModels.Count > 0)
                            AnsiConsole.MarkupLine($"   [grey]Other available: {string.Join(", ", otherModels)}[/]");
                    }
                }
                else
                {
                    AnsiConsole.MarkupLine($"[yellow]\u26a0[/] Ollama not running at {config.Ollama.BaseUrl}");
                    AnsiConsole.MarkupLine("   Summaries will use ONNX signals only (no LLM). Start Ollama:");
                    AnsiConsole.MarkupLine("   [grey]ollama serve[/]");
                    AnsiConsole.MarkupLine($"   [grey]ollama pull {config.Ollama.Model}[/]");
                    AnsiConsole.MarkupLine($"   [grey]ollama pull {config.Ollama.SentinelModel}[/]");
                }

                // 5. Create templates directory
                var templatesDir = Path.Combine(ConfigService.GetConfigDir(), "templates");
                if (!Directory.Exists(templatesDir))
                {
                    Directory.CreateDirectory(templatesDir);
                    AnsiConsole.MarkupLine($"[green]\u2713[/] Templates directory: {templatesDir}");
                    AnsiConsole.MarkupLine("   [grey]Place .yaml or .liquid files here for custom output templates[/]");
                }
                else
                {
                    var yamlCount = Directory.EnumerateFiles(templatesDir, "*.yaml")
                        .Concat(Directory.EnumerateFiles(templatesDir, "*.yml")).Count();
                    var liquidCount = Directory.GetFiles(templatesDir, "*.liquid").Length;
                    AnsiConsole.MarkupLine($"[green]\u2713[/] Templates: {yamlCount} YAML + {liquidCount} Liquid in {templatesDir}");
                }

                // 6. Download NER model if requested
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

                // 7. Install Playwright if requested
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
                            AnsiConsole.MarkupLine($"[yellow]\u26a0[/] Playwright install exited with code {exitCode} (common in single-file builds)");
                            AnsiConsole.MarkupLine("   Install manually: [grey]dotnet tool install --global Microsoft.Playwright.CLI && playwright install chromium[/]");
                            AnsiConsole.MarkupLine("   Or: [grey]npx playwright install chromium[/]");
                        }
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[yellow]\u26a0[/] Playwright failed (common in single-file builds): {Markup.Escape(ex.Message)}");
                        AnsiConsole.MarkupLine("   Install manually: [grey]dotnet tool install --global Microsoft.Playwright.CLI && playwright install chromium[/]");
                        AnsiConsole.MarkupLine("   Or: [grey]npx playwright install chromium[/]");
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
        AnsiConsole.MarkupLine("  [cyan]doomsummarizer scroll[/]                          - Fetch and summarize");
        AnsiConsole.MarkupLine("  [cyan]doomsummarizer scroll \"AI news\" --vibe snarky[/]   - Topic + vibe");
        AnsiConsole.MarkupLine("  [cyan]doomsummarizer scroll --json --nollm[/]            - Fast JSON for tools");
        AnsiConsole.MarkupLine("  [cyan]doomsummarizer crawl https://docs.example.com[/]   - Build knowledge base");
        AnsiConsole.MarkupLine("  [cyan]doomsummarizer scroll \"query\" --local[/]           - Query stored KB");
        AnsiConsole.MarkupLine("  [cyan]doomsummarizer trends[/]                           - Historical trends");
        AnsiConsole.MarkupLine("  [cyan]doomsummarizer config --show[/]                    - View configuration");
        AnsiConsole.MarkupLine("  [cyan]doomsummarizer scroll --list-templates[/]           - List output templates");
        AnsiConsole.MarkupLine("  [cyan]doomsummarizer scroll -t problem-solution \"topic\"[/] - Use YAML template");

        return 0;
    }
}
