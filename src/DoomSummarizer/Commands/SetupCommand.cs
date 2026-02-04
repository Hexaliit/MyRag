using System.ComponentModel;
using DoomSummarizer.Helpers;
using DoomSummarizer.Services;
using Spectre.Console;
using Spectre.Console.Cli;
#if FEATURE_LLAMASHARP
using Mostlylucid.DocSummarizer.LLamaSharp.Config;
using Mostlylucid.DocSummarizer.LLamaSharp.Services;
#endif

namespace DoomSummarizer.Commands;

public sealed class SetupCommand : AsyncCommand<SetupCommand.Settings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings,
        CancellationToken cancellationToken)
    {
#if FEATURE_COMPLETE
        AnsiConsole.Write(new FigletText("LucidRAG").Color(Color.Cyan1));
        AnsiConsole.MarkupLine("[grey]Setting up LucidRAG (complete)...[/]");
#else
        AnsiConsole.Write(new FigletText("DoomSummarizer").Color(Color.Red));
        AnsiConsole.MarkupLine("[grey]Setting up your doom-scrolling agent...[/]");
#endif
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
                AnsiConsole.MarkupLine(
                    $"[green]\u2713[/] Config directory: {FormattingHelpers.Esc(Path.GetDirectoryName(dbPath))}");

                // 2. Download ONNX models
                ctx.Status("Setting up ONNX embedding model...");
                var embedding = await EmbeddingFactory.CreateAsync(
                    onStatus: msg => ctx.Status(msg),
                    ct: cancellationToken);
                AnsiConsole.MarkupLine("[green]\u2713[/] ONNX model (all-MiniLM-L6-v2) ready");

#if FEATURE_LLAMASHARP
                // 2b. Download local LLM models (LLamaSharp GGUF)
                // lucidrag (complete): skip by default (uses Ollama), opt in with --local-llm
                // doomsummarizer with -p:IncludeLLamaSharp=true: auto-download by default
#if FEATURE_COMPLETE
                var shouldDownloadLocalLlm = settings.LocalLlm;
                var skipReason = "use --local-llm to download, or use Ollama (recommended)";
#else
                var shouldDownloadLocalLlm = !settings.SkipLocalLlm;
                var skipReason = "use --local-llm to download";
#endif
                if (!settings.SkipLocalLlm)
                {
                    ctx.Status("Setting up local LLM models...");
                    try
                    {
                        var llamaConfig = new LLamaSharpConfig();
                        using var downloader = new LLamaSharpModelDownloader(llamaConfig);

                        var sentinelModel = LLamaSharpModelRegistry.GetSentinel(llamaConfig.SentinelModel);
                        var synthesisModel = LLamaSharpModelRegistry.GetSynthesis(llamaConfig.SynthesisModel);

                        if (downloader.IsModelAvailable(sentinelModel))
                        {
                            AnsiConsole.MarkupLine($"[green]\u2713[/] Sentinel model ({sentinelModel.DisplayName}) already downloaded");
                        }
                        else if (shouldDownloadLocalLlm || llamaConfig.AutoDownload)
                        {
                            ctx.Status($"Downloading sentinel model ({sentinelModel.DisplayName}, ~{sentinelModel.ExpectedSizeBytes / 1_000_000}MB)...");
                            await downloader.EnsureModelAsync(sentinelModel, ct: cancellationToken);
                            AnsiConsole.MarkupLine($"[green]\u2713[/] Sentinel model ({sentinelModel.DisplayName}) ready");
                        }
                        else
                        {
                            AnsiConsole.MarkupLine($"[grey]-[/] Sentinel model not downloaded ({skipReason})");
                        }

                        if (downloader.IsModelAvailable(synthesisModel))
                        {
                            AnsiConsole.MarkupLine($"[green]\u2713[/] Synthesis model ({synthesisModel.DisplayName}) already downloaded");
                        }
                        else if (shouldDownloadLocalLlm || llamaConfig.AutoDownload)
                        {
                            ctx.Status($"Downloading synthesis model ({synthesisModel.DisplayName}, ~{synthesisModel.ExpectedSizeBytes / 1_000_000}MB)...");
                            await downloader.EnsureModelAsync(synthesisModel, ct: cancellationToken);
                            AnsiConsole.MarkupLine($"[green]\u2713[/] Synthesis model ({synthesisModel.DisplayName}) ready");
                        }
                        else
                        {
                            AnsiConsole.MarkupLine($"[grey]-[/] Synthesis model not downloaded ({skipReason})");
                        }
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[yellow]\u26a0[/] Local LLM setup failed: {Markup.Escape(ex.Message)}");
                        AnsiConsole.MarkupLine("   [grey]Models will be downloaded on first use, or use --local-llm to retry[/]");
                    }
                }
                else
                {
                    AnsiConsole.MarkupLine("[grey]-[/] Local LLM skipped (use --local-llm to download)");
                }
#else
                AnsiConsole.MarkupLine("[grey]-[/] Local LLM (LLamaSharp) not included in this build");
                AnsiConsole.MarkupLine(
                    "   [grey]Use Ollama for local inference, or use the lucidrag (complete) build[/]");
#endif

                // 3. Initialize database
                ctx.Status("Initializing database...");
                await using var storage = new StorageService(dbPath);
                await storage.InitializeAsync();
                AnsiConsole.MarkupLine($"[green]\u2713[/] Database initialized: {FormattingHelpers.Esc(dbPath)}");

                // 4. Check Ollama availability and models
                ctx.Status("Checking Ollama availability...");
                var ollama = new OllamaService(config.Ollama);
                if (await ollama.IsAvailableAsync())
                {
                    AnsiConsole.MarkupLine(
                        $"[green]\u2713[/] Ollama available at {FormattingHelpers.Esc(config.Ollama.BaseUrl)}");

                    var models = await ollama.GetAvailableModelsAsync();
                    var requiredModels = new[] { config.Ollama.Model, config.Ollama.SentinelModel };
                    foreach (var required in requiredModels.Distinct())
                    {
                        var found = models.Any(m =>
                            m.StartsWith(required.Split(':')[0], StringComparison.OrdinalIgnoreCase));
                        if (found)
                        {
                            AnsiConsole.MarkupLine(
                                $"   [green]\u2713[/] Model [bold]{FormattingHelpers.Esc(required)}[/] available");
                        }
                        else
                        {
                            AnsiConsole.MarkupLine(
                                $"   [yellow]\u26a0[/] Model [bold]{FormattingHelpers.Esc(required)}[/] not found — pull it:");
                            AnsiConsole.MarkupLine($"     [grey]ollama pull {FormattingHelpers.Esc(required)}[/]");
                        }
                    }

                    if (models.Count > 0)
                    {
                        var otherModels = models.Where(m => !requiredModels.Any(r =>
                            m.StartsWith(r.Split(':')[0], StringComparison.OrdinalIgnoreCase))).Take(5).ToList();
                        if (otherModels.Count > 0)
                            AnsiConsole.MarkupLine(
                                $"   [grey]Other available: {FormattingHelpers.Esc(string.Join(", ", otherModels))}[/]");
                    }
                }
                else
                {
#if FEATURE_COMPLETE
                    AnsiConsole.MarkupLine($"[yellow]\u26a0[/] Ollama not running at {FormattingHelpers.Esc(config.Ollama.BaseUrl)}");
                    AnsiConsole.MarkupLine("   LucidRAG uses Ollama by default. Start it:");
                    AnsiConsole.MarkupLine("   [grey]ollama serve[/]");
                    AnsiConsole.MarkupLine($"   [grey]ollama pull {FormattingHelpers.Esc(config.Ollama.Model)}[/]");
                    AnsiConsole.MarkupLine($"   [grey]ollama pull {FormattingHelpers.Esc(config.Ollama.SentinelModel)}[/]");
#if FEATURE_LLAMASHARP
                    AnsiConsole.MarkupLine("   [grey]Or use local GGUF models: lucidrag setup --local-llm[/]");
#endif
#elif FEATURE_LLAMASHARP
                    AnsiConsole.MarkupLine($"[grey]-[/] Ollama not running at {FormattingHelpers.Esc(config.Ollama.BaseUrl)} (optional — LLamaSharp handles LLM locally)");
                    AnsiConsole.MarkupLine($"   [grey]To use Ollama instead: ollama serve && ollama pull {FormattingHelpers.Esc(config.Ollama.Model)}[/]");
#else
                    AnsiConsole.MarkupLine(
                        $"[yellow]\u26a0[/] Ollama not running at {FormattingHelpers.Esc(config.Ollama.BaseUrl)}");
                    AnsiConsole.MarkupLine(
                        $"   [grey]Install Ollama for LLM synthesis: ollama serve && ollama pull {FormattingHelpers.Esc(config.Ollama.Model)}[/]");
                    AnsiConsole.MarkupLine("   [grey]Or set ANTHROPIC_API_KEY / OPENAI_API_KEY for cloud LLM[/]");
#endif
                }

                // 5. Create templates directory
                var templatesDir = Path.Combine(ConfigService.GetConfigDir(), "templates");
                if (!Directory.Exists(templatesDir))
                {
                    Directory.CreateDirectory(templatesDir);
                    AnsiConsole.MarkupLine(
                        $"[green]\u2713[/] Templates directory: {FormattingHelpers.Esc(templatesDir)}");
                    AnsiConsole.MarkupLine("   [grey]Place .yaml or .liquid files here for custom output templates[/]");
                }
                else
                {
                    var yamlCount = Directory.EnumerateFiles(templatesDir, "*.yaml")
                        .Concat(Directory.EnumerateFiles(templatesDir, "*.yml")).Count();
                    var liquidCount = Directory.GetFiles(templatesDir, "*.liquid").Length;
                    AnsiConsole.MarkupLine(
                        $"[green]\u2713[/] Templates: {yamlCount} YAML + {liquidCount} Liquid in {FormattingHelpers.Esc(templatesDir)}");
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
                            AnsiConsole.MarkupLine(
                                $"[yellow]\u26a0[/] Playwright install exited with code {exitCode} (common in single-file builds)");
                            AnsiConsole.MarkupLine(
                                "   Install manually: [grey]dotnet tool install --global Microsoft.Playwright.CLI && playwright install chromium[/]");
                            AnsiConsole.MarkupLine("   Or: [grey]npx playwright install chromium[/]");
                        }
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine(
                            $"[yellow]\u26a0[/] Playwright failed (common in single-file builds): {Markup.Escape(ex.Message)}");
                        AnsiConsole.MarkupLine(
                            "   Install manually: [grey]dotnet tool install --global Microsoft.Playwright.CLI && playwright install chromium[/]");
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
#if FEATURE_COMPLETE
        const string cmd = "lucidrag";
        AnsiConsole.MarkupLine("[grey]LucidRAG defaults to Ollama for LLM. Use --local-llm with setup to download GGUF models instead.[/]");
#else
        const string cmd = "doomsummarizer";
        AnsiConsole.MarkupLine("[grey]DoomSummarizer uses Ollama for LLM synthesis. Install and run: ollama serve[/]");
        AnsiConsole.MarkupLine("[grey]Or set ANTHROPIC_API_KEY / OPENAI_API_KEY for cloud LLM fallback.[/]");
#endif
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Quick start:");
        AnsiConsole.MarkupLine($"  [cyan]{cmd} scroll[/]                          - Fetch and summarize");
        AnsiConsole.MarkupLine($"  [cyan]{cmd} scroll \"AI news\" --vibe snarky[/]   - Topic + vibe");
        AnsiConsole.MarkupLine($"  [cyan]{cmd} scroll --json --nollm[/]            - Fast JSON for tools");
        AnsiConsole.MarkupLine($"  [cyan]{cmd} crawl https://docs.example.com[/]   - Build knowledge base");
        AnsiConsole.MarkupLine($"  [cyan]{cmd} scroll \"query\" --local[/]           - Query stored KB");
        AnsiConsole.MarkupLine($"  [cyan]{cmd} trends[/]                           - Historical trends");
        AnsiConsole.MarkupLine($"  [cyan]{cmd} config --show[/]                    - View configuration");
        AnsiConsole.MarkupLine($"  [cyan]{cmd} scroll --list-templates[/]           - List output templates");

        return 0;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--playwright")]
        [Description("Also install Playwright browsers")]
        public bool Playwright { get; init; }

        [CommandOption("--ner")]
        [Description("Download NER model for entity extraction (~430MB)")]
        public bool Ner { get; init; }

#if FEATURE_LLAMASHARP
        [CommandOption("--local-llm")]
        [Description("Download local GGUF models for LLamaSharp inference (~2.7GB)")]
        public bool LocalLlm { get; init; }

        [CommandOption("--skip-local-llm")]
        [Description("Skip local LLM model download")]
        public bool SkipLocalLlm { get; init; }
#endif
    }
}