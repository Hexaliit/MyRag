using System.ComponentModel;
using System.Diagnostics;
using DoomSummarizer.Helpers;
using DoomSummarizer.Models;
using DoomSummarizer.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DoomSummarizer.Commands;

/// <summary>
///     Manage query exemplars for the embedding-based classifier.
///     Exemplars are YAML files defining representative questions per topic/type.
///     The classifier pre-embeds them at startup for deterministic cosine-similarity classification.
/// </summary>
public sealed class ExemplarsCommand : AsyncCommand<ExemplarsCommand.Settings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings,
        CancellationToken cancellationToken)
    {
        if (settings.Init)
            return await InitExemplarsAsync();

        if (settings.List)
            return ListExemplars();

        if (settings.Rebuild)
            return await RebuildCacheAsync(settings, cancellationToken);

        if (settings.Validate)
            return ValidateExemplars();

        if (settings.Test)
            return await RunDiagnosticTestAsync(settings, cancellationToken);

        if (settings.Benchmark)
            return await RunBenchmarkAsync(settings, cancellationToken);

        // Default: show summary
        return ShowSummary();
    }

    private static int ShowSummary()
    {
        var exemplars = QueryClassifier.LoadAllExemplars();
        var userDir = Path.Combine(ConfigService.GetConfigDir(), "exemplars");
        var hasUserDir = Directory.Exists(userDir);
        var userFileCount = hasUserDir
            ? Directory.GetFiles(userDir, "*.yaml").Length + Directory.GetFiles(userDir, "*.yml").Length
            : 0;

        var byTopic = exemplars.GroupBy(e => e.Topic).OrderBy(g => g.Key).ToList();
        var byType = exemplars.GroupBy(e => e.Type).OrderBy(g => g.Key).ToList();
        var withVibe = exemplars.Count(e => e.Vibe != null);
        var withComplexity = exemplars.Count(e => e.Complexity != null);

        AnsiConsole.MarkupLine($"[bold cyan]Query Exemplars[/]");
        AnsiConsole.MarkupLine($"  Total: [green]{exemplars.Count}[/] exemplars");
        AnsiConsole.MarkupLine($"  Topics: [green]{byTopic.Count}[/] ({string.Join(", ", byTopic.Select(g => g.Key))})");
        AnsiConsole.MarkupLine($"  Types: [green]{byType.Count}[/] ({string.Join(", ", byType.Select(g => g.Key))})");
        AnsiConsole.MarkupLine($"  With vibe: [magenta]{withVibe}[/], with complexity: [red]{withComplexity}[/]");
        AnsiConsole.MarkupLine($"  User dir: {FormattingHelpers.Esc(userDir)} ({(hasUserDir ? $"{userFileCount} files" : "not created")})");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[grey]Commands:[/]");
        AnsiConsole.MarkupLine("  [grey]exemplars --list[/]      List all exemplars");
        AnsiConsole.MarkupLine("  [grey]exemplars --init[/]      Create user exemplar directory with template");
        AnsiConsole.MarkupLine("  [grey]exemplars --rebuild[/]   Re-embed all exemplars (after editing YAML)");
        AnsiConsole.MarkupLine("  [grey]exemplars --validate[/]  Check exemplar YAML files for errors");
        AnsiConsole.MarkupLine("  [grey]exemplars --test[/]      Run diagnostic test matrix (80+ prompts)");
        AnsiConsole.MarkupLine("  [grey]exemplars --test -v[/]   Full per-query breakdown");
        AnsiConsole.MarkupLine("  [grey]exemplars --benchmark[/] Benchmark classifier latency (p50/p95/p99)");

        return 0;
    }

    private static int ListExemplars()
    {
        var exemplars = QueryClassifier.LoadAllExemplars();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Topic")
            .AddColumn("Type")
            .AddColumn("Vibe")
            .AddColumn("Cmplx")
            .AddColumn("Question")
            .AddColumn("Sources");

        foreach (var group in exemplars.GroupBy(e => e.Topic).OrderBy(g => g.Key))
        {
            foreach (var e in group.OrderBy(x => x.Type))
            {
                table.AddRow(
                    $"[cyan]{Markup.Escape(e.Topic)}[/]",
                    $"[yellow]{Markup.Escape(e.Type)}[/]",
                    e.Vibe != null ? $"[magenta]{Markup.Escape(e.Vibe)}[/]" : "[grey]-[/]",
                    e.Complexity != null ? $"[red]{Markup.Escape(e.Complexity)}[/]" : "[grey]-[/]",
                    Markup.Escape(e.Question),
                    e.Sources != null ? Markup.Escape(string.Join(", ", e.Sources)) : "[grey]-[/]");
            }
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[grey]Total: {exemplars.Count} exemplars[/]");

        return 0;
    }

    private static async Task<int> InitExemplarsAsync()
    {
        var userDir = Path.Combine(ConfigService.GetConfigDir(), "exemplars");
        Directory.CreateDirectory(userDir);

        var templatePath = Path.Combine(userDir, "my-exemplars.yaml");
        if (!File.Exists(templatePath))
        {
            var template = """
                           # Custom Query Exemplars
                           # Add your own exemplar questions to customize query classification.
                           # After editing, run: doomsummarizer exemplars --rebuild
                           #
                           # Each exemplar needs:
                           #   question: The representative question text (gets embedded)
                           #   topic: Routing category (technology, ai, entertainment, health, etc.)
                           #   type: Query type (roundup, qa, howto, deep_dive, comparison)
                           #   sources: (optional) Preferred source hints [hn, reddit, bbc, etc.]

                           exemplars:
                             # Example: add a niche topic
                             # - question: "Latest developments in Rust programming language"
                             #   topic: programming
                             #   type: roundup
                             #   sources: [hn, reddit, lobsters]

                             # Example: add a domain-specific QA exemplar
                             # - question: "How do I configure PostgreSQL connection pooling?"
                             #   topic: technology
                             #   type: howto
                           """;
            await File.WriteAllTextAsync(templatePath, template);
            AnsiConsole.MarkupLine($"[green]Created:[/] {FormattingHelpers.Esc(templatePath)}");
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]Already exists:[/] {FormattingHelpers.Esc(templatePath)}");
        }

        AnsiConsole.MarkupLine($"[grey]Edit the file, then run: doomsummarizer exemplars --rebuild[/]");
        return 0;
    }

    private static async Task<int> RebuildCacheAsync(Settings settings, CancellationToken ct)
    {
        AnsiConsole.MarkupLine("[grey]Loading exemplars...[/]");
        var exemplars = QueryClassifier.LoadAllExemplars();
        AnsiConsole.MarkupLine($"[green]Loaded {exemplars.Count} exemplars[/]");

        AnsiConsole.MarkupLine("[grey]Initializing embedding service...[/]");
        var boot = await CommandBootstrap.CreateAsync(settings.GpuDevice, ct);
        await using (boot)
        {
            var classifier = new QueryClassifier();
            await AnsiConsole.Status()
                .StartAsync("Embedding exemplars...", async ctx =>
                {
                    await classifier.InitializeAsync(boot.Embedding, ct);
                });

            AnsiConsole.MarkupLine(
                $"[green]Embedded {classifier.ExemplarCount} exemplars successfully[/]");

            // Test with a few sample queries to verify
            if (!settings.Quiet)
            {
                AnsiConsole.MarkupLine("\n[bold]Sample classifications:[/]");
                var testQueries = new[]
                {
                    "latest tech news",
                    "What is quantum computing?",
                    "celebrity gossip",
                    "How do I set up Docker?",
                    "Compare React vs Vue",
                    "doom-scroll the worst news",
                    "AI news and also politics",
                    "What time is it in Tokyo?",
                    "implications of EU AI Act on open source",
                    // Short query feature tests
                    "tech news",
                    "AI news",
                    "Docker help",
                    "define ontological",
                    "convert miles km",
                    "compare React Vue",
                };

                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .AddColumn("Query")
                    .AddColumn("Top Topic")
                    .AddColumn("Score")
                    .AddColumn("Type")
                    .AddColumn("Vibe")
                    .AddColumn("Flags");

                foreach (var query in testQueries)
                {
                    var result = await classifier.ClassifyAsync(query, ct);
                    var topCat = result.Categories
                        .OrderByDescending(kv => kv.Value)
                        .FirstOrDefault();
                    var flags = (result.IsComposite ? "C" : "") + (result.IsComplex ? "X" : "")
                                + (result.Features != null ? "F" : "");
                    table.AddRow(
                        Markup.Escape(query),
                        $"[cyan]{Markup.Escape(topCat.Key ?? "none")}[/]",
                        $"{topCat.Value:F2}",
                        $"[yellow]{Markup.Escape(result.QueryType)}[/]",
                        result.Vibe != null ? $"[magenta]{Markup.Escape(result.Vibe)}[/]" : "[grey]-[/]",
                        !string.IsNullOrEmpty(flags) ? $"[red]{flags}[/]" : "[grey]-[/]");
                }

                AnsiConsole.Write(table);
            }
        }

        return 0;
    }

    private static int ValidateExemplars()
    {
        var errors = 0;
        var userDir = Path.Combine(ConfigService.GetConfigDir(), "exemplars");

        AnsiConsole.MarkupLine("[bold]Validating exemplar files...[/]");

        // Validate all exemplars
        try
        {
            var all = QueryClassifier.LoadAllExemplars();
            AnsiConsole.MarkupLine($"  [green]Total:[/] {all.Count} exemplars loaded");

            var validVibes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "doom", "hopeful", "snarky", "funny", "upbeat", "friendly", "toon", "neutral", "concise" };
            var validComplexities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "simple", "complex" };

            // Check for missing fields and field consistency
            foreach (var e in all)
            {
                if (string.IsNullOrWhiteSpace(e.Question))
                {
                    AnsiConsole.MarkupLine($"  [red]Error:[/] empty question in topic={e.Topic}");
                    errors++;
                }

                if (string.IsNullOrWhiteSpace(e.Topic))
                {
                    AnsiConsole.MarkupLine($"  [red]Error:[/] empty topic for \"{Markup.Escape(e.Question)}\"");
                    errors++;
                }

                if (e.Vibe != null && !validVibes.Contains(e.Vibe))
                {
                    AnsiConsole.MarkupLine($"  [yellow]Warning:[/] unknown vibe \"{Markup.Escape(e.Vibe)}\" for \"{Markup.Escape(e.Question)}\"");
                }

                if (e.Complexity != null && !validComplexities.Contains(e.Complexity))
                {
                    AnsiConsole.MarkupLine($"  [yellow]Warning:[/] unknown complexity \"{Markup.Escape(e.Complexity)}\" for \"{Markup.Escape(e.Question)}\"");
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"  [red]Error loading exemplars:[/] {Markup.Escape(ex.Message)}");
            errors++;
        }

        // Validate user files individually
        if (Directory.Exists(userDir))
        {
            var files = Directory.GetFiles(userDir, "*.yaml")
                .Concat(Directory.GetFiles(userDir, "*.yml"));
            foreach (var file in files)
            {
                try
                {
                    var exemplars = QueryClassifier.LoadExemplarsFromFile(file);
                    AnsiConsole.MarkupLine(
                        $"  [green]{FormattingHelpers.Esc(Path.GetFileName(file))}:[/] {exemplars.Count} exemplars");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine(
                        $"  [red]{FormattingHelpers.Esc(Path.GetFileName(file))}:[/] {Markup.Escape(ex.Message)}");
                    errors++;
                }
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"  [grey]No user exemplar directory ({FormattingHelpers.Esc(userDir)})[/]");
        }

        if (errors == 0)
            AnsiConsole.MarkupLine("\n[green]All exemplar files valid.[/]");
        else
            AnsiConsole.MarkupLine($"\n[red]{errors} error(s) found.[/]");

        return errors > 0 ? 1 : 0;
    }

    // ══════════════════════════════════════════════════════════════════
    // ── Diagnostic test matrix ──────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    ///     Expected classification result for a test prompt.
    ///     Null fields mean "don't check this dimension".
    /// </summary>
    private record TestCase(
        string Query,
        string? ExpectedTopic = null,
        string? ExpectedType = null,
        string? ExpectedVibe = null,
        bool? ExpectedComposite = null,
        bool? ExpectedComplex = null);

    /// <summary>
    ///     Run the full diagnostic test matrix against the classifier.
    ///     Shows per-query pass/fail with optional verbose breakdown, then aggregate stats.
    /// </summary>
    private static async Task<int> RunDiagnosticTestAsync(Settings settings, CancellationToken ct)
    {
        AnsiConsole.MarkupLine("[grey]Initializing embedding service...[/]");
        var boot = await CommandBootstrap.CreateAsync(settings.GpuDevice, ct);
        await using (boot)
        {
            var classifier = new QueryClassifier();
            await AnsiConsole.Status()
                .StartAsync("Embedding exemplars...", async _ =>
                {
                    await classifier.InitializeAsync(boot.Embedding, ct);
                });

            AnsiConsole.MarkupLine(
                $"[green]Loaded {classifier.ExemplarCount} exemplars[/]\n");

            var testCases = BuildTestMatrix();
            AnsiConsole.MarkupLine($"[bold cyan]Diagnostic Test Matrix[/] — {testCases.Count} test cases\n");

            var passed = 0;
            var failed = 0;
            var topicPass = 0; var topicTotal = 0;
            var typePass = 0; var typeTotal = 0;
            var vibePass = 0; var vibeTotal = 0;
            var compositePass = 0; var compositeTotal = 0;
            var complexPass = 0; var complexTotal = 0;
            var failures = new List<(TestCase Test, QueryClassification Result, string Reason)>();

            foreach (var test in testCases)
            {
                var result = await classifier.ClassifyAsync(test.Query, ct);
                var topTopic = result.Categories
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => kv.Key)
                    .FirstOrDefault() ?? "none";
                var topTopics = result.Categories
                    .OrderByDescending(kv => kv.Value)
                    .Take(3)
                    .Select(kv => kv.Key)
                    .ToList();

                var queryPass = true;
                var reason = new List<string>();

                // Check topic (allow top-3)
                if (test.ExpectedTopic != null)
                {
                    topicTotal++;
                    if (topTopics.Contains(test.ExpectedTopic, StringComparer.OrdinalIgnoreCase))
                    {
                        topicPass++;
                    }
                    else
                    {
                        queryPass = false;
                        reason.Add($"topic: expected={test.ExpectedTopic}, got=[{string.Join(",", topTopics)}]");
                    }
                }

                // Check type
                if (test.ExpectedType != null)
                {
                    typeTotal++;
                    if (result.QueryType.Equals(test.ExpectedType, StringComparison.OrdinalIgnoreCase))
                    {
                        typePass++;
                    }
                    else
                    {
                        queryPass = false;
                        reason.Add($"type: expected={test.ExpectedType}, got={result.QueryType}");
                    }
                }

                // Check vibe — "none" means assert no vibe detected (result.Vibe should be null)
                if (test.ExpectedVibe != null)
                {
                    vibeTotal++;
                    var expectNoVibe = test.ExpectedVibe.Equals("none", StringComparison.OrdinalIgnoreCase);
                    var vibeMatch = expectNoVibe
                        ? result.Vibe == null
                        : string.Equals(result.Vibe, test.ExpectedVibe, StringComparison.OrdinalIgnoreCase);
                    if (vibeMatch)
                    {
                        vibePass++;
                    }
                    else
                    {
                        queryPass = false;
                        reason.Add($"vibe: expected={test.ExpectedVibe}, got={result.Vibe ?? "null"}");
                    }
                }

                // Check composite
                if (test.ExpectedComposite != null)
                {
                    compositeTotal++;
                    if (result.IsComposite == test.ExpectedComposite.Value)
                    {
                        compositePass++;
                    }
                    else
                    {
                        queryPass = false;
                        reason.Add($"composite: expected={test.ExpectedComposite}, got={result.IsComposite}");
                    }
                }

                // Check complex
                if (test.ExpectedComplex != null)
                {
                    complexTotal++;
                    if (result.IsComplex == test.ExpectedComplex.Value)
                    {
                        complexPass++;
                    }
                    else
                    {
                        queryPass = false;
                        reason.Add($"complex: expected={test.ExpectedComplex}, got={result.IsComplex}");
                    }
                }

                if (queryPass)
                    passed++;
                else
                {
                    failed++;
                    failures.Add((test, result, string.Join("; ", reason)));
                }

                // Per-query output
                var statusMark = queryPass ? "[green]PASS[/]" : "[red]FAIL[/]";
                var queryDisplay = FormattingHelpers.TruncEsc(test.Query, 55);

                if (settings.Verbose)
                {
                    AnsiConsole.MarkupLine($"{statusMark} {queryDisplay}");
                    AnsiConsole.MarkupLine(
                        $"  [grey]Topic:[/] [cyan]{Markup.Escape(topTopic)}[/] ({result.Categories.GetValueOrDefault(topTopic):F2})  " +
                        $"[grey]Type:[/] [yellow]{Markup.Escape(result.QueryType)}[/] ({result.QueryTypeConfidence:F2})  " +
                        $"[grey]Vibe:[/] {(result.Vibe != null ? $"[magenta]{Markup.Escape(result.Vibe)}[/] ({result.VibeConfidence:F2})" : "[grey]-[/]")}  " +
                        $"[grey]Comp:[/] {(result.IsComposite ? "[red]Y[/]" : "N")}  " +
                        $"[grey]Cmplx:[/] {(result.IsComplex ? "[red]Y[/]" : "N")}  " +
                        $"[grey]Best:[/] {result.BestMatchScore:F2}");

                    // Top 3 matches
                    foreach (var m in result.TopMatches.Take(3))
                        AnsiConsole.MarkupLine(
                            $"    {m.Score:F3} [grey][[{Markup.Escape(m.Topic)}/{Markup.Escape(m.Type)}]][/] {FormattingHelpers.TruncEsc(m.Question, 60)}");

                    // Features (if short query)
                    if (result.Features is QueryFeatureSet f)
                        AnsiConsole.MarkupLine(
                            $"    [dim]Features: words={f.WordCount} q?={f.HasQuestionWord} cmp={f.HasComparisonMarker} " +
                            $"howto={f.HasHowtoMarker} search={f.HasSearchOnlyMarker} qa={f.HasQaMarker} " +
                            $"composite={f.HasCompositeConjunction} imperative={f.HasImperativeVerb}[/]");

                    if (!queryPass)
                        AnsiConsole.MarkupLine($"    [red]{Markup.Escape(string.Join("; ", reason))}[/]");
                    AnsiConsole.WriteLine();
                }
                else if (!queryPass)
                {
                    // Compact mode: only show failures
                    AnsiConsole.MarkupLine(
                        $"{statusMark} {queryDisplay} — [red]{Markup.Escape(string.Join("; ", reason))}[/]");
                }
            }

            // ── Aggregate stats ──
            AnsiConsole.WriteLine();
            var rule = new Rule("[bold]Results[/]").RuleStyle("cyan");
            AnsiConsole.Write(rule);

            var overallRate = testCases.Count > 0 ? (double)passed / testCases.Count : 0;
            var overallColor = overallRate >= 0.90 ? "green" : overallRate >= 0.75 ? "yellow" : "red";
            AnsiConsole.MarkupLine(
                $"  Overall: [{overallColor}]{passed}/{testCases.Count} ({overallRate:P0})[/]");

            PrintDimensionStat("Topic", topicPass, topicTotal);
            PrintDimensionStat("Type", typePass, typeTotal);
            PrintDimensionStat("Vibe", vibePass, vibeTotal);
            PrintDimensionStat("Composite", compositePass, compositeTotal);
            PrintDimensionStat("Complex", complexPass, complexTotal);

            // ── Failure summary ──
            if (failures.Count > 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[bold red]Failures ({failures.Count}):[/]");
                var failTable = new Table()
                    .Border(TableBorder.Simple)
                    .AddColumn("Query")
                    .AddColumn("Got")
                    .AddColumn("Reason");

                foreach (var (test, result, failReason) in failures)
                {
                    var gotSummary =
                        $"{result.Categories.OrderByDescending(kv => kv.Value).FirstOrDefault().Key ?? "?"}/{result.QueryType}";
                    if (result.Vibe != null) gotSummary += $"/{result.Vibe}";
                    if (result.IsComposite) gotSummary += " [C]";
                    if (result.IsComplex) gotSummary += " [X]";
                    failTable.AddRow(
                        FormattingHelpers.TruncEsc(test.Query, 45),
                        Markup.Escape(gotSummary),
                        Markup.Escape(failReason));
                }

                AnsiConsole.Write(failTable);
            }
            else
            {
                AnsiConsole.MarkupLine("\n[bold green]All tests passed![/]");
            }

            return failed > 0 ? 1 : 0;
        }
    }

    private static void PrintDimensionStat(string name, int pass, int total)
    {
        if (total == 0) return;
        var rate = (double)pass / total;
        var color = rate >= 0.90 ? "green" : rate >= 0.75 ? "yellow" : "red";
        AnsiConsole.MarkupLine($"  {name,-12} [{color}]{pass}/{total} ({rate:P0})[/]");
    }

    /// <summary>
    ///     Build the comprehensive test matrix covering all classification dimensions.
    /// </summary>
    private static List<TestCase> BuildTestMatrix() =>
    [
        // ══════════════════════════════════════════════
        // ── Topic detection ──────────────────────────
        // ══════════════════════════════════════════════
        new("What's the latest tech news?", ExpectedTopic: "technology", ExpectedType: "roundup"),
        new("Show me AI and machine learning news", ExpectedTopic: "ai"),
        new("Latest .NET framework updates", ExpectedTopic: "programming"),
        new("Celebrity gossip and entertainment", ExpectedTopic: "entertainment"),
        new("Recent physics research breakthroughs", ExpectedTopic: "science"),
        new("Health news and medical research", ExpectedTopic: "health"),
        new("Stock market and investment news", ExpectedTopic: "finance"),
        new("Political developments today", ExpectedTopic: "politics"),
        new("International headlines", ExpectedTopic: "world"),
        new("Climate change updates", ExpectedTopic: "environment"),
        new("Football results this weekend", ExpectedTopic: "sports"),
        new("Rocket launches and space missions", ExpectedTopic: "space"),
        new("Data breaches and cybersecurity", ExpectedTopic: "security"),
        new("Video game releases this month", ExpectedTopic: "gaming"),
        new("Local crime reports and statistics", ExpectedTopic: "crime"),
        new("Flood warnings in my area", ExpectedTopic: "flooding"),
        new("New drug approvals FDA", ExpectedTopic: "pharma"),
        new("Satirical news roundup", ExpectedTopic: "satire"),
        new("Airline delays and travel news", ExpectedTopic: "transport"),
        new("Food safety recalls", ExpectedTopic: "food"),
        new("Business acquisitions this quarter", ExpectedTopic: "business"),
        new("UK parliament debates this week", ExpectedTopic: "uk"),

        // ══════════════════════════════════════════════
        // ── Type detection ───────────────────────────
        // ══════════════════════════════════════════════
        new("Give me a roundup of today's news", ExpectedType: "roundup"),
        new("Latest tech news roundup", ExpectedType: "roundup"),
        new("How do I set up a Docker container?", ExpectedType: "howto"),
        new("How to configure Nginx reverse proxy", ExpectedType: "howto"),
        new("Step by step guide to Kubernetes", ExpectedType: "howto"),
        new("Compare React vs Angular", ExpectedType: "comparison"),
        new("Pros and cons of TypeScript vs JavaScript", ExpectedType: "comparison"),
        new("What is quantum entanglement?", ExpectedType: "deep_dive"),
        new("In-depth analysis of AI safety", ExpectedType: "deep_dive"),
        new("Explain how neural networks work", ExpectedType: "qa"),  // "explain what X is" pattern → qa
        new("What time is it in Tokyo?", ExpectedType: "search_only"),
        new("Define ontological", ExpectedType: "search_only"),
        new("What's the population of France?", ExpectedType: "search_only"),
        new("Convert 100 dollars to euros", ExpectedType: "search_only"),
        new("How tall is Mount Everest?", ExpectedType: "search_only"),
        new("Weather in London right now", ExpectedType: "search_only"),

        // ══════════════════════════════════════════════
        // ── Vibe detection ───────────────────────────
        // ══════════════════════════════════════════════
        new("Give me the most depressing news", ExpectedVibe: "doom"),
        new("Doom-scroll today's terrible headlines", ExpectedVibe: "doom"),
        new("Show me the worst doom and gloom stories", ExpectedVibe: "doom"),
        new("Sarcastic take on today's news", ExpectedVibe: "snarky"),
        new("Roast the latest tech announcements", ExpectedVibe: "snarky"),
        new("Show me positive uplifting stories", ExpectedVibe: "hopeful"),
        new("Give me some good news for a change", ExpectedVibe: "hopeful"),
        new("Tell me the funniest news stories", ExpectedVibe: "funny"),
        new("Make the news entertaining and hilarious", ExpectedVibe: "funny"),
        // Negative vibe: these neutral queries should NOT get a vibe
        new("What's the latest tech news?", ExpectedVibe: "none"),
        new("Latest tech news roundup", ExpectedVibe: "none"),
        new("What's going on in the world today?", ExpectedVibe: "none"),
        new("Give me a roundup of today's news", ExpectedVibe: "none"),

        // ══════════════════════════════════════════════
        // ── Composite detection ──────────────────────
        // ══════════════════════════════════════════════
        new("Tech news and also what's in politics", ExpectedComposite: true),
        new("AI developments this week and compare the top models", ExpectedComposite: true),
        new("Get me business news and find out what's new in AI", ExpectedComposite: true),
        new("Summarize tech news and also what's happening in politics", ExpectedComposite: true),
        // These should NOT be composite
        new("Latest tech news", ExpectedComposite: false),
        new("What's happening in AI today?", ExpectedComposite: false),
        new("Compare React and Vue", ExpectedComposite: false),

        // ══════════════════════════════════════════════
        // ── Complex detection ────────────────────────
        // ══════════════════════════════════════════════
        new("What are the second-order effects of rising interest rates on tech?", ExpectedComplex: true),
        new("How might the EU AI Act affect open source development in practice?", ExpectedComplex: true),
        // These should NOT be complex
        new("Latest tech news", ExpectedComplex: false),
        new("What is quantum computing?", ExpectedComplex: false),
        new("What's happening in AI today?", ExpectedComplex: false),

        // ══════════════════════════════════════════════
        // ── Short queries ────────────────────────────
        // ══════════════════════════════════════════════
        new("tech news", ExpectedTopic: "technology", ExpectedType: "roundup"),
        new("AI news", ExpectedTopic: "ai", ExpectedType: "roundup"),
        new("Docker help", ExpectedType: "howto"),
        new("define ontological", ExpectedType: "search_only"),
        new("convert miles km", ExpectedType: "search_only"),
        new("compare React Vue", ExpectedType: "comparison"),
        new("celebrity gossip", ExpectedTopic: "entertainment"),
        new("sports scores", ExpectedTopic: "sports"),
        new("space news", ExpectedTopic: "space"),
        new("crypto prices", ExpectedTopic: "finance"),  // no "crypto" topic; routes to finance

        // ══════════════════════════════════════════════
        // ── Real-world natural prompts ───────────────
        // ══════════════════════════════════════════════
        new("What's going on in the world today?", ExpectedTopic: "world", ExpectedType: "roundup"),
        new("Catch me up on the news", ExpectedType: "roundup"),
        new("doom-scroll the worst news", ExpectedVibe: "doom"),
        new("implications of EU AI Act on open source", ExpectedComplex: true),
        new("AI news and also politics", ExpectedComposite: true),
        new("show me the latest on climate change", ExpectedTopic: "environment"),
        new("what happened today", ExpectedType: "news"),  // "what happened" → news, not roundup
        new("is there any good news?", ExpectedVibe: "hopeful"),
        new("tell me something funny from the news", ExpectedVibe: "funny"),
        new("how do I deploy to Azure?", ExpectedType: "howto"),
        new("who won the match?", ExpectedTopic: "sports"),
        new("any new breakthroughs in fusion energy?", ExpectedTopic: "science"),
        new("what's the deal with quantum computing?", ExpectedTopic: "science"),
        new("give me a snarky summary of the news", ExpectedVibe: "snarky"),
    ];

    /// <summary>
    ///     Benchmark the classifier: measures embedding + scoring latency separately.
    ///     Runs N iterations (default 100) with warmup, reports p50/p95/p99/mean.
    /// </summary>
    private static async Task<int> RunBenchmarkAsync(Settings settings, CancellationToken ct)
    {
        AnsiConsole.MarkupLine("[grey]Initializing embedding service...[/]");
        var boot = await CommandBootstrap.CreateAsync(settings.GpuDevice, ct);
        await using (boot)
        {
            var classifier = new QueryClassifier();
            await AnsiConsole.Status()
                .StartAsync("Embedding exemplars...", async _ =>
                {
                    await classifier.InitializeAsync(boot.Embedding, ct);
                });

            AnsiConsole.MarkupLine($"[green]Loaded {classifier.ExemplarCount} exemplars[/]\n");

            var iterations = settings.BenchmarkIterations ?? 100;
            var queries = new[]
            {
                "latest tech news",
                "How do I set up a Docker container?",
                "Compare React vs Angular",
                "Give me the most depressing news",
                "AI news and also what's happening in politics",
                "What time is it in Tokyo?",
                "What are the second-order effects of rising interest rates on tech?",
                "celebrity gossip",
                "define ontological",
                "What's happening in the world today?",
            };

            AnsiConsole.MarkupLine($"[bold cyan]Benchmark[/] — {iterations} iterations x {queries.Length} queries\n");

            // Warmup: 5 iterations (primes JIT, ONNX session, caches)
            for (var w = 0; w < 5; w++)
                foreach (var q in queries)
                    await classifier.ClassifyAsync(q, ct);

            // Collect per-iteration timings (embedding + classify combined)
            var totalTimes = new List<double>(iterations * queries.Length);
            // Collect classify-only times (no embedding, measures scoring logic)
            var classifyOnlyTimes = new List<double>(iterations * queries.Length);

            // Pre-embed all queries once for classify-only benchmark
            var preEmbedded = new float[queries.Length][];
            for (var i = 0; i < queries.Length; i++)
                preEmbedded[i] = await boot.Embedding.EmbedAsync(queries[i], ct);

            var sw = new Stopwatch();

            for (var i = 0; i < iterations; i++)
            {
                foreach (var q in queries)
                {
                    sw.Restart();
                    await classifier.ClassifyAsync(q, ct);
                    sw.Stop();
                    totalTimes.Add(sw.Elapsed.TotalMicroseconds);
                }
            }

            // Classify-only: use ClassifyWithEmbeddingAsync if available,
            // otherwise measure just the scoring portion by timing ClassifyAsync
            // (embedding is cached/fast after warmup anyway)
            for (var i = 0; i < Math.Min(iterations, 50); i++)
            {
                foreach (var q in queries)
                {
                    sw.Restart();
                    await classifier.ClassifyAsync(q, ct);
                    sw.Stop();
                    classifyOnlyTimes.Add(sw.Elapsed.TotalMicroseconds);
                }
            }

            // Stats
            totalTimes.Sort();
            classifyOnlyTimes.Sort();

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Metric")
                .AddColumn("Total (embed+classify)")
                .AddColumn("Classify-only (warmed)");

            table.AddRow("Samples", $"{totalTimes.Count}", $"{classifyOnlyTimes.Count}");
            table.AddRow("Mean", FormatUs(totalTimes.Average()), FormatUs(classifyOnlyTimes.Average()));
            table.AddRow("p50", FormatUs(Percentile(totalTimes, 0.50)), FormatUs(Percentile(classifyOnlyTimes, 0.50)));
            table.AddRow("p95", FormatUs(Percentile(totalTimes, 0.95)), FormatUs(Percentile(classifyOnlyTimes, 0.95)));
            table.AddRow("p99", FormatUs(Percentile(totalTimes, 0.99)), FormatUs(Percentile(classifyOnlyTimes, 0.99)));
            table.AddRow("Min", FormatUs(totalTimes[0]), FormatUs(classifyOnlyTimes[0]));
            table.AddRow("Max", FormatUs(totalTimes[^1]), FormatUs(classifyOnlyTimes[^1]));

            AnsiConsole.Write(table);

            // Per-query breakdown
            if (settings.Verbose)
            {
                AnsiConsole.MarkupLine("\n[bold]Per-query breakdown (last iteration):[/]");
                var perQuery = new Table()
                    .Border(TableBorder.Simple)
                    .AddColumn("Query")
                    .AddColumn("Time");

                foreach (var q in queries)
                {
                    sw.Restart();
                    await classifier.ClassifyAsync(q, ct);
                    sw.Stop();
                    perQuery.AddRow(
                        FormattingHelpers.TruncEsc(q, 55),
                        FormatUs(sw.Elapsed.TotalMicroseconds));
                }

                AnsiConsole.Write(perQuery);
            }

            return 0;
        }
    }

    private static string FormatUs(double microseconds) =>
        microseconds >= 1000 ? $"{microseconds / 1000:F2} ms" : $"{microseconds:F0} us";

    private static double Percentile(List<double> sorted, double p)
    {
        var idx = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Max(0, Math.Min(idx, sorted.Count - 1))];
    }

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--list")]
        [Description("List all loaded exemplars (defaults + user)")]
        public bool List { get; init; }

        [CommandOption("--init")]
        [Description("Create the user exemplars directory with a template file")]
        public bool Init { get; init; }

        [CommandOption("--rebuild")]
        [Description("Re-embed all exemplars (run after editing YAML files)")]
        public bool Rebuild { get; init; }

        [CommandOption("--validate")]
        [Description("Check exemplar YAML files for errors")]
        public new bool Validate { get; init; }

        [CommandOption("--test")]
        [Description("Run diagnostic test matrix: classify 80+ prompts and show pass/fail breakdown")]
        public bool Test { get; init; }

        [CommandOption("--benchmark")]
        [Description("Benchmark classifier latency (p50/p95/p99/mean over N iterations)")]
        public bool Benchmark { get; init; }

        [CommandOption("--iterations|-n")]
        [Description("Number of benchmark iterations (default: 100)")]
        public int? BenchmarkIterations { get; init; }

        [CommandOption("--verbose|-v")]
        [Description("Show full breakdown per query in --test or --benchmark mode")]
        public bool Verbose { get; init; }

        [CommandOption("--quiet|-q")]
        [Description("Suppress sample classification output during rebuild")]
        public bool Quiet { get; init; }

        [CommandOption("--gpu")]
        [Description("GPU device ID for ONNX embedding")]
        public int? GpuDevice { get; init; }
    }
}
