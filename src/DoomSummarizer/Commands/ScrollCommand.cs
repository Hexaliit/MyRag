using System.ComponentModel;
using System.Reflection;
using ConsoleImage.Core;
using ConsoleImage.Player;
using DoomSummarizer.Models;
using DoomSummarizer.Plugins;
using DoomSummarizer.Plugins.Runtime;
using DoomSummarizer.Services;
using DoomSummarizer.Services.LongFormGeneration;
using LucidRAG.Decomposer.Analysis;
using LucidRAG.Decomposer.Integration;
using LucidRAG.Decomposer.Models;
using LucidRAG.Decomposer.Orchestration;
using LucidRAG.Decomposer.Refinement;
using Mostlylucid.DocSummarizer.Content;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.DocSummarizer.Services.Onnx;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DoomSummarizer.Commands;

public sealed partial class ScrollCommand : AsyncCommand<ScrollCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[prompt]")]
        [Description("Natural language prompt (e.g., 'summarize bbc and hacker news') or URL")]
        public string? Prompt { get; init; }

        [CommandOption("-v|--vibe")]
        [Description("Sentiment steering: doom, hopeful, snarky, neutral, or any custom text (e.g., 'excited about space')")]
        [DefaultValue("neutral")]
        public string Vibe { get; init; } = "neutral";

        [CommandOption("-o|--output")]
        [Description("Output file path (.md, .txt, .html, .json) - format auto-detected")]
        public string? Output { get; init; }

        [CommandOption("-t|--template")]
        [Description("Output template: default, console, compact, detailed, file, email, newsletter, slack, json")]
        [DefaultValue("default")]
        public string Template { get; init; } = "default";

        [CommandOption("-s|--source")]
        [Description("Override sources (hn, reddit, search:query, or URL)")]
        public string[]? Sources { get; init; }

        [CommandOption("-l|--limit")]
        [Description("Maximum items to fetch")]
        [DefaultValue(30)]
        public int Limit { get; init; } = 30;

        [CommandOption("-f|--force")]
        [Description("Ignore cache and fetch fresh")]
        public bool Force { get; init; }

        [CommandOption("-q|--quiet")]
        [Description("Minimal output, just the summary")]
        public bool Quiet { get; init; }

        [CommandOption("--no-llm|--nollm")]
        [Description("Skip LLM analysis — still runs embeddings, sentiment, topic inference")]
        public bool NoLlm { get; init; }

        [CommandOption("--json")]
        [Description("Output as JSON (for LLM tool consumption)")]
        public bool Json { get; init; }

        [CommandOption("--entities")]
        [Description("Enable NER entity extraction")]
        public bool Entities { get; init; }

        [CommandOption("--graph")]
        [Description("Enable knowledge graph build and display")]
        public bool Graph { get; init; }

        [CommandOption("--no-links")]
        [Description("Skip one-hop link following")]
        public bool NoLinks { get; init; }

        [CommandOption("--raw")]
        [Description("Show raw fetched content before processing")]
        public bool ShowRaw { get; init; }

        [CommandOption("--images")]
        [Description("Display inline images for important items")]
        public bool ShowImages { get; init; }

        [CommandOption("--local")]
        [Description("Query ONLY the local knowledge base — no fetching, uses previously stored articles")]
        public bool LocalOnly { get; init; }

        [CommandOption("-n|--name")]
        [Description("Query a named knowledge base collection (implies --local). Use 'show' command to list collections.")]
        public string? Name { get; init; }

        [CommandOption("--debug-pipeline|--debug")]
        [Description("Show detailed pipeline diagnostics: RRF component scores, discards, salience breakdown")]
        public bool DebugPipeline { get; init; }

        [CommandOption("--list-templates")]
        [Description("List available output templates")]
        public bool ListTemplates { get; init; }

        [CommandOption("--email")]
        [Description("Send digest via email (configure with email section in config)")]
        public bool SendEmail { get; init; }

        [CommandOption("--email-to")]
        [Description("Override email recipient(s), comma-separated")]
        public string? EmailTo { get; init; }

        [CommandOption("--briefing")]
        [Description("Show evidence briefing panel with themes, entities, and coverage metrics")]
        public bool Briefing { get; init; }

        [CommandOption("--clear-storage")]
        [Description("Delete all cached data (segments, queries, entities) and exit")]
        public bool ClearStorage { get; init; }

        [CommandOption("--backfill-entity-profiles")]
        [Description("Backfill entity profiles for existing KB items (one-time migration) and exit")]
        public bool BackfillEntityProfiles { get; init; }

        [CommandOption("--model")]
        [Description("Override LLM model for generation (e.g., qwen3:8b, llama3.2:8b)")]
        public string? Model { get; init; }

        [CommandOption("--sentinel-model")]
        [Description("Override sentinel LLM model for planning/analysis (default: smaller/faster model)")]
        public string? SentinelModel { get; init; }

        [CommandOption("--parallel")]
        [Description("Enable parallel section generation for long-form articles (faster, less cross-section coherence)")]
        [DefaultValue(true)]
        public bool Parallel { get; init; } = true;

        [CommandOption("--locale")]
        [Description("Locale for date/number parsing (e.g., en-us, en-gb, de-de, fr-fr)")]
        [DefaultValue("en-us")]
        public string Locale { get; init; } = "en-us";

        [CommandOption("--ee|--easter-egg")]
        [Description("Show the DoomSummarizer animation")]
        public bool EasterEgg { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Handle --easter-egg: play the DoomSummarizer animation
        if (settings.EasterEgg)
        {
            await PlayEasterEggAnimationAsync(cancellationToken);
            return 0;
        }

        // Handle --list-templates
        if (settings.ListTemplates)
        {
            var templateService = new TemplateService();
            await templateService.LoadCustomTemplatesAsync(
                Path.Combine(ConfigService.GetConfigDir(), "templates"));

            AnsiConsole.MarkupLine("[bold cyan]Available Templates:[/]");
            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Template")
                .AddColumn("Best For");

            table.AddRow("default", "Standard markdown output");
            table.AddRow("console", "Compact console display");
            table.AddRow("compact", "Minimal bullet list");
            table.AddRow("detailed", "Full details with sentiment");
            table.AddRow("file", "Clean markdown for file export");
            table.AddRow("email", "HTML email with inline styles");
            table.AddRow("newsletter", "Professional newsletter HTML");
            table.AddRow("slack", "Slack-formatted message");
            table.AddRow("json", "Raw JSON for API/automation");
            table.AddRow("image", "Single item with featured image");
            table.AddRow("[bold]blog-article[/]", "[cyan]Multi-section long-form article (auto-detects timeline)[/]");
            table.AddRow("[bold]blog-timeline[/]", "[cyan]Chronological article with timeline structure[/]");
            table.AddRow("[bold]blog-newsletter[/]", "[cyan]Curated newsletter with editorial picks[/]");
            table.AddRow("[bold]blog-newsletter-html[/]", "[cyan]Newsletter as styled HTML email[/]");

            // Show YAML-defined templates
            foreach (var name in templateService.ListDefinitions())
            {
                var def = templateService.GetDefinition(name);
                var desc = def?.Description ?? "Custom YAML template";
                var sections = def?.HasFixedSections == true
                    ? $" ({def.Sections.Count} sections)"
                    : "";
                table.AddRow($"[bold yellow]{Markup.Escape(name)}[/]",
                    $"[yellow]{Markup.Escape(desc)}{sections}[/]");
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine("\n[grey]Custom templates: place .liquid or .yaml files in ~/.doomsummarizer/templates/[/]");
            return 0;
        }

        await using var boot = await CommandBootstrap.CreateAsync(cancellationToken);
        if (settings.DebugPipeline)
            AnsiConsole.MarkupLine($"[grey]Config: {Markup.Escape(ConfigService.LoadedConfigPath ?? "embedded default")}[/]");

        // Handle --clear-storage: wipe all cached data and exit
        if (settings.ClearStorage)
        {
            await boot.Storage.ClearAllAsync();

            // Also clear the DuckDB vector store (knowledge graph, HNSW embeddings)
            var clearVectorDbPath = ConfigService.GetVectorDbPath();
            if (File.Exists(clearVectorDbPath))
            {
                try
                {
                    await using var vs = new DuckDbVectorStore(clearVectorDbPath);
                    await vs.InitializeAsync();
                    await vs.ClearAllAsync();
                    AnsiConsole.MarkupLine("[green]Vector store cleared (HNSW embeddings)[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[yellow]Could not clear vector store: {Markup.Escape(ex.Message)}[/]");
                }

                try
                {
                    await using var es = new DuckDbEntityGraphStore(clearVectorDbPath);
                    await es.InitializeAsync();
                    await es.ClearAllAsync();
                    AnsiConsole.MarkupLine("[green]Entity graph store cleared (entities, relationships, profiles)[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[yellow]Could not clear entity graph store: {Markup.Escape(ex.Message)}[/]");
                }
            }

            AnsiConsole.MarkupLine("[green]All stored data cleared (segments, queries, entities, circuit state, API usage, vectors)[/]");
            return 0;
        }

        // Auto-backfill FTS5 index if empty (one-time migration for existing KB items)
        if (await boot.Storage.IsFtsIndexEmptyAsync())
        {
            await BackfillFtsIndexAsync(boot.Storage, settings.Quiet);
        }

        // Initialize DuckDB vector store and entity graph store if needed
        var vectorDbPath = ConfigService.GetVectorDbPath();
        if (File.Exists(vectorDbPath) || settings.Graph || settings.BackfillEntityProfiles)
            await boot.InitializeEntityStoresAsync();

        // Handle --backfill-entity-profiles: compute entity profiles for existing KB items
        if (settings.BackfillEntityProfiles)
        {
            if (boot.VectorStore == null)
            {
                AnsiConsole.MarkupLine("[yellow]No vector store found. Run with --graph flag first to create the knowledge graph.[/]");
                return 1;
            }

            var entityProfileService = new EntityProfileService(boot.Embedding, boot.EntityStore!);
            var graphService = new KnowledgeGraphService(boot.VectorStore, boot.EntityStore!, entityProfileService);

            AnsiConsole.MarkupLine("[cyan]Backfilling entity profiles for existing KB items...[/]");

            var processed = await AnsiConsole.Status()
                .StartAsync("Computing entity profiles...", async ctx =>
                {
                    var total = 0;
                    var batch = 0;
                    while (true)
                    {
                        var count = await graphService.BackfillEntityProfilesAsync(batchSize: 50, cancellationToken);
                        if (count == 0) break;
                        total += count;
                        batch++;
                        ctx.Status($"Computed {total} entity profiles (batch {batch})...");
                    }
                    return total;
                });

            if (processed > 0)
            {
                AnsiConsole.MarkupLine($"[green]Backfill complete: {processed} entity profiles computed[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]No items needed backfilling (all items already have entity profiles, or no entity mentions exist)[/]");
            }

            return 0;
        }

        // Initialize template service for output rendering
        var outputTemplates = new TemplateService();
        await outputTemplates.LoadCustomTemplatesAsync(Path.Combine(ConfigService.GetConfigDir(), "templates"));

        var ollama = boot.CreateOllama();
        var circuitBreaker = await boot.InitializeCircuitBreakerAsync();
        if (settings.DebugPipeline)
            circuitBreaker.PrintCircuitStatus();
        var llmRouter = await boot.InitializeLlmStackAsync(circuitBreaker, cancellationToken);

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        httpClient.DefaultRequestHeaders.Add("User-Agent", "MostlyLucid-DoomSummarizer/1.0");

        // Status helper: overwrites the previous status line to keep output compact.
        // Only the latest status is visible at any time.
        var hasStatusLine = false;
        void WriteStatus(string markup)
        {
            if (settings.Quiet) return;
            if (hasStatusLine)
                Console.Write("\x1b[1A\x1b[2K"); // Move up one line, clear it
            AnsiConsole.MarkupLine(markup);
            hasStatusLine = true;
        }

        if (!settings.Quiet)
            RenderStartupPanel(boot.Config, ConfigService.LoadedConfigPath, llmRouter, boot.Embedding, boot.ApiKeys!, circuitBreaker, settings.Prompt);

        // NER preprocessing: extract entities from query BEFORE the LLM sentinel
        // This gives us structured search filters, cached segment lookups, and URL dedup
        QueryNerContext? nerContext = null;
        if (!string.IsNullOrEmpty(settings.Prompt))
        {
            nerContext = await QueryPreprocessor.PreprocessAsync(
                settings.Prompt, boot.Embedding, boot.Storage, settings.Locale, cancellationToken);

            if (nerContext.HasEntities)
            {
                var entityStr = string.Join(", ", nerContext.Entities
                    .Select(e => $"{e.Text} ({e.Type})"));
                WriteStatus($"[grey]NER: {Markup.Escape(entityStr)}[/]");

                // Show recognizer signals (dates, numbers, etc.)
                if (nerContext.RecognizerSignals?.HasAnySignals == true)
                {
                    var signals = nerContext.RecognizerSignals;
                    var signalParts = new List<string>();
                    if (signals.DateTimes.Count > 0)
                        signalParts.Add($"dates:[{string.Join(", ", signals.DateTimes.Select(d => d.Text))}]");
                    if (signals.Numbers.Count > 0)
                        signalParts.Add($"nums:[{string.Join(", ", signals.Numbers.Select(n => n.Text))}]");
                    WriteStatus($"[grey]Recognizers: {Markup.Escape(string.Join(" ", signalParts))}[/]");
                }
            }
        }

        // Interpret the prompt if provided
        // Skip sentinel interpretation in --name mode (KB query doesn't need web source detection)
        InterpretedPrompt? interpreted = null;
        var vibe = settings.Vibe;
        var isNamedKbQuery = !string.IsNullOrWhiteSpace(settings.Name);

        if (!string.IsNullOrEmpty(settings.Prompt) && !isNamedKbQuery)
        {
            WriteStatus($"[grey]Interpreting: {Markup.Escape(settings.Prompt)}[/]");

            var interpreter = new PromptInterpreter(ollama, boot.Embedding);
            interpreted = await interpreter.InterpretAsync(settings.Prompt, nerContext);

            // Composite query handling: add subqueries as additional search queries
            // This ensures each part of a composite question gets searched separately
            if (interpreted.SentinelIntent?.HasSubqueries == true)
            {
                foreach (var subquery in interpreted.SentinelIntent.Subqueries!)
                {
                    // Don't add duplicates or near-duplicates
                    if (!interpreted.SearchQueries.Any(sq =>
                        sq.Contains(subquery, StringComparison.OrdinalIgnoreCase) ||
                        subquery.Contains(sq, StringComparison.OrdinalIgnoreCase)))
                    {
                        interpreted.SearchQueries.Add(subquery);
                    }
                }
            }

            // Use interpreted vibe unless explicitly overridden
            if (settings.Vibe == "neutral" && interpreted.Vibe != "neutral")
                vibe = interpreted.Vibe;

            var sourcesStr = string.Join(", ", interpreted.Sources
                .Concat(interpreted.Websites)
                .Concat(interpreted.SearchQueries.Select(q => $"search:{q}")));

            // Show temporal extraction from sentinel (LLM-driven, not regex!)
            var temporalInfo = "";
            if (interpreted.SentinelIntent != null)
            {
                var si = interpreted.SentinelIntent;
                var temporalParts = new List<string>();
                if (si.RequiresFresh) temporalParts.Add("requires_fresh");
                if (!string.IsNullOrEmpty(si.TimeSensitivity) && si.TimeSensitivity != "any")
                    temporalParts.Add($"time={si.TimeSensitivity}");
                if (si.DateRange != null)
                    temporalParts.Add($"range={si.DateRange.Original ?? si.DateRange.Unit}");
                if (temporalParts.Count > 0)
                    temporalInfo = $", temporal=[{string.Join(", ", temporalParts)}]";
            }

            WriteStatus($"[grey]Detected: sources=[[{Markup.Escape(sourcesStr)}]], vibe={vibe}{Markup.Escape(temporalInfo)}[/]");
        }

        // ─── Decomposer: classify, analyze, plan ───
        // Runs AFTER PromptInterpreter, BEFORE cache check.
        // Fast-path: simple queries get concept classification + sentinel enhancement only.
        // Complex: multi-topic, tool-use, comparisons get full decomposition.
        DecompositionResult? decomposition = null;
        DecompositionEnrichment? decompositionEnrichment = null;

        if (!string.IsNullOrEmpty(settings.Prompt))
        {
            try
            {
                var decomposer = new DecompositionPipeline(
                    new ComplexityClassifier(boot.Embedding),
                    new ConceptClassifier(boot.Embedding),
                    new IQueryAnalyzer[]
                    {
                        new ReferenceExtractor(),
                        new StructuralAnalyzer(boot.Embedding),
                        new EntityRelationAnalyzer(boot.Embedding),
                        new TemporalAnalyzer(),
                        new SemanticClusterAnalyzer(boot.Embedding),
                        new ToolUseAnalyzer(boot.Embedding)
                    },
                    new SentinelRefiner(),
                    boot.Embedding);

                // Build sentinel refinement input from PromptInterpreter output
                object? sentinelInput = null;
                if (interpreted?.SentinelIntent != null)
                {
                    var si = interpreted.SentinelIntent;
                    sentinelInput = DoomSummarizerAdapter.ToRefinementInput(
                        si.IsComposite,
                        si.Subqueries?.ToList(),
                        si.CorrectedQuery,
                        si.FilterKeywords?.ToList(),
                        si.SearchQueries?.ToList(),
                        si.Entities?.ToList(),
                        si.TimeSensitivity,
                        si.RequiresFresh,
                        si.Intent,
                        si.Categories?.ToDictionary(k => k.Key, k => k.Value));
                }

                var hasUrls = nerContext?.RecognizerSignals?.Urls.Count > 0
                              || (interpreted?.Websites.Count > 0);
                var hasDateTimes = nerContext?.RecognizerSignals?.DateTimes.Count > 0;

                decomposition = await decomposer.DecomposeAsync(
                    settings.Prompt,
                    nerContext?.Entities?.ToList(),
                    hasUrls,
                    hasDateTimes,
                    sentinelInput,
                    cancellationToken);

                decompositionEnrichment = DoomSummarizerAdapter.GetEnrichment(decomposition);

                if (settings.DebugPipeline)
                {
                    var conceptPolicy = new ConceptRegistry().GetPolicy(decomposition.Concept);
                    WriteStatus($"[grey]Decomposer: complexity={decomposition.Complexity}, " +
                                $"concept={decomposition.Concept} (budget={conceptPolicy.FetchBudget}), " +
                                $"nodes={decomposition.Nodes.Count}, " +
                                $"fastPath={decomposition.IsFastPath}, " +
                                $"tools={decomposition.HasToolActions}[/]");

                    if (decomposition.HasToolActions)
                    {
                        foreach (var tool in decompositionEnrichment.ToolActions)
                        {
                            var paramStr = string.Join(", ", tool.Parameters.Select(p => $"{p.Key}={p.Value}"));
                            WriteStatus($"[grey]  Tool: {tool.Tool} → {Markup.Escape(tool.Intent)} ({Markup.Escape(paramStr)})[/]");
                        }
                    }

                    if (!decomposition.IsFastPath && decomposition.Nodes.Count > 1)
                    {
                        foreach (var node in decomposition.Nodes)
                        {
                            WriteStatus($"[grey]  Node: {Markup.Escape($"[{node.Type}]")} {Markup.Escape(node.Query)}[/]");
                        }
                    }
                }

                // Feed decomposer content references back into interpreted prompt websites
                if (interpreted != null && decompositionEnrichment.ContentReferences.Count > 0)
                {
                    foreach (var reference in decompositionEnrichment.ContentReferences)
                    {
                        if (reference.Kind == ContentReferenceKind.Url &&
                            !interpreted.Websites.Contains(reference.Uri))
                        {
                            interpreted.Websites.Add(reference.Uri);
                        }
                    }
                }

                // Feed decomposer sub-query search terms back into interpreted prompt
                if (interpreted != null && !decomposition.IsFastPath)
                {
                    foreach (var node in decomposition.Nodes.Where(n =>
                        n.Type == QueryNodeType.Atomic && n.SearchQueries.Count > 0))
                    {
                        foreach (var sq in node.SearchQueries)
                        {
                            if (!interpreted.SearchQueries.Any(existing =>
                                existing.Contains(sq, StringComparison.OrdinalIgnoreCase) ||
                                sq.Contains(existing, StringComparison.OrdinalIgnoreCase)))
                            {
                                interpreted.SearchQueries.Add(sq);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Decomposer failure is non-fatal — the existing pipeline works without it
                if (settings.DebugPipeline)
                    WriteStatus($"[yellow]Decomposer failed (non-fatal): {Markup.Escape(ex.Message)}[/]");
            }
        }

        // Get vibe prompt - supports predefined vibes or arbitrary text
        string vibePrompt;
        if (boot.Config.Vibes.TryGetValue(vibe, out var configuredPrompt))
        {
            vibePrompt = configuredPrompt;
        }
        else if (IsCustomVibe(vibe))
        {
            // Arbitrary vibe text - use it directly as the instruction
            vibePrompt = $"Apply this tone/perspective: {vibe}. Filter and present content through this lens.";
        }
        else
        {
            vibePrompt = boot.Config.Vibes.GetValueOrDefault("neutral", "Objective, balanced summary.");
        }

        var ollamaAvailable = !settings.NoLlm && await ollama.IsAvailableAsync();
        if (!ollamaAvailable)
            WriteStatus("[yellow]Warning: Ollama not available. Summaries will be limited.[/]");

        // Query feedback: check for similar recent query to reuse cached segments
        var queryText = interpreted?.RawPrompt ?? settings.Prompt ?? "";
        float[]? earlyQueryEmbedding = null;
        QueryMatch? cachedQuery = null;
        var useCachedSegments = false;

        if (!settings.Force && !settings.LocalOnly && string.IsNullOrWhiteSpace(settings.Name) && !string.IsNullOrWhiteSpace(queryText))
        {
            // Temporal intent bypass: if query needs fresh data, skip cache entirely
            var requiresFresh = interpreted?.SentinelIntent?.RequiresFresh == true;
            var isTimeSensitive = interpreted?.SentinelIntent?.TimeSensitivity is "breaking" or "today";

            if (requiresFresh || isTimeSensitive)
            {
                if (settings.DebugPipeline)
                    WriteStatus($"[grey]Cache bypass: temporal intent detected (fresh={requiresFresh}, time={interpreted?.SentinelIntent?.TimeSensitivity})[/]");
            }
            else
            {
                earlyQueryEmbedding = await boot.Embedding.EmbedAsync(queryText, cancellationToken);
                cachedQuery = await boot.Storage.FindSimilarQueryAsync(earlyQueryEmbedding, threshold: 0.97);
                if (cachedQuery != null)
                {
                    useCachedSegments = true;
                    var ageMin = (int)(DateTimeOffset.UtcNow - cachedQuery.IssuedAt).TotalMinutes;
                    WriteStatus($"[grey]Reusing {cachedQuery.ItemIds.Count} segments ({cachedQuery.Similarity:F2} match, {ageMin}m ago)[/]");
                }
            }
        }

        // Clear the status line before Progress takes over rendering
        if (hasStatusLine)
            Console.Write("\x1b[1A\x1b[2K");

        var items = new List<ContentItem>();
        var uniqueItems = new List<ContentItem>();

        // Rendering state — hoisted so console output happens after progress bars are gone
        var analyzedItems = new List<(string title, string summary, string topic, float sentiment, string url, double relevance)>();
        var finalSummary = "";
        var template = "default";
        var isBlogTemplate = false;
        DigestData? templateData = null;
        var allEntities = new List<NerEntity>();
        var articleEntityMap = new List<(ContentItem item, List<NerEntity> entities)>();
        var extractEntities = false;
        var linkCacheHits = 0;
        var linksSkippedByRelevance = 0;

        await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                // Stage 1: Fetch content (or load from knowledge base)
                // --name implies --local mode (query a named KB collection)
                var isLocalMode = settings.LocalOnly || !string.IsNullOrWhiteSpace(settings.Name);
                var fetchTask = ctx.AddTask(
                    isLocalMode ? "[cyan]Loading from knowledge base[/]" : "[cyan]Fetching content[/]",
                    maxValue: 100);

                // --local / --name mode: skip ALL fetching, query stored knowledge base only
                // Delegates to shared RetrievalPipeline (Lucene FTS + embedding HNSW + entity profiles + RRF)
                if (isLocalMode)
                {
                    var localQuery = interpreted?.RawPrompt ?? settings.Prompt ?? "";

                    // Derive source filter: --name takes priority, then --source crawl:xxx or page:xxx
                    string? sourceFilter = null;
                    if (!string.IsNullOrWhiteSpace(settings.Name))
                    {
                        var collections = await boot.Storage.GetCollectionsAsync();
                        var matchingCollection = collections.FirstOrDefault(c =>
                            c.Source.Equals(settings.Name, StringComparison.OrdinalIgnoreCase) ||
                            c.Source.Equals($"crawl:{settings.Name}", StringComparison.OrdinalIgnoreCase) ||
                            c.Source.Equals($"page:{settings.Name}", StringComparison.OrdinalIgnoreCase));
                        sourceFilter = matchingCollection?.Source ?? $"crawl:{settings.Name}";
                    }
                    else
                    {
                        sourceFilter = settings.Sources?.FirstOrDefault(s =>
                            s.StartsWith("crawl:", StringComparison.OrdinalIgnoreCase) ||
                            s.StartsWith("page:", StringComparison.OrdinalIgnoreCase));
                    }

                    var collectionLabel = sourceFilter ?? "all";
                    var collectionName = settings.Name ?? "default";
                    fetchTask.Value = 10;

                    if (!string.IsNullOrWhiteSpace(localQuery))
                    {
                        var retrieval = new RetrievalPipeline(boot.Embedding, boot.Storage, boot.EntityStore);
                        var retrievalResult = await retrieval.SearchAsync(localQuery, new RetrievalOptions
                        {
                            SourceFilter = sourceFilter,
                            CollectionName = collectionName,
                            TopK = settings.Limit * 2,
                            MinRelevance = 0.15f,
                            IsKnowledgeBase = true,
                            UseEmbeddingDedup = true,
                            QueryEntities = interpreted?.SentinelIntent?.Entities,
                        }, cancellationToken);

                        items.AddRange(retrievalResult.Items);
                    }
                    else
                    {
                        // No query: return most recent from the collection
                        var storedLocal = sourceFilter != null
                            ? await boot.Storage.GetRecentItemsAsync(days: 365, source: sourceFilter)
                            : await boot.Storage.GetRecentItemsAsync(days: 30);

                        var localItems = storedLocal
                            .Where(s => !string.IsNullOrEmpty(s.Summary) || !string.IsNullOrEmpty(s.Title))
                            .Select(s => s.ToContentItem())
                            .OrderByDescending(i => i.FetchedAt)
                            .Take(settings.Limit)
                            .ToList();
                        items.AddRange(localItems);
                    }

                    fetchTask.Value = 100;
                    fetchTask.Description = $"[cyan]KB: {items.Count} items matched[/]";
                    if (settings.DebugPipeline)
                        AnsiConsole.MarkupLine($"[grey]KB query ({Markup.Escape(collectionLabel)}): {items.Count} items matched[/]");
                }

                // Segment reuse: load cached items from a similar recent query
                if (!isLocalMode && useCachedSegments && cachedQuery != null)
                {
                    var cachedStored = await boot.Storage.GetItemsByIdsAsync(cachedQuery.ItemIds);
                    var cachedItems = cachedStored
                        .Where(s => !string.IsNullOrEmpty(s.Summary) || !string.IsNullOrEmpty(s.Title))
                        .Select(s => s.ToContentItem())
                        .ToList();

                    // Relevance gate: verify cached segments have sufficient salience for THIS query
                    // Only reuse cache when local data is genuinely good — otherwise fetch fresh
                    if (earlyQueryEmbedding != null && cachedItems.Count > 0)
                    {
                        var withEmbeddings = cachedItems.Where(i => i.Embedding != null).ToList();
                        if (withEmbeddings.Count > 0)
                        {
                            var similarities = withEmbeddings
                                .Select(i => VectorMath.CosineSimilarity(earlyQueryEmbedding, i.Embedding!))
                                .OrderByDescending(s => s)
                                .ToList();

                            var topRelevance = similarities.Take(5).Average();
                            var bestSingle = similarities.First();
                            var aboveThreshold = similarities.Count(s => s >= 0.30f);

                            // Require: (1) top-5 average >= 0.40, AND (2) best single >= 0.50,
                            // AND (3) at least 3 items above 0.30 — ensures genuine salience
                            if (topRelevance < 0.40f || bestSingle < 0.50f || aboveThreshold < 3)
                            {
                                useCachedSegments = false;
                                if (!settings.Quiet)
                                    AnsiConsole.MarkupLine($"[yellow]Cached segments lack salience for this query (avg={topRelevance:F2}, best={bestSingle:F2}, above-0.30={aboveThreshold}) — fetching fresh[/]");
                            }
                            else if (settings.DebugPipeline)
                            {
                                AnsiConsole.MarkupLine($"[grey]Cache salience: avg={topRelevance:F2}, best={bestSingle:F2}, above-0.30={aboveThreshold} — reusing[/]");
                            }
                        }
                        else
                        {
                            // No embeddings to evaluate — can't verify salience, fetch fresh
                            useCachedSegments = false;
                            if (!settings.Quiet)
                                AnsiConsole.MarkupLine("[yellow]Cached segments have no embeddings — fetching fresh[/]");
                        }
                    }

                    if (useCachedSegments)
                    {
                        items.AddRange(cachedItems);
                        fetchTask.Value = 100;
                        fetchTask.Description = $"[green]Reused {items.Count} cached segments (skipped fetching)[/]";
                    }
                }

                // Detect query type early — used for roundup date-gating inside fetch mode
                // AND for adaptive RRF weights in Stage 2.5 (outside the fetch block)
                var earlyQueryType = QueryTypeDetector.Detect(interpreted?.RawPrompt ?? settings.Prompt, interpreted?.SentinelIntent);

                if (!isLocalMode && !useCachedSegments)
                {
                // Normal fetch mode
                var fetchTasks = new List<Task<List<ContentItem>>>();

                // Determine what to fetch
                var sources = settings.Sources?.ToList() ?? [];
                if (interpreted != null)
                {
                    sources.AddRange(interpreted.Sources);
                    sources.AddRange(interpreted.Websites);
                    sources.AddRange(interpreted.SearchQueries.Select(q => $"search:{q}"));
                }

                // If nothing specified, use general search sources (not tech-specific)
                // Google News search + DuckDuckGo cover most topics
                if (sources.Count == 0)
                {
                    var query = interpreted?.RawPrompt ?? settings.Prompt;
                    sources.AddRange([$"gnews:{query}", $"search:{query}"]);
                }

                // Dedupe sources
                sources = sources.Distinct().ToList();

                var perSourceLimit = Math.Max(10, settings.Limit / Math.Max(1, sources.Count));

                // Initialize plugin registry (builtins + runtime plugins)
                var pluginRegistry = new SourcePluginRegistry();
                var outputRegistry = new OutputPluginRegistry();
                BuiltinPlugins.RegisterAllSources(pluginRegistry);
                BuiltinPlugins.RegisterAllOutputs(outputRegistry);

                // Load runtime plugins from manifest (~/.doomsummarizer/plugins/)
                var pluginManager = new PluginManager(httpClient);
                pluginManager.LoadAndRegister(pluginRegistry, outputRegistry);

                var pluginServices = new SourcePluginServices
                {
                    HttpClient = httpClient,
                    ApiKeys = boot.ApiKeys!,
                    ApiBudget = boot.ApiBudget!,
                    CircuitBreaker = circuitBreaker
                };
                await pluginRegistry.InitializeAllAsync(pluginServices, cancellationToken);

                // Create parallel fetch tasks via plugin registry
                foreach (var source in sources)
                {
                    var fetchCtx = SourceFetchContext.ParseWithCompositeKeys(
                        source,
                        pluginRegistry.AllKeys,
                        query: interpreted?.RawPrompt ?? settings.Prompt,
                        limit: perSourceLimit,
                        vibe: vibe,
                        config: boot.Config,
                        rawPrompt: interpreted?.RawPrompt ?? settings.Prompt);

                    var plugin = pluginRegistry.FindByKey(fetchCtx.SourceKey);
                    if (plugin != null)
                    {
                        var capturedCtx = fetchCtx;
                        fetchTasks.Add(Task.Run(async () =>
                            await plugin.FetchAsync(capturedCtx, cancellationToken)));
                    }
                    else if (fetchCtx.SourceKey.StartsWith("http"))
                    {
                        // URL fallback — route to the web plugin
                        var webPlugin = pluginRegistry.FindByKey("web");
                        if (webPlugin != null)
                        {
                            var webCtx = fetchCtx with { RawSource = source };
                            fetchTasks.Add(Task.Run(async () =>
                                await webPlugin.FetchAsync(webCtx, cancellationToken)));
                        }
                    }
                }

                fetchTask.Value = 20;

                // Wait for all fetches in parallel
                var results = await Task.WhenAll(fetchTasks);
                foreach (var result in results)
                {
                    items.AddRange(result);
                }

                // Fix broken URLs from aggregators (Google News, Bing News)
                // These return redirect URLs that often return 400/404
                var urlFixer = new UrlFixerService(httpClient);
                var urlsNeedingFix = items.Count(i => UrlFixerService.NeedsFix(i.Url));
                if (urlsNeedingFix > 0)
                {
                    if (settings.DebugPipeline)
                        AnsiConsole.MarkupLine($"[grey]URL fixer: resolving {urlsNeedingFix} aggregator URLs...[/]");
                    await urlFixer.FixUrlsAsync(items, cancellationToken);
                }

                fetchTask.Value = 80;

                // Source diversity fallback: if initial fetch returned too few items,
                // auto-add search fallbacks to fill the gap via plugin registry
                var minDesired = Math.Max(15, settings.Limit / 2);
                if (items.Count < minDesired && !string.IsNullOrEmpty(interpreted?.RawPrompt ?? settings.Prompt))
                {
                    var fallbackQuery = interpreted?.RawPrompt ?? settings.Prompt ?? "";
                    var fallbackSources = new List<Task<List<ContentItem>>>();

                    var hasSearchSource = sources.Any(s =>
                        s.StartsWith("search:", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("gsearch", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("brave", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("serper", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("tavily", StringComparison.OrdinalIgnoreCase));

                    if (!hasSearchSource)
                    {
                        var searchPlugin = pluginRegistry.FindByKey("search");
                        if (searchPlugin != null)
                        {
                            var searchCtx = new SourceFetchContext
                            {
                                RawSource = $"search:{fallbackQuery}",
                                SourceKey = "search",
                                SubParams = [fallbackQuery],
                                Query = fallbackQuery,
                                RawPrompt = fallbackQuery,
                                Limit = perSourceLimit * 2,
                                Vibe = vibe,
                                Config = boot.Config
                            };
                            fallbackSources.Add(Task.Run(async () =>
                                await searchPlugin.FetchAsync(searchCtx, cancellationToken)));
                        }
                    }

                    var hasNewsSource = sources.Any(s =>
                        s.StartsWith("gnews", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("newsapi", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("newsdata", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("currents", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("bravenews", StringComparison.OrdinalIgnoreCase) ||
                        s.StartsWith("serpernews", StringComparison.OrdinalIgnoreCase));

                    if (!hasNewsSource)
                    {
                        var searchPlugin = pluginRegistry.FindByKey("search");
                        if (searchPlugin != null)
                        {
                            if (boot.ApiKeys!.IsAvailable("newsapi"))
                            {
                                var newsCtx = new SourceFetchContext
                                {
                                    RawSource = $"newsapi:{fallbackQuery}",
                                    SourceKey = "newsapi",
                                    SubParams = [fallbackQuery],
                                    Query = fallbackQuery,
                                    Limit = perSourceLimit,
                                    Vibe = vibe,
                                    Config = boot.Config
                                };
                                fallbackSources.Add(Task.Run(async () =>
                                    await searchPlugin.FetchAsync(newsCtx, cancellationToken)));
                            }

                            if (boot.ApiKeys!.IsAvailable("currents"))
                            {
                                var currentsCtx = new SourceFetchContext
                                {
                                    RawSource = $"currents:{fallbackQuery}",
                                    SourceKey = "currents",
                                    SubParams = [fallbackQuery],
                                    Query = fallbackQuery,
                                    Limit = perSourceLimit,
                                    Vibe = vibe,
                                    Config = boot.Config
                                };
                                fallbackSources.Add(Task.Run(async () =>
                                    await searchPlugin.FetchAsync(currentsCtx, cancellationToken)));
                            }

                            // GNews RSS as final news fallback if no API keys
                            if (!boot.ApiKeys!.IsAvailable("newsapi") && !boot.ApiKeys!.IsAvailable("currents"))
                            {
                                var gnewsPlugin = pluginRegistry.FindByKey("gnews");
                                if (gnewsPlugin != null)
                                {
                                    var gnewsCtx = new SourceFetchContext
                                    {
                                        RawSource = $"gnews:{fallbackQuery}",
                                        SourceKey = "gnews",
                                        SubParams = [fallbackQuery],
                                        Query = fallbackQuery,
                                        Limit = perSourceLimit,
                                        Vibe = vibe,
                                        Config = boot.Config
                                    };
                                    fallbackSources.Add(Task.Run(async () =>
                                        await gnewsPlugin.FetchAsync(gnewsCtx, cancellationToken)));
                                }
                            }
                        }
                    }

                    if (fallbackSources.Count > 0)
                    {
                        var fallbackResults = await Task.WhenAll(fallbackSources);
                        var fallbackCount = 0;
                        foreach (var fb in fallbackResults)
                        {
                            items.AddRange(fb);
                            fallbackCount += fb.Count;
                        }
                        if (fallbackCount > 0)
                            fetchTask.Description = $"[cyan]Fallback: +{fallbackCount} items[/]";
                        if (settings.DebugPipeline && fallbackCount > 0)
                            AnsiConsole.MarkupLine($"[grey]Diversity fallback: added {fallbackCount} items from backup sources[/]");
                    }
                }

                fetchTask.Value = 100;
                fetchTask.Description = $"[green]Fetched {items.Count} items[/]";

                // Apply topic filter ONLY to items from generic sources (hn, reddit, lobsters, devto)
                // that don't natively filter by topic. Skip for search/gnews/category-specific feeds
                // since those already fetched topic-relevant content.
                if (interpreted?.Topics.Count > 0)
                {
                    var topicTerms = interpreted.Topics.SelectMany(t => t.Split(' ')).ToList();
                    // Sources that already searched/filtered for the topic
                    var topicAwareSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        { "gnews", "search", "bbc", "guardian", "cnn", "reuters", "currents",
                          "factcheck", "spaceflight", "earthquake", "wikipedia", "arxiv" };

                    var preFilterCount = items.Count;

                    var filtered = items.Where(item =>
                    {
                        // Keep all items from topic-aware sources (they already filtered)
                        if (topicAwareSources.Contains(item.Source))
                            return true;

                        // Filter generic sources by topic terms
                        var text = $"{item.Title} {item.Content ?? ""}".ToLowerInvariant();
                        return topicTerms.Any(term => text.Contains(term.ToLowerInvariant()));
                    }).ToList();

                    // Graceful fallback: if topic filter is too aggressive (< 5 items),
                    // keep all topic-aware items + relax to allow partial term matches
                    if (filtered.Count < 5 && preFilterCount >= 5)
                    {
                        // Softer filter: any single word from topic terms (not full phrases)
                        var singleWords = topicTerms
                            .SelectMany(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                            .Where(w => w.Length > 3)
                            .Select(w => w.ToLowerInvariant())
                            .Distinct()
                            .ToList();

                        filtered = items.Where(item =>
                        {
                            if (topicAwareSources.Contains(item.Source))
                                return true;
                            var text = $"{item.Title} {item.Content ?? ""}".ToLowerInvariant();
                            return singleWords.Any(word => text.Contains(word));
                        }).ToList();

                        // If still too few, skip topic filter entirely (rely on downstream relevance)
                        if (filtered.Count < 5)
                        {
                            fetchTask.Description = $"[cyan]Topic filter: skipped (too few)[/]";
                            if (settings.DebugPipeline)
                                AnsiConsole.MarkupLine($"[grey]Topic filter: {preFilterCount} → {filtered.Count} (too aggressive, skipping)[/]");
                            filtered = items;
                        }
                        else
                        {
                            fetchTask.Description = $"[cyan]Topic filter: {filtered.Count} items[/]";
                            if (settings.DebugPipeline)
                                AnsiConsole.MarkupLine($"[grey]Topic filter: {preFilterCount} → {filtered.Count} items (relaxed)[/]");
                        }
                    }
                    else if (filtered.Count < preFilterCount)
                    {
                        fetchTask.Description = $"[cyan]Topic filter: {filtered.Count} items[/]";
                        if (settings.DebugPipeline)
                            AnsiConsole.MarkupLine($"[grey]Topic filter: {preFilterCount} → {filtered.Count} items[/]");
                    }

                    items = filtered;
                }

                // Temporal filtering: use sentinel LLM extraction (not regex patterns!)
                var sentinelIntent = interpreted?.SentinelIntent;
                var needsRecencyFilter = sentinelIntent?.RequiresFresh == true
                    || sentinelIntent?.TimeSensitivity is "today" or "breaking" or "week"
                    || sentinelIntent?.DateRange != null;

                // For roundup intent, also penalize topic drift
                if (earlyQueryType == QueryType.Roundup)
                {
                    foreach (var item in items)
                    {
                        if (QueryTypeDetector.IsTopicDrift(item))
                            item.RelevanceScore *= 0.3; // Heavy penalty
                    }
                }

                // Date-gate using sentinel's temporal extraction
                if (needsRecencyFilter)
                {
                    // Get max age from sentinel intent (LLM-driven, not hardcoded)
                    var maxAge = QueryTypeDetector.GetMaxAge(sentinelIntent, interpreted?.RawPrompt ?? settings.Prompt);

                    foreach (var item in items)
                    {
                        var mult = QueryTypeDetector.GetFreshnessMultiplier(item, maxAge);
                        item.RelevanceScore *= mult;
                    }

                    // Re-sort by relevance after freshness adjustment
                    items = items.OrderByDescending(i => i.RelevanceScore).ToList();

                    var freshCount = items.Count(i => (DateTimeOffset.UtcNow - i.CreatedAt) <= maxAge);
                    var ageDesc = maxAge.TotalHours <= 48 ? $"{maxAge.TotalHours}h" : $"{maxAge.TotalDays}d";
                    fetchTask.Description = $"[cyan]Date-gate ({ageDesc}): {freshCount}/{items.Count} fresh[/]";
                    if (settings.DebugPipeline)
                    {
                        var reason = sentinelIntent?.RequiresFresh == true ? "requires_fresh"
                            : sentinelIntent?.TimeSensitivity ?? "date_range";
                        AnsiConsole.MarkupLine($"[grey]Temporal filter ({reason}): {freshCount}/{items.Count} items within {ageDesc}[/]");
                    }
                }

                // Show raw content if requested
                if (settings.ShowRaw && !settings.Json)
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[bold yellow]Raw Fetched Content:[/]");
                    foreach (var item in items.Take(settings.Limit))
                    {
                        AnsiConsole.MarkupLine($"[cyan]---[/] {Markup.Escape(item.Title)}");
                        if (!string.IsNullOrEmpty(item.Url))
                            AnsiConsole.MarkupLine($"[grey]URL:[/] {Markup.Escape(item.Url)}");
                        if (!string.IsNullOrEmpty(item.Content))
                        {
                            var content = item.Content.Length > 500 ? item.Content[..500] + "..." : item.Content;
                            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(content)}[/]");
                        }
                        AnsiConsole.WriteLine();
                    }
                }

                // Load recent stored items to combine with fresh content (skip with --force)
                var storedItems = settings.Force ? [] : await boot.Storage.GetRecentItemsAsync(days: 1);
                var storedContentItems = storedItems
                    .Where(s => !string.IsNullOrEmpty(s.Summary)) // Only include analyzed items
                    .Select(s => s.ToContentItem() with { Score = 0 }) // Lower priority than fresh items
                    .ToList();

                // Combine fresh items first (higher priority), then stored items
                items.AddRange(storedContentItems);

                // Inject NER-matched cached items (entity-specific, high relevance)
                if (nerContext?.HasCachedData == true)
                {
                    var existingUrls = new HashSet<string>(
                        items.Where(i => !string.IsNullOrEmpty(i.Url)).Select(i => i.Url!),
                        StringComparer.OrdinalIgnoreCase);

                    var nerCachedItems = nerContext.CachedItems
                        .Where(s => !string.IsNullOrEmpty(s.Summary))
                        .Select(s => s.ToContentItem())
                        .Where(c => !existingUrls.Contains(c.Url ?? ""))
                        .ToList();

                    if (nerCachedItems.Count > 0)
                    {
                        items.AddRange(nerCachedItems);
                        fetchTask.Description = $"[cyan]NER cache: +{nerCachedItems.Count} items[/]";
                        if (settings.DebugPipeline)
                            AnsiConsole.MarkupLine($"[grey]NER cache: injected {nerCachedItems.Count} entity-matched items[/]");
                    }
                }
                } // end normal fetch mode

                // Stage 2: Deduplicate by URL (not embedding - topic queries have similar content)
                var dedupeTask = ctx.AddTask("[cyan]Deduplicating[/]", maxValue: items.Count);
                uniqueItems.Clear(); // Use outer scope variable
                var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var item in items)
                {
                    // Dedupe by URL (same article from different sources)
                    // and by title (catches exact duplicates without URL)
                    var normalizedUrl = item.Url?.Split('?')[0].TrimEnd('/') ?? "";
                    var normalizedTitle = item.Title.ToLowerInvariant().Trim();

                    var isDuplicate = !string.IsNullOrEmpty(normalizedUrl) && seenUrls.Contains(normalizedUrl)
                                      || seenTitles.Contains(normalizedTitle);

                    if (!isDuplicate)
                    {
                        uniqueItems.Add(item);
                        if (!string.IsNullOrEmpty(normalizedUrl))
                            seenUrls.Add(normalizedUrl);
                        seenTitles.Add(normalizedTitle);
                    }

                    dedupeTask.Increment(1);
                }

                dedupeTask.Description = $"[green]Found {uniqueItems.Count} unique items[/]";

                // Stage 2.1: Source domain filtering (allow/block lists)
                if (boot.Config.SourceFilter.AllowedDomains.Count > 0 || boot.Config.SourceFilter.BlockedDomains.Count > 0)
                {
                    var preFilterCount = uniqueItems.Count;
                    uniqueItems = ApplySourceDomainFilter(uniqueItems, boot.Config.SourceFilter);

                    if (uniqueItems.Count < preFilterCount)
                        fetchTask.Description = $"[cyan]Source filter: {uniqueItems.Count} items[/]";
                    if (settings.DebugPipeline && uniqueItems.Count < preFilterCount)
                        AnsiConsole.MarkupLine($"[grey]Source filter: {preFilterCount} → {uniqueItems.Count} items[/]");
                }

                // Stage 2.2: KB enrichment (web queries only) — Lucene + Embeddings
                // Uses sentinel-generated Lucene query + semantic similarity for better recall
                if (!isLocalMode && uniqueItems.Count > 0)
                {
                    var enrichQuery = interpreted?.RawPrompt ?? settings.Prompt ?? "";
                    if (!string.IsNullOrWhiteSpace(enrichQuery))
                    {
                        var candidateIds = new HashSet<string>();
                        var luceneCount = 0;
                        var embedCount = 0;

                        // Layer 1: Lucene search (sentinel-generated query for salience)
                        try
                        {
                            var luceneIndexPath = Path.Combine(boot.Storage.DataPath, "lucene", "enrichment");
                            using var lucene = new LuceneSearchService(luceneIndexPath);
                            lucene.Open();

                            // Ensure KB items are indexed (incremental)
                            var recentItems = await boot.Storage.GetRecentItemsAsync(days: 90);
                            var itemsToIndex = recentItems
                                .Where(s => !lucene.ContainsDocument(s.Id))
                                .Select(s => s.ToContentItem())
                                .ToList();
                            if (itemsToIndex.Count > 0)
                            {
                                lucene.IndexItems(itemsToIndex);
                                lucene.Commit();
                            }

                            // Generate Lucene query from natural language (via sentinel)
                            var luceneQuery = await LuceneQueryGenerator.GenerateQueryAsync(enrichQuery, ollama, cancellationToken);
                            if (settings.DebugPipeline)
                                AnsiConsole.MarkupLine($"[grey]KB Lucene query: {Markup.Escape(luceneQuery)}[/]");

                            var luceneResults = lucene.Search(luceneQuery, limit: 15);
                            foreach (var r in luceneResults) candidateIds.Add(r.Id);
                            luceneCount = luceneResults.Count;
                        }
                        catch (Exception ex)
                        {
                            if (settings.DebugPipeline)
                                AnsiConsole.MarkupLine($"[grey]Lucene KB search skipped: {ex.Message}[/]");
                        }

                        // Layer 2: Embedding search for semantic coverage (catches related content)
                        try
                        {
                            var queryEmbed = await boot.Embedding.EmbedAsync(enrichQuery, cancellationToken);
                            var embeddingResults = await boot.Storage.FindSimilarAsync(queryEmbed, limit: 10, threshold: 0.25);
                            foreach (var r in embeddingResults) candidateIds.Add(r.Id);
                            embedCount = embeddingResults.Count;
                        }
                        catch (Exception ex)
                        {
                            if (settings.DebugPipeline)
                                AnsiConsole.MarkupLine($"[grey]Embedding search skipped: {ex.Message}[/]");
                        }

                        // Layer 3: Entity profile HNSW search (when entity profiles exist)
                        var entityCount = 0;
                        if (boot.EntityStore != null && interpreted?.SentinelIntent?.Entities?.Count >= 2)
                        {
                            try
                            {
                                var hasProfiles = await boot.EntityStore!.HasEntityProfilesAsync();
                                if (hasProfiles)
                                {
                                    var entityProfileService = new EntityProfileService(boot.Embedding);
                                    var entityDocCounts = await boot.EntityStore!.GetEntityDocCountsAsync();
                                    var totalDocs = await boot.EntityStore!.GetTotalDocsWithEntitiesAsync();

                                    // Infer entity types using heuristics (ORG, PER, LOC, MISC)
                                    var queryEntities = interpreted.SentinelIntent.Entities
                                        .Select(e => (name: e, type: EntityProfileService.InferEntityType(e), confidence: 0.8f))
                                        .ToList();

                                    var queryEntityProfile = await entityProfileService.ComputeQueryProfileAsync(
                                        queryEntities, entityDocCounts, totalDocs);

                                    if (queryEntityProfile.Length > 0)
                                    {
                                        var entityResults = await boot.EntityStore!.FindRelatedByEntityProfileAsync(
                                            queryEntityProfile, topK: 8, minSimilarity: 0.25f);
                                        foreach (var (itemId, _, _) in entityResults)
                                            candidateIds.Add(itemId);
                                        entityCount = entityResults.Count;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                if (settings.DebugPipeline)
                                    AnsiConsole.MarkupLine($"[grey]Entity profile search skipped: {ex.Message}[/]");
                            }
                        }

                        // Merge into results — with salience gate
                        // Only keep KB items that are genuinely relevant to THIS query
                        if (candidateIds.Count > 0)
                        {
                            var storedItems = await boot.Storage.LoadItemsByIdsAsync(candidateIds.ToList());
                            var existingIds = new HashSet<string>(uniqueItems.Select(i => i.Id));
                            var existingUrls2 = new HashSet<string>(
                                uniqueItems.Where(i => !string.IsNullOrEmpty(i.Url))
                                    .Select(i => i.Url!.Split('?')[0].TrimEnd('/').ToLowerInvariant()),
                                StringComparer.OrdinalIgnoreCase);
                            var newFromKb = storedItems.Where(s =>
                                !existingIds.Contains(s.Id) &&
                                (string.IsNullOrEmpty(s.Url) || !existingUrls2.Contains(s.Url.Split('?')[0].TrimEnd('/').ToLowerInvariant())))
                                .ToList();

                            // Salience gate: score KB candidates against query, keep only salient items
                            var enrichQueryEmbed = await boot.Embedding.EmbedAsync(enrichQuery, cancellationToken);
                            var preGateCount = newFromKb.Count;
                            newFromKb = newFromKb.Where(item =>
                            {
                                if (item.Embedding == null) return false;
                                var sim = VectorMath.CosineSimilarity(enrichQueryEmbed, item.Embedding);
                                return sim >= 0.30f;
                            }).ToList();

                            if (newFromKb.Count > 0)
                            {
                                uniqueItems.AddRange(newFromKb);
                                fetchTask.Description = $"[cyan]KB enrichment: +{newFromKb.Count} items[/]";
                                var entityInfo = entityCount > 0 ? $", Entity={entityCount}" : "";
                                var gateInfo = preGateCount > newFromKb.Count ? $", Gated={preGateCount - newFromKb.Count} below salience" : "";
                                if (settings.DebugPipeline)
                                    AnsiConsole.MarkupLine($"[grey]KB enrichment: Lucene={luceneCount}, Embed={embedCount}{entityInfo}, Merged={newFromKb.Count}{gateInfo}[/]");
                            }
                            else if (settings.DebugPipeline && preGateCount > 0)
                            {
                                AnsiConsole.MarkupLine($"[grey]KB enrichment: {preGateCount} candidates all below salience threshold (0.30) — skipped[/]");
                            }
                        }
                    }
                }

                // Stage 2.5: Unified scoring pipeline (5-signal RRF with PRF + outlier penalty)
                // All scoring goes through RetrievalPipeline.ScoreItemsAsync — single path for
                // KB queries (zero auth/freshness + Lucene FTS), web queries (query-type-adaptive),
                // and MCP tools. BM25 handled by Lucene at retrieval, not in scorer.
                var queryText = interpreted?.RawPrompt ?? settings.Prompt ?? "";
                float[]? queryEmbedding = null;
                List<float[]>? subqueryEmbeddings = null;

                // Compute query embedding (needed for scoring and post-scoring steps)
                if (!string.IsNullOrWhiteSpace(queryText))
                {
                    queryEmbedding = await boot.Embedding.EmbedAsync(queryText, cancellationToken);

                    // Composite query: embed each subquery for multi-query evidence checks
                    if (interpreted?.SentinelIntent?.HasSubqueries == true)
                    {
                        var subqueryTexts = interpreted.SentinelIntent.Subqueries!;
                        var sqEmbeddings = await boot.Embedding.EmbedBatchAsync(subqueryTexts, cancellationToken);
                        subqueryEmbeddings = sqEmbeddings.ToList();
                        if (settings.DebugPipeline)
                            AnsiConsole.MarkupLine($"[grey]Multi-query: {subqueryEmbeddings.Count} subquery embeddings[/]");
                    }
                }

                var scoringVibeText = vibe != "neutral" ? GetVibeRepresentativeText(vibe) : null;

                // Construct pipeline once — reused for scoring and potential re-search
                var scoringPipeline = new RetrievalPipeline(boot.Embedding, boot.Storage, boot.EntityStore);
                ScoringOptions? scoringOpts = null;
                ScoringResult? scoringResult = null;

                if (!string.IsNullOrWhiteSpace(queryText) && queryEmbedding != null)
                {
                    var preScoreCount = uniqueItems.Count;

                    scoringOpts = new ScoringOptions
                    {
                        Query = queryText,
                        QueryEmbedding = queryEmbedding,
                        VibeText = scoringVibeText,
                        IsKnowledgeBase = isLocalMode,
                        QueryType = earlyQueryType,
                        UseOutlierPenalty = true,
                        UseEmbeddingDedup = false, // Web-mode uses URL/title dedup instead
                    };

                    scoringResult = await scoringPipeline.ScoreItemsAsync(uniqueItems, scoringOpts, cancellationToken);
                    uniqueItems = scoringResult.Items;

                    if (uniqueItems.Count < preScoreCount)
                        fetchTask.Description = $"[cyan]Relevance: {uniqueItems.Count} items[/]";
                    fetchTask.Description = $"[cyan]RRF ranked: {uniqueItems.Count} items[/]";

                    if (settings.DebugPipeline)
                    {
                        if (uniqueItems.Count < preScoreCount)
                            AnsiConsole.MarkupLine($"[grey]Fast relevance filter: {preScoreCount} → {uniqueItems.Count} items[/]");

                        // Post-hoc signal breakdown for debug display
                        var authLookup2 = RelevanceScorer.ComputeAuthorityScores(uniqueItems)
                            .ToDictionary(x => x.item.Id, x => x.score);

                        AnsiConsole.WriteLine();
                        var table = new Table()
                            .Title("[bold yellow]Scoring Pipeline Results (5-signal RRF)[/]")
                            .Border(TableBorder.Rounded)
                            .AddColumn("[cyan]#[/]")
                            .AddColumn("[cyan]Source[/]")
                            .AddColumn("[cyan]Fresh[/]")
                            .AddColumn("[cyan]Auth[/]")
                            .AddColumn("[cyan]QSim[/]")
                            .AddColumn("[cyan]Qual[/]")
                            .AddColumn("[cyan]Vibe[/]")
                            .AddColumn("[cyan]RRF[/]")
                            .AddColumn("[cyan]Title[/]");

                        float[]? debugVibeEmbed = null;
                        if (scoringVibeText != null)
                            debugVibeEmbed = await boot.Embedding.EmbedAsync(scoringVibeText, cancellationToken);

                        // Quality anchors for debug display
                        var debugHighQ = await boot.Embedding.EmbedAsync(RelevanceScorer.HighQualityAnchorText, cancellationToken);
                        var debugLowQ = await boot.Embedding.EmbedAsync(RelevanceScorer.LowQualityAnchorText, cancellationToken);

                        var rank = 1;
                        // Use ORIGINAL query embedding for debug QSim — not the PRF-refined one.
                        // PRF centroid can drift toward off-topic items, showing misleading uniform
                        // similarity values. The original embedding reflects the actual user query.
                        var debugQueryEmbed = queryEmbedding;

                        // Diagnostic: embedding state
                        var withEmbed = uniqueItems.Count(i => i.Embedding != null);
                        var nullEmbed = uniqueItems.Count(i => i.Embedding == null);
                        AnsiConsole.MarkupLine($"[grey]Embeddings: {withEmbed} set, {nullEmbed} null | queryEmbed: {(debugQueryEmbed != null ? $"{debugQueryEmbed.Length}d" : "NULL")} | subqueries: {subqueryEmbeddings?.Count ?? 0}[/]");
                        if (withEmbed > 0 && debugQueryEmbed != null)
                        {
                            // Show actual cosine similarities for first 3 items to verify embedding discrimination
                            foreach (var diagItem in uniqueItems.Take(5))
                            {
                                if (diagItem.Embedding != null)
                                {
                                    var rawCos = VectorMath.CosineSimilarity(diagItem.Embedding, debugQueryEmbed);
                                    var sqInfo = "";
                                    if (subqueryEmbeddings?.Count > 0)
                                    {
                                        var sqSims = subqueryEmbeddings.Select(sq => VectorMath.CosineSimilarity(diagItem.Embedding, sq)).ToList();
                                        sqInfo = $", subq=({string.Join(", ", sqSims.Select(s => $"{s:F3}"))})";
                                    }
                                    var diagMsg = $"  {diagItem.Source}: \"{diagItem.Title[..Math.Min(40, diagItem.Title.Length)]}\" primary={rawCos:F4}{sqInfo} max={ComputeMaxQuerySimilarity(diagItem.Embedding, debugQueryEmbed, subqueryEmbeddings):F3}";
                                    AnsiConsole.MarkupLine($"[grey]{Markup.Escape(diagMsg)}[/]");
                                }
                                else
                                {
                                    AnsiConsole.MarkupLine($"[grey]  {Markup.Escape(diagItem.Source)}: \"{Markup.Escape(diagItem.Title[..Math.Min(40, diagItem.Title.Length)])}\" NO EMBEDDING[/]");
                                }
                            }
                        }

                        foreach (var item in uniqueItems.Take(25))
                        {
                            var fresh = RelevanceScorer.ComputeFreshness(item);
                            var auth = authLookup2.GetValueOrDefault(item.Id, 0.3);
                            var qSim = ComputeMaxQuerySimilarity(item.Embedding, debugQueryEmbed, subqueryEmbeddings);
                            var qual = item.Embedding != null
                                ? RelevanceScorer.ComputeQualityScore(item.Embedding, debugHighQ, debugLowQ) : 0.5;
                            var vSim = debugVibeEmbed != null && item.Embedding != null
                                ? VectorMath.CosineSimilarity(item.Embedding, debugVibeEmbed) : 0f;

                            table.AddRow(
                                $"{rank++}",
                                Markup.Escape(item.Source),
                                $"{fresh:F2}",
                                $"{auth:F2}",
                                $"{qSim:F3}",
                                $"{qual:F2}",
                                $"{vSim:F3}",
                                $"[bold]{item.RelevanceScore:F3}[/]",
                                Markup.Escape(item.Title.Length > 50 ? item.Title[..47] + "..." : item.Title));
                        }
                        AnsiConsole.Write(table);

                        var topScore = uniqueItems.FirstOrDefault()?.RelevanceScore ?? 0;
                        var botScore = uniqueItems.LastOrDefault()?.RelevanceScore ?? 0;
                        AnsiConsole.MarkupLine($"[grey]RRF ranked {uniqueItems.Count} items (top={topScore:F3}, bot={botScore:F3})[/]");
                    }
                }

                // Stage 2.5a: Apply source reliability weights (RRF score multipliers)
                if (boot.Config.SourceFilter.Weights.Count > 0)
                {
                    var weightedCount = ApplySourceWeights(uniqueItems, boot.Config.SourceFilter);
                    if (weightedCount > 0)
                        fetchTask.Description = $"[cyan]Src weights: {weightedCount} adjusted[/]";
                    if (settings.DebugPipeline && weightedCount > 0)
                        AnsiConsole.MarkupLine($"[grey]Source weights: {weightedCount} items adjusted[/]");

                    // Re-sort after weight adjustment
                    var weighted = uniqueItems.OrderByDescending(i => i.RelevanceScore).ToList();
                    uniqueItems.Clear();
                    uniqueItems.AddRange(weighted);
                }

                // Stage 2.5b: LFU diversity decay — penalize items returned too often
                if (uniqueItems.Count > 0)
                {
                    var itemIds = uniqueItems.Select(i => i.Id).ToList();
                    var usageStats = await boot.Storage.GetItemUsageAsync(itemIds);
                    if (usageStats.Count > 0)
                    {
                        var lfuAdjusted = 0;
                        foreach (var item in uniqueItems)
                        {
                            if (usageStats.TryGetValue(item.Id, out var usage) && usage.accessCount > 1)
                            {
                                // Mild decay: 1/(1 + 0.1 * log2(accessCount))
                                // 2 accesses → 0.91x, 4 → 0.83x, 8 → 0.77x, 16 → 0.71x
                                var decay = 1.0 / (1.0 + 0.1 * Math.Log2(usage.accessCount));
                                item.RelevanceScore *= decay;
                                lfuAdjusted++;
                            }
                        }

                        if (lfuAdjusted > 0)
                        {
                            // Re-sort after LFU decay
                            var lfuSorted = uniqueItems.OrderByDescending(i => i.RelevanceScore).ToList();
                            uniqueItems.Clear();
                            uniqueItems.AddRange(lfuSorted);

                            fetchTask.Description = $"[cyan]LFU: {lfuAdjusted} items decayed[/]";
                            if (settings.DebugPipeline)
                                AnsiConsole.MarkupLine($"[grey]LFU diversity: {lfuAdjusted} frequently-seen items decayed[/]");
                        }
                    }
                }

                // Stage 2.5c: One-hop link following for richer context
                linkCacheHits = 0;
                linksSkippedByRelevance = 0;
                if (boot.Config.LinkFollowing.Enabled && !settings.NoLinks)
                {
                    var itemsToFollow = uniqueItems.Take(settings.Limit).ToList();
                    var linkTask = ctx.AddTask("[cyan]Following links[/]", maxValue: itemsToFollow.Count);

                    var linkService = new LinkFollowingService(
                        httpClient, boot.Config.LinkFollowing, boot.Storage,
                        embedder: text => boot.Embedding.EmbedAsync(text).GetAwaiter().GetResult(),
                        queryEmbedding: queryEmbedding);
                    var activityLog = new List<string>();

                    await linkService.FollowLinksAsync(
                        itemsToFollow,
                        new Progress<(int current, int total)>(p => linkTask.Value = p.current),
                        onActivity: activity =>
                        {
                            activityLog.Add(activity);
                            // Show last activity in progress description (strip markup for non-markup-safe display)
                            linkTask.Description = $"[cyan]{Markup.Remove(activity)}[/]";
                        });

                    var totalLinked = itemsToFollow.Sum(i => i.LinkedPages.Count);
                    var enrichedCount = itemsToFollow.Count(i => i.IsEnriched);
                    var structuredCount = itemsToFollow.Count(i => i.ContentStructure != null);
                    linkTask.Value = itemsToFollow.Count;
                    var cacheInfo = linkService.CacheHits > 0 ? $", {linkService.CacheHits} cached" : "";
                    var relevanceInfo = linkService.LinksSkippedByRelevance > 0 ? $", {linkService.LinksSkippedByRelevance} irrelevant skipped" : "";
                    linkTask.Description = $"[green]Enriched {enrichedCount} articles ({structuredCount} with structure), {totalLinked} linked pages{cacheInfo}{relevanceInfo}[/]";

                    if (settings.DebugPipeline)
                    {
                        AnsiConsole.MarkupLine($"[grey]Links: {enrichedCount} enriched, {totalLinked} linked, {linkService.CacheHits} cache hits, {linkService.LinksSkippedByRelevance} irrelevant skipped[/]");
                    }

                    // Re-embed items that were enriched with full article content
                    // (original embeddings were computed on short RSS descriptions)
                    if (enrichedCount > 0)
                    {
                        foreach (var item in itemsToFollow.Where(i => i.IsEnriched))
                        {
                            var textToEmbed = $"{item.Title} {item.Content ?? ""}".Trim();
                            item.Embedding = await boot.Embedding.EmbedAsync(textToEmbed, cancellationToken);
                        }
                    }

                    // Capture stats for JSON output
                    linkCacheHits = linkService.CacheHits;
                    linksSkippedByRelevance = linkService.LinksSkippedByRelevance;
                }

                // In-corpus link authority ("silly PageRank"):
                // Articles that are linked by other articles in our corpus get an authority boost.
                var inLinkCounts = ComputeInCorpusLinkAuthority(uniqueItems);
                if (inLinkCounts.Count > 0)
                {
                    foreach (var item in uniqueItems)
                    {
                        var normalizedUrl = item.Url?.Split('?')[0].TrimEnd('/').ToLowerInvariant() ?? "";
                        if (inLinkCounts.TryGetValue(normalizedUrl, out var linkCount) && linkCount > 0)
                        {
                            // Boost: log scale so 2 in-links = +0.05, 5 = +0.08, 10 = +0.10
                            var boost = Math.Min(0.10, Math.Log2(1 + linkCount) * 0.035);
                            item.RelevanceScore = Math.Min(1.0, item.RelevanceScore + boost);
                        }
                    }

                    // Re-sort after boost
                    var boostedItems = uniqueItems.OrderByDescending(i => i.RelevanceScore).ToList();
                    uniqueItems.Clear();
                    uniqueItems.AddRange(boostedItems);

                    if (inLinkCounts.Values.Any(c => c > 0))
                    {
                        var boostedCount = inLinkCounts.Count(kv => kv.Value > 0);
                        fetchTask.Description = $"[cyan]PageRank: {boostedCount} boosted[/]";
                        if (settings.DebugPipeline)
                            AnsiConsole.MarkupLine($"[grey]In-corpus PageRank: {boostedCount} items boosted by cross-references[/]");
                    }
                }

                // Evidence sufficiency check: if top items are irrelevant, re-search with focused queries
                if (queryEmbedding != null && uniqueItems.Count > 0 && !isLocalMode)
                {
                    var topItems = uniqueItems.Take(5).Where(i => i.Embedding != null).ToList();
                    if (topItems.Count > 0)
                    {
                        // Multi-query: use max similarity across subqueries for composite queries
                        var avgRelevance = topItems
                            .Select(i => (double)ComputeMaxQuerySimilarity(i.Embedding, queryEmbedding, subqueryEmbeddings))
                            .Average();

                        if (avgRelevance < 0.25)
                        {
                            if (!settings.Quiet)
                                AnsiConsole.MarkupLine(
                                    $"[yellow]Evidence gap detected (top-5 relevance: {avgRelevance:F2}) — running targeted re-search[/]");

                            // Re-search with the raw query directly through available search APIs
                            var reSearchQuery = queryText;
                            var reSearchResults = new List<ContentItem>();
                            var reSearchTasks = new List<Task<List<ContentItem>>>();

                            if (boot.ApiKeys!.IsAvailable("brave_search"))
                                reSearchTasks.Add(Task.Run(async () =>
                                    await new BraveSearchService(httpClient, boot.ApiKeys!, boot.ApiBudget!, circuitBreaker)
                                        .SearchAsync(reSearchQuery, 10)));
                            if (boot.ApiKeys!.IsAvailable("serper"))
                                reSearchTasks.Add(Task.Run(async () =>
                                    await new SerperSearchService(httpClient, boot.ApiKeys!, boot.ApiBudget!, circuitBreaker)
                                        .SearchAsync(reSearchQuery, 10)));
                            if (boot.ApiKeys!.IsAvailable("tavily"))
                                reSearchTasks.Add(Task.Run(async () =>
                                    await new TavilySearchService(httpClient, boot.ApiKeys!, boot.ApiBudget!, circuitBreaker)
                                        .SearchAsync(reSearchQuery, 10)));
                            if (boot.ApiKeys!.IsAvailable("jina"))
                                reSearchTasks.Add(Task.Run(async () =>
                                    await new JinaSearchService(httpClient, boot.ApiKeys!, boot.ApiBudget!, circuitBreaker)
                                        .SearchAsync(reSearchQuery, 5)));
                            if (reSearchTasks.Count == 0)
                                reSearchTasks.Add(Task.Run(async () =>
                                    await new DuckDuckGoSearch(httpClient, circuitBreaker)
                                        .SearchAsync(reSearchQuery, 10)));

                            var reSearchBatches = await Task.WhenAll(reSearchTasks);
                            foreach (var batch in reSearchBatches)
                                reSearchResults.AddRange(batch);

                            if (reSearchResults.Count > 0)
                            {
                                // Embed and deduplicate new results
                                var existingIds = new HashSet<string>(uniqueItems.Select(i => i.Id));
                                var existingUrls = new HashSet<string>(
                                    uniqueItems.Where(i => !string.IsNullOrEmpty(i.Url))
                                        .Select(i => i.Url!.Split('?')[0].TrimEnd('/').ToLowerInvariant()),
                                    StringComparer.OrdinalIgnoreCase);
                                var existingTitles = new HashSet<string>(
                                    uniqueItems.Select(i => i.Title.ToLowerInvariant().Trim()),
                                    StringComparer.OrdinalIgnoreCase);
                                var newItems = reSearchResults.Where(i =>
                                    !existingIds.Contains(i.Id) &&
                                    (string.IsNullOrEmpty(i.Url) || !existingUrls.Contains(i.Url.Split('?')[0].TrimEnd('/').ToLowerInvariant())) &&
                                    !existingTitles.Contains(i.Title.ToLowerInvariant().Trim()))
                                    .ToList();

                                // Compute embeddings for new items
                                var newTexts = newItems
                                    .Select(item => $"{item.Title} {item.Content ?? ""}".Trim())
                                    .ToList();
                                var newEmbeddings = await boot.Embedding.EmbedBatchAsync(newTexts, cancellationToken);
                                for (var ei = 0; ei < newItems.Count; ei++)
                                    newItems[ei].Embedding = newEmbeddings[ei];

                                // Merge and re-score through the unified pipeline
                                uniqueItems.AddRange(newItems);
                                if (scoringOpts != null)
                                {
                                    var reScored = await scoringPipeline.ScoreItemsAsync(uniqueItems, scoringOpts, cancellationToken);
                                    uniqueItems = reScored.Items;
                                }

                                fetchTask.Description = $"[cyan]Re-search: +{newItems.Count} items[/]";
                                if (settings.DebugPipeline)
                                    AnsiConsole.MarkupLine(
                                        $"[grey]Re-search: {newItems.Count} new items merged, {uniqueItems.Count} total[/]");
                            }
                        }
                    }
                }

                // Stage 3: Deterministic signal analysis — no LLM
                // Segments, sentiment, topic all computed via ONNX embeddings and article processing.
                // The LLM is reserved for Stage 4 (synthesis) only.
                analyzedItems = new List<(string title, string summary, string topic, float sentiment, string url, double relevance)>();

                // Pre-compute anchor embeddings once for sentiment and topic inference
                var processor = await ItemProcessor.CreateAsync(boot.Embedding, boot.Storage, boot.EntityStore, cancellationToken);

                {
                    var itemsToAnalyze = uniqueItems.Take(settings.Limit).ToList();

                    // Split: items with existing summaries skip re-analysis
                    var alreadyAnalyzed = itemsToAnalyze
                        .Where(i => !string.IsNullOrEmpty(i.Summary) && i.Summary != i.Title)
                        .ToList();
                    var needsAnalysis = itemsToAnalyze
                        .Where(i => string.IsNullOrEmpty(i.Summary) || i.Summary == i.Title)
                        .ToList();

                    if (alreadyAnalyzed.Count > 0)
                        fetchTask.Description = $"[cyan]Cached: {alreadyAnalyzed.Count} items[/]";
                    if (alreadyAnalyzed.Count > 0 && settings.DebugPipeline)
                        AnsiConsole.MarkupLine($"[grey]Using cached analyses for {alreadyAnalyzed.Count} previously processed items[/]");

                    foreach (var item in alreadyAnalyzed)
                    {
                        analyzedItems.Add((item.Title, item.Summary ?? item.Title, item.DetectedTopic ?? "general",
                            item.SentimentScore, item.Url ?? "", item.RelevanceScore));
                    }

                    var analyzeTask = ctx.AddTask("[cyan]Analyzing content[/]", maxValue: Math.Max(1, needsAnalysis.Count));
                    if (needsAnalysis.Count == 0)
                    {
                        analyzeTask.Value = 1;
                        analyzeTask.Description = $"[green]Analyzed {analyzedItems.Count} items ({alreadyAnalyzed.Count} cached)[/]";
                    }
                    else
                    {
                        // Phase 1: Segment extraction via ArticleProcessor (CPU-bound)
                        // ONNX InferenceSession.Run() is thread-safe — parallelize article processing
                        using var articleProcessor = new ArticleProcessor();

                        var parallelOpts = new ParallelOptions
                        {
                            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4)
                        };

                        await Parallel.ForEachAsync(needsAnalysis, parallelOpts, async (item, ct) =>
                        {
                            try
                            {
                                var processed = await articleProcessor.ProcessAsync(item, ct);

                                // Summary from top salience segments (deterministic, no LLM)
                                var topSegments = processed.TopSegments
                                    .OrderByDescending(s => s.SalienceScore)
                                    .Take(3)
                                    .ToList();
                                item.Summary = topSegments.Count > 0
                                    ? string.Join(" ", topSegments.Select(s =>
                                        s.Text.Length > 200 ? s.Text[..200] : s.Text))
                                    : (item.Content?.Length > 300
                                        ? item.Content[..300] + "..."
                                        : item.Content ?? item.Title);

                                // Structural analysis
                                if (item.Content != null)
                                    item.ContentStructure = MarkdownContentAnalyzer.Analyze(item.Content);
                            }
                            catch
                            {
                                // Segmentation failed — use content truncation
                                var content = item.Content ?? "";
                                item.Summary = content.Length > 300
                                    ? content[..300] + "..."
                                    : (content.Length > 0 ? content : item.Title);
                            }

                            // Phase 2: Embedding-based sentiment + topic (pure math, thread-safe)
                            processor.ScoreSentimentAndTopic(item);

                            analyzeTask.Increment(1);
                        });

                        // Build analyzedItems after parallel completion (preserves order)
                        foreach (var item in needsAnalysis)
                        {
                            analyzedItems.Add((item.Title, item.Summary ?? item.Title, item.DetectedTopic ?? "general",
                                item.SentimentScore, item.Url ?? "", item.RelevanceScore));
                        }
                    }

                    // Save to storage + index into FTS5 for keyword pre-filtering
                    // Batch all writes in a single SQLite transaction for performance
                    await processor.IndexBatchAsync(itemsToAnalyze);

                    analyzeTask.Description = $"[green]Analyzed {analyzedItems.Count} items (deterministic)[/]";
                }

                // Debug: Show enriched signals
                if (settings.DebugPipeline && analyzedItems.Count > 0)
                {
                    AnsiConsole.WriteLine();
                    var table = new Table()
                        .Title("[bold yellow]Signal Enrichment: Sentiment + Topic + Relevance[/]")
                        .Border(TableBorder.Rounded)
                        .AddColumn("[cyan]#[/]")
                        .AddColumn("[cyan]Source[/]")
                        .AddColumn("[cyan]Topic[/]")
                        .AddColumn("[cyan]Sent[/]")
                        .AddColumn("[cyan]RRF[/]")
                        .AddColumn("[cyan]Title[/]")
                        .AddColumn("[cyan]Snippet[/]");

                    var rank = 1;
                    foreach (var item in analyzedItems.OrderByDescending(i => i.relevance).Take(20))
                    {
                        var sentColor = item.sentiment > 0.1f ? "green" : item.sentiment < -0.1f ? "red" : "grey";
                        var snippet = item.summary.Length > 60
                            ? item.summary[..57] + "..."
                            : item.summary;
                        // Remove newlines from snippet
                        snippet = snippet.Replace("\n", " ").Replace("\r", "");

                        table.AddRow(
                            $"{rank++}",
                            Markup.Escape(GetSourceFromUrl(item.url)),
                            $"[bold]{Markup.Escape(item.topic)}[/]",
                            $"[{sentColor}]{item.sentiment:F2}[/]",
                            $"{item.relevance:F3}",
                            Markup.Escape(item.title.Length > 40 ? item.title[..37] + "..." : item.title),
                            Markup.Escape(snippet));
                    }
                    AnsiConsole.Write(table);

                    // Show structural analysis for enriched items
                    var enrichedWithStructure = uniqueItems
                        .Where(i => i.IsEnriched && i.ContentStructure != null)
                        .OrderByDescending(i => i.RelevanceScore)
                        .Take(10)
                        .ToList();

                    if (enrichedWithStructure.Count > 0)
                    {
                        AnsiConsole.WriteLine();
                        var structTable = new Table()
                            .Title("[bold yellow]Structural Analysis (Enriched Articles)[/]")
                            .Border(TableBorder.Rounded)
                            .AddColumn("[cyan]Source[/]")
                            .AddColumn("[cyan]Type[/]")
                            .AddColumn("[cyan]Quality[/]")
                            .AddColumn("[cyan]Structure[/]")
                            .AddColumn("[cyan]Title[/]");

                        foreach (var item in enrichedWithStructure)
                        {
                            var s = item.ContentStructure!;
                            var qColor = s.QualityScore > 0.5 ? "green" : s.QualityScore > 0.25 ? "yellow" : "red";
                            structTable.AddRow(
                                Markup.Escape(GetSourceFromUrl(item.Url ?? "")),
                                $"[bold]{Markup.Escape(s.ContentType)}[/]",
                                $"[{qColor}]{s.QualityScore:F2}[/]",
                                Markup.Escape(s.ToSummary()),
                                Markup.Escape(item.Title.Length > 50 ? item.Title[..47] + "..." : item.Title));
                        }
                        AnsiConsole.Write(structTable);
                    }
                }

                // NER entity extraction (--entities or --graph)
                allEntities = new List<NerEntity>();
                articleEntityMap = new List<(ContentItem item, List<NerEntity> entities)>();
                // Auto-enable entity extraction when GraphScope is Global or Connective (GraphRAG scope detection)
                extractEntities = settings.Entities
                    || (interpreted?.GraphScope is GraphScope.Global or GraphScope.Connective);

                if (extractEntities)
                {
                    var nerTask = ctx.AddTask("[cyan]Extracting entities[/]", maxValue: analyzedItems.Count);
                    using var nerService = new NerService();

                    if (nerService.IsAvailable)
                    {
                        await nerService.InitializeAsync();
                        foreach (var item in analyzedItems)
                        {
                            var textForNer = $"{item.title} {item.summary}";
                            var entities = await nerService.ExtractEntitiesAsync(textForNer);
                            allEntities.AddRange(entities);

                            // Track per-article entities for knowledge graph
                            var matchingContentItem = uniqueItems.FirstOrDefault(u =>
                                string.Equals(u.Title, item.title, StringComparison.Ordinal));
                            if (matchingContentItem != null && entities.Count > 0)
                            {
                                articleEntityMap.Add((matchingContentItem, entities));
                            }

                            nerTask.Increment(1);
                        }
                        // Dedupe entities for display
                        allEntities = allEntities
                            .GroupBy(e => e.Text.ToLowerInvariant())
                            .Select(g => g.OrderByDescending(e => e.Confidence).First())
                            .OrderByDescending(e => e.Confidence)
                            .ToList();
                    }
                    nerTask.Description = $"[green]Found {allEntities.Count} entities[/]";

                    // Persist entities to SQLite for future runs (enriches theme briefing without --entities)
                    if (articleEntityMap.Count > 0)
                    {
                        foreach (var (ci, ents) in articleEntityMap)
                        {
                            await processor.PersistEntitiesAsync(ci, ents);
                        }
                    }
                }

                // Layer 3: Graph enrichment — discover related documents via entity similarity
                // Uses entity profile HNSW when available (semantic entity matching),
                // falls back to SQL entity count when entity profiles don't exist yet.
                // Enabled when: (a) --entities flag is set, OR (b) entity profiles exist in KB
                var hasEntityProfiles = boot.EntityStore != null && await boot.EntityStore.HasEntityProfilesAsync();
                if ((extractEntities || hasEntityProfiles) && uniqueItems.Count >= 3)
                {
                    var topItemIds = uniqueItems
                        .OrderByDescending(i => i.RelevanceScore)
                        .Take(5)
                        .Select(i => i.Id)
                        .ToList();

                    var existingIds = new HashSet<string>(uniqueItems.Select(i => i.Id));
                    List<string> relatedIds;
                    var enrichmentMethod = "entities";

                    // Prefer entity profile HNSW when available (O(log N) semantic matching)
                    if (hasEntityProfiles && boot.VectorStore != null && boot.EntityStore != null)
                    {
                        var entityProfileService = new EntityProfileService(boot.Embedding, boot.EntityStore!);
                        var graphService = new KnowledgeGraphService(boot.VectorStore, boot.EntityStore, entityProfileService);
                        var related = await graphService.FindRelatedByEntityProfileAsync(
                            topItemIds, topK: 3, minSimilarity: 0.3f);
                        relatedIds = related
                            .Where(r => !existingIds.Contains(r.itemId))
                            .Select(r => r.itemId)
                            .ToList();
                        enrichmentMethod = "entity profile HNSW";

                        if (settings.DebugPipeline && related.Count > 0)
                        {
                            AnsiConsole.MarkupLine($"[grey]Entity profile HNSW: found {related.Count} candidates[/]");
                            foreach (var (itemId, title, sim) in related.Take(5))
                            {
                                var truncTitle = title.Length > 40 ? title[..37] + "..." : title;
                                AnsiConsole.MarkupLine($"[grey]  ⤷ {Markup.Escape(truncTitle)}: {sim:F3}[/]");
                            }
                        }
                    }
                    else
                    {
                        // Fallback: SQL-based shared entity count (legacy O(N²) approach)
                        relatedIds = await boot.Storage.FindRelatedByEntitiesAsync(
                            topItemIds, excludeIds: existingIds.ToList(), limit: 3);
                    }

                    if (relatedIds.Count > 0)
                    {
                        var relatedItems = await boot.Storage.LoadItemsByIdsAsync(relatedIds);
                        // Assign slightly lower relevance so they appear after scored items
                        var lowestScore = uniqueItems.Count > 0
                            ? uniqueItems.Min(i => i.RelevanceScore)
                            : 0.1;
                        foreach (var item in relatedItems)
                        {
                            if (!existingIds.Contains(item.Id))
                            {
                                var enriched = item with { Source = item.Source + " (via entities)" };
                                enriched.RelevanceScore = lowestScore * 0.9;
                                uniqueItems.Add(enriched);
                                existingIds.Add(item.Id);
                            }
                        }

                        if (relatedItems.Count > 0)
                            fetchTask.Description = $"[cyan]Graph: +{relatedItems.Count} related[/]";
                        if (settings.DebugPipeline && relatedItems.Count > 0)
                            AnsiConsole.MarkupLine($"[grey]Graph enrichment ({enrichmentMethod}): +{relatedItems.Count} items[/]");
                    }
                }

                // Index item embeddings into DuckDB for HNSW similarity search (skip in --no-llm fast mode)
                if (settings.Graph && boot.VectorStore != null && boot.EntityStore != null)
                {
                    var indexTask = ctx.AddTask("[cyan]Indexing embeddings[/]", maxValue: 100);
                    var graphService = new KnowledgeGraphService(boot.VectorStore, boot.EntityStore);
                    var itemsWithEmbeddings = uniqueItems
                        .Where(i => i.Embedding != null)
                        .Take(settings.Limit)
                        .ToList();
                    await graphService.IndexItemEmbeddingsAsync(itemsWithEmbeddings);
                    indexTask.Value = 100;
                    indexTask.Description = $"[green]Indexed {itemsWithEmbeddings.Count} embeddings[/]";
                }

                // Ingest entities into knowledge graph (with entity profiles for HNSW search)
                if (settings.Graph && boot.VectorStore != null && boot.EntityStore != null && articleEntityMap.Count > 0)
                {
                    var graphTask = ctx.AddTask("[cyan]Building knowledge graph[/]", maxValue: 100);
                    var entityProfileService = new EntityProfileService(boot.Embedding, boot.EntityStore!);
                    var graphService = new KnowledgeGraphService(boot.VectorStore, boot.EntityStore, entityProfileService);
                    await graphService.IngestEntitiesAsync(articleEntityMap);

                    // Ingest linked page entities with lower confidence
                    foreach (var (item, _) in articleEntityMap)
                    {
                        if (item.LinkedPages.Count > 0)
                        {
                            using var linkedNer = new NerService();
                            if (linkedNer.IsAvailable)
                            {
                                await linkedNer.InitializeAsync();
                                foreach (var linked in item.LinkedPages)
                                {
                                    var linkedEntities = await linkedNer.ExtractEntitiesAsync(
                                        $"{linked.Title} {linked.Content}");
                                    if (linkedEntities.Count > 0)
                                    {
                                        await graphService.IngestLinkedPageEntitiesAsync(
                                            item, linkedEntities, linked.Url);
                                    }
                                }
                            }
                        }
                    }

                    graphTask.Value = 100;
                    var (ec, rc, mc, ic) = await boot.EntityStore!.GetStatsAsync();
                    graphTask.Description = $"[green]Graph: {ec} entities, {rc} relationships, {ic} items[/]";
                }

                // Stage 4: Generate summary
                var summaryTask = ctx.AddTask("[cyan]Generating summary[/]", maxValue: 100);
                template = settings.Template.ToLowerInvariant();
                isBlogTemplate = template is "blog-article" or "blog-timeline"
                    or "blog-newsletter" or "blog-newsletter-html";

                // finalSummary and templateData already declared outside lambda
                templateData = null;

                if (ollamaAvailable && analyzedItems.Count > 0)
                {
                    summaryTask.Value = 10;
                    var userQuery = interpreted?.RawPrompt ?? settings.Prompt;

                    // Composite query handling: enhance userQuery to explicitly address each subquery
                    // This ensures the summarizer answers each part of the composite question
                    if (interpreted?.SentinelIntent?.HasSubqueries == true)
                    {
                        var subqs = interpreted.SentinelIntent.Subqueries!;
                        var subqList = string.Join("\n", subqs.Select((sq, i) => $"  {i + 1}. {sq}"));
                        userQuery = $"""
                            {userQuery}

                            IMPORTANT: This is a composite question. Please answer EACH of these sub-questions:
                            {subqList}

                            Structure your response to clearly address each question.
                            """;
                    }

                    // Detect query type for source quality weighting and template auto-selection.
                    // Use sentinel intent when available — the LLM is better at distinguishing
                    // QA from roundup (e.g., "What's the SNL host this week?" is QA, not roundup).
                    var detectedQueryType = QueryTypeDetector.Detect(userQuery, interpreted?.SentinelIntent);

                    // Apply source quality multipliers based on query type
                    if (detectedQueryType is QueryType.Timeline or QueryType.Explainer
                            or QueryType.Roundup)
                    {
                        var qualityAdjusted = 0;
                        foreach (var item in uniqueItems)
                        {
                            var multiplier = QueryTypeDetector.GetSourceQualityMultiplier(
                                detectedQueryType, item.Url);
                            if (Math.Abs(multiplier - 1.0) > 0.01)
                            {
                                item.RelevanceScore *= multiplier;
                                qualityAdjusted++;
                            }
                        }
                        if (qualityAdjusted > 0)
                        {
                            var sorted = uniqueItems.OrderByDescending(i => i.RelevanceScore).ToList();
                            uniqueItems.Clear();
                            uniqueItems.AddRange(sorted);
                            // Also re-sort analyzedItems to match
                            analyzedItems = analyzedItems
                                .OrderByDescending(a => a.relevance)
                                .ToList();

                            summaryTask.Description = $"[cyan]Quality: {qualityAdjusted} adjusted[/]";
                            if (settings.DebugPipeline)
                                AnsiConsole.MarkupLine(
                                    $"[grey]Source quality ({detectedQueryType}): {qualityAdjusted} items adjusted[/]");
                        }
                    }

                    summaryTask.Value = 20;

                    // Resolve YAML template definition (if any)
                    var templateDef = outputTemplates.GetDefinition(template);
                    var effectiveBase = templateDef?.BaseTemplate ?? template;
                    var isBlogArticle = effectiveBase is "blog-article" or "blog-timeline"
                                        || template is "blog-article" or "blog-timeline";

                    // Route to appropriate synthesis based on template
                    if (isBlogArticle)
                    {
                        // Force timeline for blog-timeline, otherwise auto-detect
                        var articleQueryType = effectiveBase == "blog-timeline" || template == "blog-timeline"
                            ? QueryType.Timeline
                            : detectedQueryType;

                        BlogArticleResult blogResult;
                        using (var articleProcessor = new ArticleProcessor())
                        {
                            var generator = new LongFormDocumentGenerator(
                                ollama, articleProcessor);
                            blogResult = await generator.GenerateAsync(
                                analyzedItems, uniqueItems,
                                userQuery ?? "topic overview",
                                vibe, vibePrompt, articleQueryType,
                                templateDef, cancellationToken,
                                parallel: settings.Parallel);
                        }

                        // Build template data
                        templateData = new DigestData
                        {
                            Date = DateTimeOffset.Now,
                            Vibe = vibe,
                            Query = userQuery,
                            ArticleTitle = blogResult.Title,
                            Introduction = blogResult.Introduction,
                            Sections = blogResult.Sections
                                .Select(s => new DigestSection(s.Heading, s.Content, s.SourceUrls))
                                .ToList(),
                            Conclusion = blogResult.Conclusion,
                            SourceUrls = blogResult.SourceUrls,
                            Items = analyzedItems.Select(a => new DigestItem
                            {
                                Title = a.title,
                                Url = a.url,
                                Summary = a.summary,
                                Topic = a.topic,
                                Sentiment = a.sentiment
                            }).ToList()
                        };

                        // Use YAML template's own Liquid template if registered, else base template
                        var renderTemplate = templateDef?.Template != null ? template
                            : effectiveBase == "blog-timeline" ? "blog-timeline"
                            : "blog-article";
                        finalSummary = outputTemplates.Render(templateData, renderTemplate);
                    }
                    else if (template is "blog-newsletter" or "blog-newsletter-html")
                    {
                        var newsletterResult = await ollama.SynthesizeNewsletterAsync(
                            analyzedItems, vibe, vibePrompt,
                            userQuery,
                            uniqueItems, text => boot.Embedding.EmbedAsync(text).GetAwaiter().GetResult(), cancellationToken);

                        templateData = new DigestData
                        {
                            Date = DateTimeOffset.Now,
                            Vibe = vibe,
                            Query = userQuery,
                            Introduction = newsletterResult.Introduction,
                            TopPicks = newsletterResult.TopPicks
                                .Select(p => new DigestPick(p.Title, p.Url, p.Commentary, p.Source))
                                .ToList(),
                            QuickHits = newsletterResult.QuickHits
                                .Select(q => new DigestQuickHit(q.Title, q.Url, q.OneLiner))
                                .ToList(),
                            SignOff = newsletterResult.SignOff,
                            Items = analyzedItems.Select(a => new DigestItem
                            {
                                Title = a.title,
                                Url = a.url,
                                Summary = a.summary,
                                Topic = a.topic,
                                Sentiment = a.sentiment
                            }).ToList()
                        };

                        finalSummary = outputTemplates.Render(templateData,
                            template == "blog-newsletter-html" ? "blog-newsletter-html" : "blog-newsletter");
                    }
                    else
                    {
                        // Standard synthesis path
                        summaryTask.Value = 50;

                        // Entity disambiguation: detect ambiguous entities in top items
                        // Only apply for research/qa queries (entity lookups), not news/roundups
                        var sentinelIntent = interpreted?.SentinelIntent?.Intent ?? "";
                        var isEntityQuery = sentinelIntent is "research" or "qa";
                        if (isEntityQuery && !string.IsNullOrWhiteSpace(userQuery)
                            && detectedQueryType != QueryType.Roundup)
                        {
                            var topForDisambig = uniqueItems
                                .OrderByDescending(i => i.RelevanceScore)
                                .Take(settings.Limit)
                                .ToList();

                            var disambiguator = new EntityDisambiguationService();
                            var disambiguation = await disambiguator.DisambiguateFastAsync(
                                topForDisambig, userQuery, boot.Embedding, boot.Storage);

                            // Filter out clusters irrelevant to the query — prevents e.g.
                            // "Artificial Intelligence" clusters appearing in "strawberry prices" queries
                            if (disambiguation.IsAmbiguous && disambiguation.Clusters.Count >= 2 && queryEmbedding != null)
                            {
                                var relevantClusters = disambiguation.Clusters
                                    .Where(c =>
                                    {
                                        if (c.Items.Count == 0) return false;
                                        var topItem = c.Items.OrderByDescending(i => i.RelevanceScore).First();
                                        if (topItem.Embedding == null) return true; // can't filter without embedding
                                        // Multi-query: use max similarity across subqueries for composite queries
                                        var sim = ComputeMaxQuerySimilarity(topItem.Embedding, queryEmbedding, subqueryEmbeddings);
                                        return sim >= 0.35f; // minimum topical relevance to query
                                    })
                                    .ToList();

                                if (relevantClusters.Count >= 2)
                                {
                                    var entityLines = relevantClusters
                                        .Select(c => $"- Entity: \"{c.Label}\"")
                                        .ToList();

                                    userQuery = $"""
                                        IMPORTANT: Evidence contains distinct entities with similar names.
                                        Summarize EACH entity separately under its own heading:
                                        {string.Join("\n", entityLines)}
                                        Do NOT conflate these into one entity.

                                        ORIGINAL QUERY: {userQuery}
                                        """;
                                }
                            }
                        }

                        finalSummary = await ollama.SynthesizeSummaryAsync(
                            analyzedItems, vibe, vibePrompt, userQuery, uniqueItems,
                            embedder: text => boot.Embedding.EmbedAsync(text).GetAwaiter().GetResult());
                    }
                }
                else
                {
                    finalSummary = GenerateFallbackSummary(analyzedItems, vibe);
                }

                summaryTask.Value = 100;
                summaryTask.Description = isBlogTemplate
                    ? $"[green]{template} generated[/]"
                    : "[green]Summary generated[/]";

                // Save summary
                await boot.Storage.SaveSummaryAsync(vibe, finalSummary, analyzedItems.Count);

                // Log query for segment reuse (LFU tracking + similar query matching)
                if (!string.IsNullOrWhiteSpace(queryText))
                {
                    var returnedIds = uniqueItems.Take(settings.Limit).Select(i => i.Id).ToList();
                    var logEmbedding = earlyQueryEmbedding ?? (queryText.Length > 0 ? await boot.Embedding.EmbedAsync(queryText, cancellationToken) : null);
                    await boot.Storage.LogQueryAsync(queryText, logEmbedding, vibe, returnedIds);
                }

                // Cleanup old data (before Progress ends)
                await boot.Storage.CleanupOldDataAsync(boot.Config.Storage.RetentionDays);
                if (boot.VectorStore != null)
                    await boot.VectorStore.CleanupAsync(boot.Config.Storage.RetentionDays);
                if (boot.EntityStore != null)
                    await boot.EntityStore.CleanupAsync(boot.Config.Storage.RetentionDays);
            });

        // === Output (outside Progress block — no more progress bar overlap) ===
        if (settings.Json)
        {
            // Token-efficient structured JSON for LLM tool / agent consumption.
            // Optimized to be useful as a tool response: compact, fact-dense,
            // source-attributed, no markdown noise.
            // Tiers: "full" (LLM synthesis), "signals" (--nollm, ONNX-only).

            var jsonQuery = interpreted?.RawPrompt ?? settings.Prompt;

            // Per-item TextRank excerpts (most informative sentences, no LLM needed)
            var itemExcerpts = new Dictionary<string, string>();
            var keyFactCandidates = new List<(string fact, double relevance, string title, string url)>();

            foreach (var item in uniqueItems.Take(settings.Limit))
            {
                var content = item.Content ?? "";
                if (content.Length > 200)
                {
                    try
                    {
                        var excerpt = StripMarkdownForLlm(
                            TextRankExtractor.ExtractKeySentences(
                                content, text => boot.Embedding.EmbedAsync(text).GetAwaiter().GetResult(), maxChars: 400));
                        itemExcerpts[item.Id] = excerpt;

                        // Top-relevance articles contribute their lead fact
                        if (item.RelevanceScore > 0.3)
                        {
                            var dotIdx = excerpt.IndexOf(". ", StringComparison.Ordinal);
                            var leadFact = dotIdx > 20 ? excerpt[..(dotIdx + 1)] : excerpt;
                            if (leadFact.Length > 250) leadFact = leadFact[..250] + "...";
                            keyFactCandidates.Add((leadFact, item.RelevanceScore,
                                item.Title, item.Url ?? ""));
                        }
                    }
                    catch
                    {
                        itemExcerpts[item.Id] = StripMarkdownForLlm(
                            content.Length > 400 ? content[..400] + "..." : content);
                    }
                }
                else if (content.Length > 0)
                {
                    itemExcerpts[item.Id] = StripMarkdownForLlm(content);
                }
            }

            // Cross-article key facts: source-attributed, one per top article
            var keyFacts = keyFactCandidates
                .OrderByDescending(k => k.relevance)
                .Take(7)
                .Select(k => new
                {
                    fact = k.fact,
                    source = GetSourceFromUrl(k.url),
                    url = k.url
                })
                .ToArray();

            // Source diversity
            var itemsForStats = uniqueItems.Take(settings.Limit).ToList();
            var sourceDistribution = itemsForStats
                .GroupBy(i => i.Source)
                .ToDictionary(g => g.Key, g => g.Count());

            var sentimentBreakdown = new
            {
                positive = analyzedItems.Count(i => i.sentiment > 0.15f),
                neutral = analyzedItems.Count(i => i.sentiment is >= -0.15f and <= 0.15f),
                negative = analyzedItems.Count(i => i.sentiment < -0.15f)
            };

            var themeData = ExtractKeyThemes(analyzedItems, uniqueItems);

            // In signals mode, replace verbose fallback summary with compact version
            var jsonSummary = ollamaAvailable
                ? finalSummary
                : (keyFacts.Length > 0
                    ? string.Join(" ", keyFacts.Select(f => f.fact))
                    : $"Found {analyzedItems.Count} items for \"{jsonQuery}\".");

            var jsonOutput = new
            {
                meta = new
                {
                    query = jsonQuery,
                    vibe,
                    generated = DateTimeOffset.UtcNow,
                    pipeline = ollamaAvailable ? "full" : "signals",
                    itemCount = analyzedItems.Count,
                    sources = new
                    {
                        unique = sourceDistribution.Count,
                        distribution = sourceDistribution
                    },
                    sentiment = sentimentBreakdown,
                    cache = linkCacheHits > 0 || linksSkippedByRelevance > 0
                        ? new { hits = linkCacheHits, irrelevantSkipped = linksSkippedByRelevance }
                        : null
                },
                summary = jsonSummary,
                keyFacts,
                themes = new
                {
                    topics = themeData.topics.Select(tp => new { tp.topic, tp.count }).ToArray(),
                    keyTerms = themeData.terms.Select(tr => new { tr.term, tr.articles }).ToArray()
                },
                items = analyzedItems.Select(i =>
                {
                    var contentItem = uniqueItems.FirstOrDefault(u =>
                        string.Equals(u.Title, i.title, StringComparison.Ordinal));
                    var hasExcerpt = contentItem != null
                        && itemExcerpts.TryGetValue(contentItem.Id, out var ex);
                    return new
                    {
                        i.title,
                        i.url,
                        source = contentItem?.Source ?? GetSourceFromUrl(i.url),
                        i.topic,
                        i.sentiment,
                        i.relevance,
                        quality = contentItem?.ContentStructure?.QualityScore,
                        excerpt = hasExcerpt
                            ? itemExcerpts[contentItem!.Id]
                            : StripMarkdownForLlm(
                                i.summary.Length > 400 ? i.summary[..400] + "..." : i.summary),
                        linkedCount = contentItem?.LinkedPages.Count ?? 0
                    };
                }).ToArray(),
                entities = extractEntities ? allEntities.Select(e => new
                {
                    text = e.Text,
                    type = e.Type,
                    confidence = e.Confidence
                }).ToArray() : null
            };
            var json = System.Text.Json.JsonSerializer.Serialize(jsonOutput,
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                });

            if (!string.IsNullOrEmpty(settings.Output))
            {
                await File.WriteAllTextAsync(settings.Output, json);
                if (!settings.Quiet)
                    AnsiConsole.MarkupLine($"[green]JSON saved to:[/] {settings.Output}");
            }
            else
            {
                Console.WriteLine(json);
            }
        }
        else if (!string.IsNullOrEmpty(settings.Output))
        {
            await File.WriteAllTextAsync(settings.Output, finalSummary);
            AnsiConsole.MarkupLine($"[green]Summary saved to:[/] {settings.Output}");
        }
        else
        {
            AnsiConsole.WriteLine();
            var renderedMarkup = MarkdownToSpectre(finalSummary);
            var header = isBlogTemplate
                ? $"[bold cyan]{Markup.Escape(template)}[/]"
                : $"[bold cyan]Doom Scroll Digest ({vibe})[/]";
            // Word-wrap content to prevent the panel from stretching to full terminal width
            var maxContentWidth = Math.Min(AnsiConsole.Profile.Width - 6, 94);
            var wrappedMarkup = WordWrapMarkup(renderedMarkup, maxContentWidth);
            AnsiConsole.Write(new Panel(wrappedMarkup)
                .Header(header)
                .Border(BoxBorder.Rounded)
                .Padding(1, 1));

            // Deterministic sources section — shows which documents were used
            RenderSourcesUsed(analyzedItems, uniqueItems, maxContentWidth);

            // Display evidence briefing with named themes (opt-in via --briefing)
            if (settings.Briefing && analyzedItems.Count > 0)
            {
                // Load entity data for enriched theme briefing
                Dictionary<string, List<(string name, string type, double confidence)>>? itemEntities = null;

                if (articleEntityMap.Count > 0)
                {
                    // Use NER entities from this session
                    itemEntities = articleEntityMap.ToDictionary(
                        ae => ae.item.Id,
                        ae => ae.entities.Select(e => (e.Text, e.Type, (double)e.Confidence)).ToList(),
                        StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    // Query stored entities from previous runs (knowledge graph)
                    var itemIds = uniqueItems.Select(u => u.Id).ToList();
                    itemEntities = await boot.Storage.GetEntitiesForItemsAsync(itemIds);
                }

                var briefing = ExtractThemeBriefing(analyzedItems, uniqueItems, itemEntities);
                if (briefing.Themes.Count > 0)
                {
                    AnsiConsole.WriteLine();
                    var briefingParts = new List<string>();

                    // Corpus coverage line
                    var entityNote = briefing.HasGraphEntities
                        ? $", {briefing.GraphEntityCount} graph entities"
                        : "";
                    var coverageNote = $"[dim]Themes inferred from {briefing.TotalEvidenceItems} evidence items across {briefing.SourceCount} sources{entityNote} (coverage: {briefing.CoveragePercent}%).[/]";
                    briefingParts.Add(coverageNote);
                    var methodNote = briefing.HasGraphEntities
                        ? "[dim]Entity-graph enriched; RRF + in-corpus PageRank; diversity decay applied.[/]"
                        : "[dim]Selected by RRF + in-corpus PageRank; diversity decay applied.[/]";
                    briefingParts.Add(methodNote);
                    briefingParts.Add("");

                    // Named themes with evidence counts and snippets
                    foreach (var theme in briefing.Themes)
                    {
                        var color = theme.TopicLabel.ToLowerInvariant() switch
                        {
                            "technology" => "blue",
                            "ai" or "machine_learning" => "magenta",
                            "security" => "red",
                            "science" => "cyan",
                            "health" => "green",
                            "business" or "economy" => "yellow",
                            "politics" => "red",
                            "world" => "aqua",
                            "entertainment" or "humor" => "fuchsia",
                            "climate" or "environment" => "green",
                            "space" => "blue",
                            _ => "grey"
                        };

                        var eids = theme.EvidenceIds.Count > 0
                            ? " " + string.Join(", ", theme.EvidenceIds.Take(5).Select(id => $"[dim]E{id}[/]"))
                            : "";
                        briefingParts.Add(
                            $"[{color}]■[/] [bold]{Markup.Escape(theme.ThesisName)}[/] [dim]({theme.SegmentCount} segments)[/]{eids}");

                        // Supporting snippets
                        foreach (var (snippet, eid) in theme.Snippets)
                        {
                            var tag = eid.HasValue ? $" [dim][[E{eid.Value}]][/]" : "";
                            var truncSnippet = snippet.Length > 90 ? snippet[..87] + "..." : snippet;
                            briefingParts.Add($"  [italic dim]\"{Markup.Escape(truncSnippet)}\"[/]{tag}");
                        }

                        // Show entities: typed NER entities when available, else key terms
                        if (theme.GraphEntities.Count > 0)
                        {
                            var typedEntities = string.Join(", ", theme.GraphEntities.Take(6).Select(e =>
                            {
                                var typeColor = e.type switch
                                {
                                    "PER" => "green",
                                    "ORG" => "blue",
                                    "LOC" => "yellow",
                                    _ => "grey"
                                };
                                var typeLabel = e.type switch
                                {
                                    "PER" => "person",
                                    "ORG" => "org",
                                    "LOC" => "loc",
                                    _ => "misc"
                                };
                                return $"[{typeColor}]{Markup.Escape(e.name)}[/][dim]:{typeLabel}[/]";
                            }));
                            briefingParts.Add($"  {typedEntities}");
                        }
                        else if (theme.KeyTerms.Count > 0)
                        {
                            var terms = string.Join(", ", theme.KeyTerms.Select(t => Markup.Escape(t)));
                            briefingParts.Add($"  [dim]Terms: {terms}[/]");
                        }

                        briefingParts.Add("");
                    }

                    // Missing themes / outliers
                    if (briefing.MissingTopics.Count > 0)
                    {
                        var missing = string.Join(", ", briefing.MissingTopics.Select(t =>
                            Markup.Escape(char.ToUpper(t[0]) + t[1..])));
                        briefingParts.Add($"[dim]Not strongly represented: {missing}[/]");
                    }

                    var briefingContent = string.Join("\n", briefingParts).TrimEnd('\n');
                    var wrappedBriefing = WordWrapMarkup(briefingContent, maxContentWidth);
                    AnsiConsole.Write(new Panel(wrappedBriefing)
                        .Header("[bold yellow]Evidence Briefing[/]")
                        .Border(BoxBorder.Rounded)
                        .Padding(1, 0));
                }
            }

            // Display entities if requested
            if (extractEntities && allEntities.Count > 0)
            {
                AnsiConsole.WriteLine();
                var entityTable = new Table()
                    .Title("[bold yellow]Named Entities[/]")
                    .Border(TableBorder.Rounded)
                    .AddColumn("[cyan]Entity[/]")
                    .AddColumn("[cyan]Type[/]")
                    .AddColumn("[cyan]Confidence[/]");

                foreach (var entity in allEntities.Take(20))
                {
                    var typeColor = entity.Type switch
                    {
                        "PER" => "green",
                        "ORG" => "blue",
                        "LOC" => "yellow",
                        _ => "grey"
                    };
                    entityTable.AddRow(
                        Markup.Escape(entity.Text),
                        $"[{typeColor}]{entity.Type}[/]",
                        $"{entity.Confidence:P0}");
                }
                AnsiConsole.Write(entityTable);

                // Story Connections: show articles linked by shared entities
                if (articleEntityMap.Count >= 2)
                {
                    DisplayStoryConnections(articleEntityMap);
                }
            }

            // Display knowledge graph (skip in --no-llm fast mode)
            if (settings.Graph && boot.VectorStore != null && boot.EntityStore != null)
            {
                var graphService = new KnowledgeGraphService(boot.VectorStore, boot.EntityStore);
                await graphService.DisplayGraphAsync(topN: 15, daysBack: 7);
            }

            // Display images for important items
            if (settings.ShowImages)
            {
                // Get items with images, sorted by importance (score)
                var itemsWithImages = uniqueItems
                    .Where(i => !string.IsNullOrEmpty(i.ImageUrl))
                    .OrderByDescending(i => i.Score)
                    .Take(3)
                    .ToList();

                // Also try to fetch og:image for high-scoring items without images
                var highScoringWithoutImages = uniqueItems
                    .Where(i => string.IsNullOrEmpty(i.ImageUrl) && i.Score > 50 && !string.IsNullOrEmpty(i.Url))
                    .OrderByDescending(i => i.Score)
                    .Take(2);

                using var imageService = new ImageService(httpClient);

                foreach (var item in highScoringWithoutImages)
                {
                    var ogImage = await imageService.FetchOgImageAsync(item.Url!);
                    if (!string.IsNullOrEmpty(ogImage))
                    {
                        item.ImageUrl = ogImage;
                        itemsWithImages.Add(item);
                    }
                }

                if (itemsWithImages.Count > 0)
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[bold yellow]Featured Images[/]");
                    AnsiConsole.WriteLine();

                    foreach (var item in itemsWithImages.Take(3))
                    {
                        var localPath = await imageService.DownloadImageAsync(item.ImageUrl!, item.Id);
                        if (localPath != null)
                        {
                            AnsiConsole.MarkupLine($"[cyan]{Markup.Escape(item.Title)}[/]");
                            if (item.Score > 0)
                                AnsiConsole.MarkupLine($"[grey]Score: {item.Score}[/]");
                            imageService.DisplayImage(localPath, maxWidth: 50);
                            AnsiConsole.WriteLine();
                        }
                    }
                }
            }
        }

        // Send email if requested
        if (settings.SendEmail)
        {
            string emailHtml;
            if (templateData != null)
            {
                // Use full template rendering (blog/newsletter paths)
                emailHtml = outputTemplates.Render(templateData, boot.Config.Email.Template);
            }
            else
            {
                // Standard synthesis: build templateData from analyzed items
                // Convert markdown summary to HTML for email rendering
                var overviewHtml = Markdig.Markdown.ToHtml(finalSummary ?? "");
                templateData = new DigestData
                {
                    Date = DateTimeOffset.Now,
                    Vibe = vibe,
                    Query = interpreted?.RawPrompt ?? settings.Prompt,
                    Overview = overviewHtml,
                    Items = analyzedItems.Select(a => new DigestItem
                    {
                        Title = a.title,
                        Url = a.url,
                        Summary = a.summary,
                        Topic = a.topic,
                        Sentiment = a.sentiment
                    }).ToList()
                };
                emailHtml = outputTemplates.Render(templateData, boot.Config.Email.Template);
            }

            var emailService = new EmailService(boot.Config.Email, boot.ApiKeys!);
            var subject = boot.Config.Email.SubjectTemplate
                .Replace("{{DATE}}", DateTime.Now.ToString("MMMM d, yyyy"))
                .Replace("{{QUERY}}", interpreted?.RawPrompt ?? settings.Prompt ?? "");
            await emailService.SendAsync(emailHtml, subject, settings.EmailTo, cancellationToken);
        }

        return 0;
    }

    /// <summary>
    /// Play the DoomSummarizer easter egg animation with the title.
    /// </summary>
    private static async Task PlayEasterEggAnimationAsync(CancellationToken ct)
    {
        // Enable Windows ANSI support for proper color rendering
        ConsoleHelper.EnableAnsiSupport();

        AnsiConsole.Clear();
        AnsiConsole.WriteLine();

        var title = new FigletText("DoomSummarizer")
            .Color(Color.Cyan1);

        AnsiConsole.Write(title);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]AI-powered doom scrolling so you don't have to.[/]");
        AnsiConsole.WriteLine();

        // Try to load and play the embedded .cidz animation
        var doc = await LoadEmbeddedAnimationAsync(ct);

        if (doc != null)
        {
            AnsiConsole.MarkupLine("[dim]Press Ctrl+C to exit[/]");
            AnsiConsole.WriteLine();

            try
            {
                using var player = new ConsolePlayer(doc, loopCount: 3);
                await player.PlayAsync(ct);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[dim]Animation error: {ex.Message}[/]");
                await PlayInlineAnimationAsync(ct);
            }
        }
        else
        {
            // Fall back to inline ASCII animation
            await PlayInlineAnimationAsync(ct);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]Ready to doom scroll![/]");
    }

    /// <summary>
    /// Load the embedded spin.cidz animation from assembly resources.
    /// </summary>
    private static async Task<PlayerDocument?> LoadEmbeddedAnimationAsync(CancellationToken ct)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();

            // Try different resource name patterns
            var resourceNames = new[] { "DoomSummarizer.spin.cidz", "DoomSummarizer.img.spin.cidz", "spin.cidz" };
            Stream? stream = null;
            string? foundName = null;

            foreach (var name in resourceNames)
            {
                stream = assembly.GetManifestResourceStream(name);
                if (stream != null)
                {
                    foundName = name;
                    break;
                }
            }

            if (stream == null)
            {
                AnsiConsole.MarkupLine("[dim]No embedded animation found[/]");
                return null;
            }

            AnsiConsole.MarkupLine($"[dim]Loading animation from {foundName} ({stream.Length} bytes)[/]");

            await using (stream)
            {
                var doc = await PlayerDocument.FromCompressedStreamAsync(stream, ct);
                AnsiConsole.MarkupLine($"[dim]Loaded {doc.FrameCount} frames[/]");
                return doc;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[dim]Animation load error: {ex.Message}[/]");
            return null;
        }
    }

    /// <summary>
    /// Fallback inline ASCII animation when .cidz file is not available.
    /// </summary>
    private static async Task PlayInlineAnimationAsync(CancellationToken ct)
    {
        var frames = new[]
        {
            @"
   ████████████████████████
   ██                    ██
   ██  ████        ████  ██
   ██  ████        ████  ██
   ██                    ██
   ██       ████████     ██
   ██    ██  ████  ██    ██
   ██                    ██
   ████████████████████████
            ",
            @"
   ████████████████████████
   ██                    ██
   ██  ▓▓▓▓        ▓▓▓▓  ██
   ██  ▓▓▓▓        ▓▓▓▓  ██
   ██                    ██
   ██       ████████     ██
   ██    ██  ████  ██    ██
   ██                    ██
   ████████████████████████
            ",
            @"
   ████████████████████████
   ██                    ██
   ██  ░░░░        ░░░░  ██
   ██  ░░░░        ░░░░  ██
   ██                    ██
   ██       ████████     ██
   ██    ██  ████  ██    ██
   ██                    ██
   ████████████████████████
            "
        };

        var colors = new[] { Color.Red, Color.Orange1, Color.Yellow };

        AnsiConsole.MarkupLine("[dim]Press Ctrl+C to exit[/]");
        AnsiConsole.WriteLine();

        var loops = 0;
        var maxLoops = 6;
        var frameIndex = 0;

        while (!ct.IsCancellationRequested && loops < maxLoops)
        {
            var color = colors[frameIndex % colors.Length];
            var frame = frames[frameIndex % frames.Length];

            AnsiConsole.Cursor.SetPosition(0, 8);
            AnsiConsole.Write(new Text(frame, new Style(color)));

            frameIndex++;
            if (frameIndex >= frames.Length * 2)
            {
                frameIndex = 0;
                loops++;
            }

            try
            {
                await Task.Delay(150, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
