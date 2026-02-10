using System.ComponentModel;
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

        [CommandOption("--quiet|-q")]
        [Description("Suppress sample classification output during rebuild")]
        public bool Quiet { get; init; }

        [CommandOption("--gpu")]
        [Description("GPU device ID for ONNX embedding")]
        public int? GpuDevice { get; init; }
    }
}
