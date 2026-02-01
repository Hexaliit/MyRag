using System.Text;
using System.Threading.Channels;
using DoomSummarizer.Commands;
using DoomSummarizer.Helpers;
using DoomSummarizer.Models;
using DoomSummarizer.Services;
using LucidRAG.Decomposer.Analysis;
using LucidRAG.Decomposer.Models;
using LucidRAG.Decomposer.Orchestration;
using LucidRAG.Decomposer.Refinement;
using Mostlylucid.DocSummarizer.Services;
using Spectre.Console;

namespace DoomSummarizer.Services;

public record InteractiveAskOptions(
    IReadOnlyList<string>? Sources,
    string? Name,
    int Days,
    int TopK,
    bool Once,
    bool Quiet,
    string? InitialQuestion,
    ChannelReader<CrawlProgressUpdate>? CrawlProgress = null,
    Func<bool>? IsCrawlRunning = null,
    string? PromptTemplate = null)
{
    /// <summary>Convenience: single source.</summary>
    public InteractiveAskOptions(
        string? Source, string? Name, int Days, int TopK, bool Once, bool Quiet,
        string? InitialQuestion, ChannelReader<CrawlProgressUpdate>? CrawlProgress = null,
        Func<bool>? IsCrawlRunning = null, string? PromptTemplate = null)
        : this(
            Source != null ? new[] { Source } : null,
            Name, Days, TopK, Once, Quiet, InitialQuestion,
            CrawlProgress, IsCrawlRunning, PromptTemplate)
    { }
}

public sealed class InteractiveAskLoop
{
    private readonly CommandBootstrap _boot;
    private readonly OllamaService? _ollama;
    private readonly LlmRouter? _llmRouter;
    private readonly bool _ollamaAvailable;
    private readonly InteractiveAskOptions _options;

    public InteractiveAskLoop(
        CommandBootstrap boot,
        OllamaService? ollama,
        LlmRouter? llmRouter,
        bool ollamaAvailable,
        InteractiveAskOptions options)
    {
        _boot = boot;
        _ollama = ollama;
        _llmRouter = llmRouter;
        _ollamaAvailable = ollamaAvailable;
        _options = options;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var effectiveSources = SourceFilterSet.MergeNameAndSource(
            _options.Name, _options.Sources);

        var retrieval = new RetrievalPipeline(_boot.Embedding, _boot.Storage, _boot.EntityStore);
        var history = new List<(string question, string answer, List<string> sourceIds)>();
        var collectionName = _options.Name ?? "default";

        var llmAvailable = _ollamaAvailable || (_llmRouter?.HasCloudProvider ?? false);

        if (!_options.Once)
        {
            AnsiConsole.Write(new Rule("[bold cyan]DoomSummarizer Ask Mode[/]").LeftJustified());
            AnsiConsole.MarkupLine("[grey]Interactive Q&A over your stored knowledge base.[/]");
            AnsiConsole.MarkupLine("[grey]Commands: quit, sources, history, clear, suggest <prefix>[/]");
            if (_options.CrawlProgress != null)
                AnsiConsole.MarkupLine("[grey]Background crawl is running — items become queryable as they're indexed.[/]");
            AnsiConsole.WriteLine();
        }

        var question = _options.InitialQuestion;

        while (!ct.IsCancellationRequested)
        {
            // Drain crawl progress before each prompt
            DrainCrawlProgress();

            if (string.IsNullOrWhiteSpace(question))
            {
                if (_options.Once) break;

                // Spectre.Console prompts require an interactive terminal.
                // Fall back to Console.ReadLine when stdin is redirected (e.g., piped input, tests).
                if (Console.IsInputRedirected)
                {
                    Console.Write("> ");
                    question = Console.ReadLine();
                    if (question == null) break; // End of input stream
                }
                else
                {
                    question = AnsiConsole.Prompt(
                        new TextPrompt<string>("[bold cyan]>[/]")
                            .AllowEmpty());
                }

                if (string.IsNullOrWhiteSpace(question)) continue;
            }

            var cmd = question.Trim().ToLowerInvariant();
            if (cmd is "quit" or "exit" or "bye" or "q")
                break;
            if (cmd == "sources")
            {
                ShowSources(history);
                question = null;
                continue;
            }
            if (cmd == "history")
            {
                ShowHistory(history);
                question = null;
                continue;
            }
            if (cmd == "clear")
            {
                history.Clear();
                AnsiConsole.MarkupLine("[grey]Conversation cleared.[/]");
                question = null;
                continue;
            }
            if (cmd.StartsWith("suggest "))
            {
                var prefix = question.Trim()[8..];
                ShowLuceneSuggestions(prefix, collectionName, _boot.Storage);
                question = null;
                continue;
            }

            await AnswerQuestion(question, effectiveSources, retrieval,
                _boot.Storage, _boot.Embedding, _ollama!,
                llmAvailable, history, ct);

            if (_options.Once) break;
            question = null;
        }

        // Drain any remaining progress
        DrainCrawlProgress();

        if (!_options.Once)
            AnsiConsole.MarkupLine("\n[grey]Goodbye.[/]");

        return 0;
    }

    private void DrainCrawlProgress()
    {
        if (_options.CrawlProgress == null) return;
        while (_options.CrawlProgress.TryRead(out var update))
        {
            RenderCrawlUpdate(update);
        }
    }

    private static void RenderCrawlUpdate(CrawlProgressUpdate update)
    {
        var pct = update.Total > 0 ? (int)(100.0 * update.Current / update.Total) : 0;

        if (update.IsComplete)
        {
            if (update.Error != null)
                AnsiConsole.MarkupLine($"[red][[crawl error]] {Markup.Escape(update.Message)}[/]");
            else
                AnsiConsole.MarkupLine($"[green][[crawl done]] {Markup.Escape(update.Message)}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[dim][[crawl {pct}%]] {Markup.Escape(update.Message)}[/]");
        }
    }

    private async Task AnswerQuestion(
        string question,
        IReadOnlyList<string>? effectiveSources,
        RetrievalPipeline retrieval,
        StorageService storage,
        IEmbeddingService embedding,
        OllamaService ollama,
        bool ollamaAvailable,
        List<(string question, string answer, List<string> sourceIds)> history,
        CancellationToken ct)
    {
        // Unified progress spinner wrapping all phases
        DecompositionResult? decomposition = null;
        var conceptBudget = _options.TopK;
        var collectionName = _options.Name ?? "default";
        var retrievalResult = RetrievalResult.Empty;
        var evidence = new List<ContentItem>();
        var topEvidence = new List<ContentItem>();
        var sourceIds = new List<string>();
        var answer = "";
        var needsDisambiguation = false;
        DisambiguationResult? disambiguation = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync("Classifying query...", async ctx =>
            {
                // Phase 1: Decompose
                try
                {
                    var decomposer = new DecompositionPipeline(
                        new ComplexityClassifier(embedding),
                        new ConceptClassifier(embedding),
                        new IQueryAnalyzer[]
                        {
                            new ReferenceExtractor(),
                            new StructuralAnalyzer(embedding),
                            new TemporalAnalyzer(),
                            new SemanticClusterAnalyzer(embedding),
                            new ToolUseAnalyzer(embedding)
                        },
                        new DeterministicRefiner(),
                        embedding);

                    decomposition = await decomposer.DecomposeAsync(
                        question,
                        entities: null,
                        hasUrls: false,
                        hasDateTimes: false,
                        sentinelData: null,
                        ct: ct);
                }
                catch
                {
                    // Decomposer failure is non-fatal
                }

                if (decomposition != null)
                {
                    var policy = new ConceptRegistry().GetPolicy(decomposition.Concept);
                    conceptBudget = Math.Max(_options.TopK, policy.FetchBudget / 2);
                }

                // Phase 2: Retrieval
                ctx.Status("Searching knowledge base...");
                retrievalResult = await retrieval.SearchAsync(question, new RetrievalOptions
                {
                    SourceFilters = effectiveSources,
                    CollectionName = collectionName,
                    TopK = conceptBudget * 2,
                    MinRelevance = 0.15f,
                    IsKnowledgeBase = true,
                    UseEmbeddingDedup = true,
                }, ct);

                evidence = retrievalResult.Items;

                if (evidence.Count == 0)
                    return; // Will handle below

                // Phase 3: Disambiguate
                ctx.Status("Disambiguating entities...");
                var disambiguator = new EntityDisambiguationService();
                disambiguation = await disambiguator.DisambiguateAsync(
                    evidence, question, embedding, storage, ollama, ollamaAvailable, ct);

                if (disambiguation.IsAmbiguous && disambiguation.TooMany)
                    return; // Will handle interactively below

                if (disambiguation.IsAmbiguous && (_options.Once || Console.IsInputRedirected))
                {
                    var best = disambiguation.Clusters
                        .OrderByDescending(c => c.AverageRelevance)
                        .First();
                    evidence = best.Items;
                }
                else if (disambiguation.IsAmbiguous)
                {
                    needsDisambiguation = true;
                    return; // Need interactive selection — exit spinner first
                }

                // Phase 4: Generate answer
                topEvidence = evidence.Take(_options.TopK).ToList();
                sourceIds = topEvidence.Select(e => e.Id).ToList();

                ctx.Status("Verifying terms...");
                var missingTerms = DoomSummarizer.Core.Services.TermVerifier.Verify(
                    question, storage.DataPath, collectionName,
                    sourceFilters: effectiveSources);

                ctx.Status("Synthesizing answer...");
                if (ollamaAvailable)
                {
                    var analyzedItems = topEvidence.Select(e => (
                        title: e.Title,
                        summary: e.Summary ?? "",
                        topic: e.DetectedTopic ?? "general",
                        sentiment: e.SentimentScore,
                        url: e.Url ?? "",
                        relevance: (double)e.RelevanceScore
                    )).ToList();

                    var effectiveQuery = question;
                    if (history.Count > 0)
                    {
                        var histCtx = new StringBuilder("Context from prior conversation:\n");
                        foreach (var (q, a, _) in history.TakeLast(3))
                        {
                            histCtx.AppendLine($"Q: {q}");
                            var truncA = a.Length > 200 ? a[..200] + "..." : a;
                            histCtx.AppendLine($"A: {truncA}");
                        }
                        effectiveQuery = $"{histCtx}\n\nCurrent question: {question}";
                    }

                    answer = await ollama.SynthesizeSummaryAsync(
                        analyzedItems,
                        "neutral",
                        "",
                        effectiveQuery,
                        topEvidence,
                        embedder: text => embedding.EmbedAsync(text).GetAwaiter().GetResult(),
                        batchEmbedder: texts => embedding.EmbedBatchAsync(texts).GetAwaiter().GetResult(),
                        forceAnswer: true,
                        promptTemplate: _options.PromptTemplate,
                        missingTerms: missingTerms,
                        ct: ct);
                }
                else
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"Found {topEvidence.Count} relevant items:\n");
                    foreach (var item in topEvidence)
                    {
                        sb.AppendLine($"- **{item.Title}**");
                        if (!string.IsNullOrEmpty(item.Summary))
                            sb.AppendLine($"  {item.Summary}");
                        if (!string.IsNullOrEmpty(item.Url))
                            sb.AppendLine($"  {item.Url}");
                    }
                    answer = sb.ToString();
                }
            });

        if (!_options.Quiet && evidence.Count > 0)
            AnsiConsole.MarkupLine($"[dim]{evidence.Count} results ({retrievalResult.Elapsed.TotalMilliseconds:F0}ms)[/]");

        if (evidence.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No matching evidence found in the knowledge base.[/]");
            if (_options.IsCrawlRunning?.Invoke() == true)
                AnsiConsole.MarkupLine("[grey]Crawl is still running — more items will become available soon.[/]");
            else
                AnsiConsole.MarkupLine("[grey]Try: doomsummarizer scroll \"your topic\" first to fetch content.[/]");
            return;
        }

        // Interactive disambiguation (must run outside spinner for prompt)
        if (disambiguation is { IsAmbiguous: true, TooMany: true })
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Found {disambiguation.Clusters.Count} distinct entities matching \"{Markup.Escape(question)}\".[/]");
            AnsiConsole.MarkupLine("[yellow]Please be more specific.[/]");
            return;
        }

        if (needsDisambiguation && disambiguation is { IsAmbiguous: true })
        {
            AnsiConsole.MarkupLine(
                $"\n[bold yellow]Found {disambiguation.Clusters.Count} distinct entities matching \"{Markup.Escape(question)}\":[/]");

            var choices = new List<string>();
            for (var i = 0; i < disambiguation.Clusters.Count; i++)
            {
                var c = disambiguation.Clusters[i];
                var label = $"{i + 1}. {c.Label} — {c.Items.Count} sources";
                choices.Add(label);
                AnsiConsole.MarkupLine($"  [cyan]{Markup.Escape(label)}[/]");
            }
            choices.Add("All results");

            var selected = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Which entity did you mean?[/]")
                    .AddChoices(choices));

            if (selected != "All results")
            {
                var selectedIdx = choices.IndexOf(selected);
                if (selectedIdx >= 0 && selectedIdx < disambiguation.Clusters.Count)
                    evidence = disambiguation.Clusters[selectedIdx].Items;
            }

            // After disambiguation, generate the answer with a second spinner
            topEvidence = evidence.Take(_options.TopK).ToList();
            sourceIds = topEvidence.Select(e => e.Id).ToList();

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("green"))
                .StartAsync("Synthesizing answer...", async ctx =>
                {
                    ctx.Status("Verifying terms...");
                    var missingTerms = DoomSummarizer.Core.Services.TermVerifier.Verify(
                        question, storage.DataPath, collectionName,
                        sourceFilters: effectiveSources);

                    ctx.Status("Synthesizing answer...");
                    if (ollamaAvailable)
                    {
                        var analyzedItems = topEvidence.Select(e => (
                            title: e.Title,
                            summary: e.Summary ?? "",
                            topic: e.DetectedTopic ?? "general",
                            sentiment: e.SentimentScore,
                            url: e.Url ?? "",
                            relevance: (double)e.RelevanceScore
                        )).ToList();

                        var effectiveQuery = question;
                        if (history.Count > 0)
                        {
                            var histCtx = new StringBuilder("Context from prior conversation:\n");
                            foreach (var (q, a, _) in history.TakeLast(3))
                            {
                                histCtx.AppendLine($"Q: {q}");
                                var truncA = a.Length > 200 ? a[..200] + "..." : a;
                                histCtx.AppendLine($"A: {truncA}");
                            }
                            effectiveQuery = $"{histCtx}\n\nCurrent question: {question}";
                        }

                        answer = await ollama.SynthesizeSummaryAsync(
                            analyzedItems,
                            "neutral",
                            "",
                            effectiveQuery,
                            topEvidence,
                            embedder: text => embedding.EmbedAsync(text).GetAwaiter().GetResult(),
                            batchEmbedder: texts => embedding.EmbedBatchAsync(texts).GetAwaiter().GetResult(),
                            forceAnswer: true,
                            promptTemplate: _options.PromptTemplate,
                            missingTerms: missingTerms,
                            ct: ct);
                    }
                    else
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine($"Found {topEvidence.Count} relevant items:\n");
                        foreach (var item in topEvidence)
                        {
                            sb.AppendLine($"- **{item.Title}**");
                            if (!string.IsNullOrEmpty(item.Summary))
                                sb.AppendLine($"  {item.Summary}");
                            if (!string.IsNullOrEmpty(item.Url))
                                sb.AppendLine($"  {item.Url}");
                        }
                        answer = sb.ToString();
                    }
                });
        }
        else if (disambiguation is { IsAmbiguous: true } && (_options.Once || Console.IsInputRedirected))
        {
            // Auto-selected inside spinner — just report it
            var best = disambiguation.Clusters
                .OrderByDescending(c => c.AverageRelevance)
                .First();
            if (!_options.Quiet)
                AnsiConsole.MarkupLine(
                    $"[grey]Auto-selected: {Markup.Escape(best.Label)} ({best.Items.Count} sources, method: {disambiguation.Method})[/]");
        }

        // Show evidence summary
        if (!_options.Quiet)
        {
            var evidenceTable = new Table()
                .Border(TableBorder.Simple)
                .AddColumn(new TableColumn("#").RightAligned())
                .AddColumn("Title")
                .AddColumn("Source")
                .AddColumn("Fetched");

            var rank = 1;
            foreach (var item in evidence.Take(_options.TopK))
            {
                var title = item.Title.Length > 50 ? item.Title[..47] + "..." : item.Title;
                var fetchedAge = FormattingHelpers.FormatAge(item.FetchedAt);
                evidenceTable.AddRow(
                    $"[grey]{rank}[/]",
                    Markup.Escape(title),
                    $"[grey]{item.Source}[/]",
                    $"[grey]{fetchedAge}[/]");
                rank++;
            }

            AnsiConsole.Write(evidenceTable);
        }

        // Display answer
        AnsiConsole.WriteLine();
        var panel = new Panel(Markup.Escape(answer))
            .Header("[bold green]Answer[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Green)
            .Padding(1, 0);
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();

        // Log query and update history
        history.Add((question, answer, sourceIds));
        var logEmbedding = await embedding.EmbedAsync(question, ct);
        await storage.LogQueryAsync(question, logEmbedding, null, sourceIds);
    }

    private static void ShowSources(List<(string question, string answer, List<string> sourceIds)> history)
    {
        if (history.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No questions asked yet.[/]");
            return;
        }

        var allIds = history.SelectMany(h => h.sourceIds).Distinct().ToList();
        AnsiConsole.MarkupLine($"[bold]Sources used across {history.Count} question(s):[/]");
        foreach (var id in allIds)
            AnsiConsole.MarkupLine($"  [grey]{id}[/]");
    }

    private static void ShowHistory(List<(string question, string answer, List<string> sourceIds)> history)
    {
        if (history.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No questions asked yet.[/]");
            return;
        }

        for (var i = 0; i < history.Count; i++)
        {
            var (q, a, ids) = history[i];
            AnsiConsole.MarkupLine($"\n[bold cyan]Q{i + 1}:[/] {Markup.Escape(q)}");
            var preview = a.Length > 200 ? a[..200] + "..." : a;
            AnsiConsole.MarkupLine($"[green]A:[/] {Markup.Escape(preview)}");
            AnsiConsole.MarkupLine($"[grey]({ids.Count} sources)[/]");
        }
    }

    private static void ShowLuceneSuggestions(string prefix, string collectionName, StorageService storage)
    {
        var luceneIndexPath = Path.Combine(storage.DataPath, "lucene", collectionName);
        if (!Directory.Exists(luceneIndexPath))
        {
            AnsiConsole.MarkupLine("[yellow]No Lucene index for this collection yet. Ask a question first.[/]");
            return;
        }

        using var lucene = new LuceneSearchService(luceneIndexPath);
        lucene.Open();

        var suggestions = lucene.Suggest(prefix, limit: 10);
        if (suggestions.Count == 0)
        {
            AnsiConsole.MarkupLine($"[grey]No suggestions for \"{Markup.Escape(prefix)}\"[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[bold]Suggestions for \"{Markup.Escape(prefix)}\":[/]");
        foreach (var s in suggestions)
            AnsiConsole.MarkupLine($"  [cyan]{Markup.Escape(s.Title ?? s.Id)}[/] [grey]({s.Score:F2})[/]");
    }
}
