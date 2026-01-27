using System.ComponentModel;
using DoomSummarizer.Models;
using DoomSummarizer.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DoomSummarizer.Commands;

/// <summary>
/// Benchmarks Ollama models for synthesis and sentinel tasks.
/// Measures tokens/second, total latency, and output quality.
/// </summary>
public sealed class BenchmarkCommand : AsyncCommand<BenchmarkCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[models]")]
        [Description("Comma-separated model names to test (e.g. qwen3:4b,gemma3:4b,phi4-mini). If omitted, auto-detects available small models.")]
        public string? Models { get; init; }

        [CommandOption("--role <ROLE>")]
        [Description("Role to benchmark: synthesis, sentinel, or both (default: both)")]
        [DefaultValue("both")]
        public string Role { get; init; } = "both";

        [CommandOption("--rounds <N>")]
        [Description("Number of rounds per model (default: 2)")]
        [DefaultValue(2)]
        public int Rounds { get; init; } = 2;

        [CommandOption("--pull")]
        [Description("Auto-pull models that aren't available locally")]
        public bool Pull { get; init; }
    }

    // Standardized test prompt for synthesis (digest generation)
    private const string SynthesisPrompt = """
        Create a doom-scroll digest in markdown format.

        TODAY'S DATE: January 25, 2026
        VIBE: snarky
        VIBE INSTRUCTION: Be witty and sardonic, like a tech journalist who's seen it all.

        ITEMS (use ONLY these - do not add any other content):

        ## AI
        - [OpenAI Announces GPT-5 Architecture Changes](https://example.com/gpt5): Major architectural shift moves away from pure transformers. Hybrid approach combines structured reasoning with neural generation. (relevance: 0.95, sentiment: 0.3)
        - [EU AI Act Enforcement Begins](https://example.com/eu-ai): First penalties issued under the EU's AI regulation framework. Three companies fined for non-compliance. (relevance: 0.82, sentiment: -0.4)

        ## SECURITY
        - [Critical SSH Vulnerability Affects All Linux Distros](https://example.com/ssh-vuln): CVE-2026-1234 allows remote code execution via crafted SSH packets. Patches available for major distributions. (relevance: 0.91, sentiment: -0.8)

        ## TOOLS
        - [Rust 2.0 Preview Released](https://example.com/rust2): Major language evolution with backwards-compatible async improvements and improved error handling. Community reception mixed. (relevance: 0.78, sentiment: 0.2)

        STRICT RULES:
        - ONLY summarize items listed above
        - Use ONLY the URLs provided
        - Use ONLY January 25, 2026 as the date

        Create: 1) Brief overview (2-3 sentences), 2) Organize by topic, 3) Include URLs, 4) Brief "what to watch".
        Use markdown formatting. Match the snarky vibe.
        """;

    // Standardized test prompt for sentinel (per-article JSON analysis)
    private const string SentinelPrompt = """
        Analyze this content and respond with JSON only:

        TITLE: Critical SSH Vulnerability Affects All Linux Distros
        CONTENT: A newly discovered vulnerability in OpenSSH (CVE-2026-1234) allows remote attackers to execute arbitrary code on affected systems. The flaw exists in the key exchange mechanism and affects all versions from 8.0 to 9.6. Major Linux distributions including Ubuntu, Fedora, and Debian have released emergency patches. Security researchers at Google Project Zero discovered the vulnerability during routine auditing. The CVSS score is 9.8, making it critical severity. System administrators are urged to update immediately, especially for internet-facing servers. The vulnerability was responsibly disclosed 90 days ago, and there is no evidence of exploitation in the wild yet.

        Respond with this exact JSON structure:
        {
            "summary": "2-3 sentence summary",
            "topic": "single word topic category",
            "sentiment": 0.0
        }

        For sentiment, use: -1.0 (very negative) to 1.0 (very positive), 0.0 is neutral.

        Vibe instruction: Be factual and precise.
        """;

    // Known 4B-class model patterns to auto-detect
    private static readonly string[] SmallModelPatterns =
    [
        "qwen3:4b", "qwen2.5:3b", "qwen2.5:7b",
        "gemma3:4b", "gemma2:2b",
        "phi4-mini", "phi3:mini", "phi3.5:3.8b",
        "llama3.2:3b", "llama3.2:1b",
        "mistral:7b", "deepseek-r1:7b",
        "qwen3:8b" // include current default for comparison
    ];

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.Write(new FigletText("Benchmark").Color(Color.Yellow));
        AnsiConsole.MarkupLine("[grey]Model speed & quality comparison[/]");
        AnsiConsole.WriteLine();

        var config = await ConfigService.LoadAsync();
        var ollama = new OllamaService(config.Ollama);

        if (!await ollama.IsAvailableAsync())
        {
            AnsiConsole.MarkupLine("[red]Ollama not available.[/] Start it with: [grey]ollama serve[/]");
            return 1;
        }

        // Determine which models to test
        var available = await ollama.GetAvailableModelsAsync();
        AnsiConsole.MarkupLine($"[grey]Available models: {string.Join(", ", available)}[/]");

        List<string> modelsToTest;
        if (!string.IsNullOrEmpty(settings.Models))
        {
            modelsToTest = settings.Models.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        else
        {
            // Auto-detect: find available models that match small/4B-class patterns
            modelsToTest = [];
            foreach (var pattern in SmallModelPatterns)
            {
                var baseName = pattern.Split(':')[0];
                var match = available.FirstOrDefault(a =>
                    a.StartsWith(baseName, StringComparison.OrdinalIgnoreCase));
                if (match != null && !modelsToTest.Contains(match))
                    modelsToTest.Add(match);
            }

            // Always include current configured models for comparison
            if (!modelsToTest.Any(m => m.StartsWith(config.Ollama.Model.Split(':')[0], StringComparison.OrdinalIgnoreCase)))
            {
                var currentMatch = available.FirstOrDefault(a =>
                    a.StartsWith(config.Ollama.Model.Split(':')[0], StringComparison.OrdinalIgnoreCase));
                if (currentMatch != null)
                    modelsToTest.Insert(0, currentMatch);
            }

            if (modelsToTest.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No candidate models found.[/] Specify models: [grey]benchmark qwen3:4b,gemma3:4b[/]");
                return 1;
            }
        }

        // Pull models if requested
        if (settings.Pull)
        {
            foreach (var model in modelsToTest.ToList())
            {
                var baseName = model.Split(':')[0];
                if (!available.Any(a => a.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)))
                {
                    AnsiConsole.MarkupLine($"[yellow]Pulling {model}...[/]");
                    var pullResult = await PullModelAsync(config.Ollama.BaseUrl, model, cancellationToken);
                    if (!pullResult)
                    {
                        AnsiConsole.MarkupLine($"[red]Failed to pull {model}, skipping[/]");
                        modelsToTest.Remove(model);
                    }
                }
            }
        }
        else
        {
            // Filter to only locally available models
            var filtered = new List<string>();
            foreach (var model in modelsToTest)
            {
                var baseName = model.Split(':')[0];
                if (available.Any(a => a.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)))
                    filtered.Add(model);
                else
                    AnsiConsole.MarkupLine($"[grey]Skipping {model} (not pulled). Use --pull to auto-download.[/]");
            }
            modelsToTest = filtered;
        }

        if (modelsToTest.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No models available to test.[/]");
            return 1;
        }

        // Normalize model names to what Ollama actually has
        modelsToTest = NormalizeModelNames(modelsToTest, available);

        AnsiConsole.MarkupLine($"\n[bold]Testing {modelsToTest.Count} model(s):[/] {string.Join(", ", modelsToTest)}");
        AnsiConsole.MarkupLine($"[grey]Rounds: {settings.Rounds} | Role: {settings.Role}[/]\n");

        var benchSynthesis = settings.Role is "both" or "synthesis";
        var benchSentinel = settings.Role is "both" or "sentinel";

        var results = new List<ModelBenchmarkSummary>();

        await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var totalSteps = modelsToTest.Count * settings.Rounds * (benchSynthesis && benchSentinel ? 2 : 1);
                var task = ctx.AddTask("Benchmarking models", maxValue: totalSteps);

                foreach (var model in modelsToTest)
                {
                    var synthResults = new List<BenchmarkResult>();
                    var sentinelResults = new List<BenchmarkResult>();

                    for (var round = 0; round < settings.Rounds; round++)
                    {
                        if (benchSynthesis)
                        {
                            task.Description = $"[yellow]{model}[/] synthesis (round {round + 1})";
                            try
                            {
                                var result = await ollama.GenerateWithTimingAsync(
                                    model, SynthesisPrompt, null, 0.6, cancellationToken);
                                synthResults.Add(result);
                            }
                            catch (Exception ex)
                            {
                                AnsiConsole.MarkupLine($"[red]  {model} synthesis failed: {Markup.Escape(ex.Message)}[/]");
                            }
                            task.Increment(1);
                        }

                        if (benchSentinel)
                        {
                            task.Description = $"[yellow]{model}[/] sentinel (round {round + 1})";
                            try
                            {
                                var result = await ollama.GenerateWithTimingAsync(
                                    model, SentinelPrompt, null, 0.3, cancellationToken);
                                sentinelResults.Add(result);
                            }
                            catch (Exception ex)
                            {
                                AnsiConsole.MarkupLine($"[red]  {model} sentinel failed: {Markup.Escape(ex.Message)}[/]");
                            }
                            task.Increment(1);
                        }
                    }

                    var summary = new ModelBenchmarkSummary
                    {
                        Model = model,
                        IsCurrentSynthesis = model.Equals(config.Ollama.Model, StringComparison.OrdinalIgnoreCase) ||
                                             model.StartsWith(config.Ollama.Model + ":", StringComparison.OrdinalIgnoreCase),
                        IsCurrentSentinel = model.Equals(config.Ollama.SentinelModel, StringComparison.OrdinalIgnoreCase) ||
                                            model.StartsWith(config.Ollama.SentinelModel + ":", StringComparison.OrdinalIgnoreCase)
                    };

                    if (synthResults.Count > 0)
                    {
                        summary.SynthAvgTokSec = synthResults.Average(r => r.TokensPerSecond);
                        summary.SynthAvgTotalMs = synthResults.Average(r => r.TotalDurationMs);
                        summary.SynthAvgTokens = synthResults.Average(r => r.TokensGenerated);
                        summary.SynthSampleResponse = synthResults[0].Response;
                        summary.SynthHasMarkdown = synthResults.All(r => r.Response.Contains('#'));
                        summary.SynthHasUrls = synthResults.All(r => r.Response.Contains("example.com"));
                    }

                    if (sentinelResults.Count > 0)
                    {
                        summary.SentAvgTokSec = sentinelResults.Average(r => r.TokensPerSecond);
                        summary.SentAvgTotalMs = sentinelResults.Average(r => r.TotalDurationMs);
                        summary.SentAvgTokens = sentinelResults.Average(r => r.TokensGenerated);
                        summary.SentSampleResponse = sentinelResults[0].Response;
                        // Check if sentinel output is valid JSON
                        summary.SentValidJson = sentinelResults.Count(r =>
                        {
                            var resp = r.Response;
                            var start = resp.IndexOf('{');
                            var end = resp.LastIndexOf('}');
                            if (start < 0 || end <= start) return false;
                            try
                            {
                                System.Text.Json.JsonDocument.Parse(resp[start..(end + 1)]);
                                return true;
                            }
                            catch { return false; }
                        });
                        summary.SentTotalRounds = sentinelResults.Count;
                    }

                    results.Add(summary);
                }

                task.Description = "[green]Done[/]";
            });

        // Display results
        AnsiConsole.WriteLine();
        DisplayResults(results, benchSynthesis, benchSentinel, config);

        return 0;
    }

    private static void DisplayResults(List<ModelBenchmarkSummary> results, bool showSynth, bool showSent, DoomConfig config)
    {
        if (showSynth)
        {
            AnsiConsole.Write(new Rule("[bold yellow]Synthesis Results[/]").LeftJustified());
            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Model")
                .AddColumn(new TableColumn("tok/s").RightAligned())
                .AddColumn(new TableColumn("Total (s)").RightAligned())
                .AddColumn(new TableColumn("Tokens").RightAligned())
                .AddColumn("Markdown?")
                .AddColumn("URLs?")
                .AddColumn("Current?");

            var sorted = results.Where(r => r.SynthAvgTokSec > 0).OrderByDescending(r => r.SynthAvgTokSec).ToList();
            var fastest = sorted.FirstOrDefault();

            foreach (var r in sorted)
            {
                var isFastest = r == fastest;
                var tokStyle = isFastest ? "[bold green]" : "[white]";
                var currentTag = r.IsCurrentSynthesis ? "[cyan]\u2605[/]" : "";

                table.AddRow(
                    $"{(isFastest ? "[bold green]" : "")}{Markup.Escape(r.Model)}{(isFastest ? "[/]" : "")}",
                    $"{tokStyle}{r.SynthAvgTokSec:F1}[/]",
                    $"{r.SynthAvgTotalMs / 1000:F1}",
                    $"{r.SynthAvgTokens:F0}",
                    r.SynthHasMarkdown ? "[green]Yes[/]" : "[red]No[/]",
                    r.SynthHasUrls ? "[green]Yes[/]" : "[red]No[/]",
                    currentTag);
            }

            AnsiConsole.Write(table);

            // Show speed comparison
            if (fastest != null && sorted.Count > 1)
            {
                var current = sorted.FirstOrDefault(r => r.IsCurrentSynthesis);
                if (current != null && current != fastest)
                {
                    var speedup = fastest.SynthAvgTokSec / current.SynthAvgTokSec;
                    AnsiConsole.MarkupLine($"\n[green]\u2605 {Markup.Escape(fastest.Model)}[/] is [bold]{speedup:F1}x faster[/] than current [cyan]{Markup.Escape(current.Model)}[/] for synthesis");
                }
            }
        }

        if (showSent)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[bold yellow]Sentinel Results[/]").LeftJustified());
            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Model")
                .AddColumn(new TableColumn("tok/s").RightAligned())
                .AddColumn(new TableColumn("Total (s)").RightAligned())
                .AddColumn(new TableColumn("Tokens").RightAligned())
                .AddColumn("Valid JSON")
                .AddColumn("Current?");

            var sorted = results.Where(r => r.SentAvgTokSec > 0).OrderByDescending(r => r.SentAvgTokSec).ToList();
            var fastest = sorted.FirstOrDefault();

            foreach (var r in sorted)
            {
                var isFastest = r == fastest;
                var tokStyle = isFastest ? "[bold green]" : "[white]";
                var currentTag = r.IsCurrentSentinel ? "[cyan]\u2605[/]" : "";
                var jsonRate = r.SentTotalRounds > 0 ? $"{r.SentValidJson}/{r.SentTotalRounds}" : "N/A";
                var jsonColor = r.SentValidJson == r.SentTotalRounds ? "[green]" : "[yellow]";

                table.AddRow(
                    $"{(isFastest ? "[bold green]" : "")}{Markup.Escape(r.Model)}{(isFastest ? "[/]" : "")}",
                    $"{tokStyle}{r.SentAvgTokSec:F1}[/]",
                    $"{r.SentAvgTotalMs / 1000:F1}",
                    $"{r.SentAvgTokens:F0}",
                    $"{jsonColor}{jsonRate}[/]",
                    currentTag);
            }

            AnsiConsole.Write(table);

            if (fastest != null && sorted.Count > 1)
            {
                var current = sorted.FirstOrDefault(r => r.IsCurrentSentinel);
                if (current != null && current != fastest)
                {
                    var speedup = fastest.SentAvgTokSec / current.SentAvgTokSec;
                    AnsiConsole.MarkupLine($"\n[green]\u2605 {Markup.Escape(fastest.Model)}[/] is [bold]{speedup:F1}x faster[/] than current [cyan]{Markup.Escape(current.Model)}[/] for sentinel");
                }
            }
        }

        // Recommendations
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold]Recommendations[/]").LeftJustified());

        if (showSynth)
        {
            var synthCandidates = results
                .Where(r => r.SynthAvgTokSec > 0 && r.SynthHasMarkdown && r.SynthHasUrls)
                .OrderByDescending(r => r.SynthAvgTokSec)
                .ToList();

            if (synthCandidates.Count > 0)
            {
                var best = synthCandidates[0];
                var tag = best.IsCurrentSynthesis ? " (already configured)" : "";
                AnsiConsole.MarkupLine($"  [bold]Synthesis:[/] [green]{Markup.Escape(best.Model)}[/] — {best.SynthAvgTokSec:F1} tok/s, valid markdown+URLs{tag}");
                if (!best.IsCurrentSynthesis)
                {
                    AnsiConsole.MarkupLine($"    To switch: edit [grey]~/.doomsummarizer/config.json[/] → set [cyan]ollama.model[/] = \"{best.Model.Split(':')[0]}:{best.Model.Split(':').ElementAtOrDefault(1) ?? "latest"}\"");
                }
            }
            else
            {
                AnsiConsole.MarkupLine("  [yellow]Synthesis: No models produced valid markdown+URLs. Stick with current config.[/]");
            }
        }

        if (showSent)
        {
            var sentCandidates = results
                .Where(r => r.SentAvgTokSec > 0 && r.SentValidJson == r.SentTotalRounds && r.SentTotalRounds > 0)
                .OrderByDescending(r => r.SentAvgTokSec)
                .ToList();

            if (sentCandidates.Count > 0)
            {
                var best = sentCandidates[0];
                var tag = best.IsCurrentSentinel ? " (already configured)" : "";
                AnsiConsole.MarkupLine($"  [bold]Sentinel:[/] [green]{Markup.Escape(best.Model)}[/] — {best.SentAvgTokSec:F1} tok/s, 100% valid JSON{tag}");
                if (!best.IsCurrentSentinel)
                {
                    AnsiConsole.MarkupLine($"    To switch: edit [grey]~/.doomsummarizer/config.json[/] → set [cyan]ollama.sentinelModel[/] = \"{best.Model.Split(':')[0]}:{best.Model.Split(':').ElementAtOrDefault(1) ?? "latest"}\"");
                }
            }
            else
            {
                AnsiConsole.MarkupLine("  [yellow]Sentinel: No models achieved 100% valid JSON. Stick with current config.[/]");
            }
        }

        AnsiConsole.WriteLine();
    }

    private static List<string> NormalizeModelNames(List<string> requested, List<string> available)
    {
        var normalized = new List<string>();
        foreach (var model in requested)
        {
            // Try exact match first
            var exact = available.FirstOrDefault(a =>
                a.Equals(model, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                normalized.Add(exact);
                continue;
            }

            // Try base name match
            var baseName = model.Split(':')[0];
            var match = available.FirstOrDefault(a =>
                a.StartsWith(baseName + ":", StringComparison.OrdinalIgnoreCase) ||
                a.Equals(baseName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                normalized.Add(match);
            else
                normalized.Add(model); // Use as-is, let Ollama handle it
        }
        return normalized;
    }

    private static async Task<bool> PullModelAsync(string baseUrl, string model, CancellationToken ct)
    {
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(30) };
            var json = $"{{\"name\":\"{model}\"}}";
            var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");

            AnsiConsole.MarkupLine($"[grey]Pulling {model} (this may download several GB)...[/]");
            var response = await client.PostAsync("/api/pull", content, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Pull failed: {Markup.Escape(ex.Message)}[/]");
            return false;
        }
    }

    private class ModelBenchmarkSummary
    {
        public string Model { get; set; } = "";
        public bool IsCurrentSynthesis { get; set; }
        public bool IsCurrentSentinel { get; set; }

        // Synthesis metrics
        public double SynthAvgTokSec { get; set; }
        public double SynthAvgTotalMs { get; set; }
        public double SynthAvgTokens { get; set; }
        public string SynthSampleResponse { get; set; } = "";
        public bool SynthHasMarkdown { get; set; }
        public bool SynthHasUrls { get; set; }

        // Sentinel metrics
        public double SentAvgTokSec { get; set; }
        public double SentAvgTotalMs { get; set; }
        public double SentAvgTokens { get; set; }
        public string SentSampleResponse { get; set; } = "";
        public int SentValidJson { get; set; }
        public int SentTotalRounds { get; set; }
    }
}
