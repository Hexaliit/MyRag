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
using Mostlylucid.DocSummarizer.Services.Onnx;
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

        // Per-session conversational RAG state
        var sessionId = Guid.NewGuid().ToString("N")[..12];
        var segmentCache = new SalientSegmentCache(capacity: 50);
        var chatCorpus = new ChatCorpusService(_boot.Storage, _boot.Embedding);
        var turnNumber = 0;
        Task? pendingIndexTask = null;

        // Personal corpus: user memory via existing entity infrastructure
        var personalCorpus = CreatePersonalCorpus();

        if (!_options.Once)
        {
            AnsiConsole.Write(new Rule("[bold cyan]DoomSummarizer Ask Mode[/]").LeftJustified());
            AnsiConsole.MarkupLine("[grey]Interactive Q&A over your stored knowledge base.[/]");
            AnsiConsole.MarkupLine("[grey]Commands: quit, sources, history, clear, suggest <prefix>[/]");
            AnsiConsole.MarkupLine("[grey]Slash commands: /docs, /segments, /cache, /resolve <query>, /me, /remember, /forget, /personal, /whois[/]");
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
                segmentCache = new SalientSegmentCache(capacity: 50);
                turnNumber = 0;
                AnsiConsole.MarkupLine("[grey]Conversation and cache cleared.[/]");
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

            // Slash commands
            if (cmd.StartsWith("/"))
            {
                await HandleSlashCommandAsync(cmd, question.Trim(), history, segmentCache,
                    retrieval, effectiveSources, collectionName, turnNumber,
                    personalCorpus, ct);
                question = null;
                continue;
            }

            // Await any pending index task from previous turn before starting new one
            if (pendingIndexTask != null)
            {
                try { await pendingIndexTask; } catch { /* non-fatal */ }
                pendingIndexTask = null;
            }

            turnNumber++;
            pendingIndexTask = await AnswerQuestion(question, effectiveSources, retrieval,
                _boot.Storage, _boot.Embedding, _ollama,
                llmAvailable, history, segmentCache, chatCorpus,
                personalCorpus, sessionId, turnNumber, ct);

            if (_options.Once) break;
            question = null;
        }

        // Await final index task before exit to prevent ONNX disposal crash
        if (pendingIndexTask != null)
        {
            try { await pendingIndexTask; } catch { /* non-fatal */ }
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

    private async Task<Task?> AnswerQuestion(
        string question,
        IReadOnlyList<string>? effectiveSources,
        RetrievalPipeline retrieval,
        StorageService storage,
        IEmbeddingService embedding,
        OllamaService? ollama,
        bool ollamaAvailable,
        List<(string question, string answer, List<string> sourceIds)> history,
        SalientSegmentCache segmentCache,
        ChatCorpusService chatCorpus,
        PersonalCorpusService? personalCorpus,
        string sessionId,
        int turnNumber,
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
        ConversationSentinelResult? sentinelResult = null;
        string? personalContext = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync("Classifying query...", async ctx =>
            {
                // Phase 0: Conversation Sentinel — resolve follow-ups before retrieval
                if (history.Count > 0)
                {
                    ctx.Status("Resolving conversation context...");
                    var sentinel = new ConversationSentinel(ollama, embedding, ollamaAvailable);
                    sentinelResult = await sentinel.ResolveAsync(question, history, ct);

                    if (!_options.Quiet && sentinelResult.IsFollowUp)
                    {
                        // Diagnostic will be shown after spinner
                    }
                }

                var searchQuery = sentinelResult?.ResolvedQuery ?? question;

                // Phase 0b: Personal context gap-filling — resolve "here", "my company", etc.
                if (personalCorpus != null)
                {
                    try
                    {
                        var sentinel = new ConversationSentinel(ollama, embedding, ollamaAvailable);
                        var (personalResolved, pCtx) =
                            await sentinel.ResolveFromPersonalAsync(searchQuery, personalCorpus, ct);
                        if (personalResolved != searchQuery)
                            searchQuery = personalResolved;
                        personalContext = pCtx;
                    }
                    catch
                    {
                        // Personal resolution failure is non-fatal
                    }
                }

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
                        searchQuery,
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

                // Phase 2: Retrieval — use resolved query
                ctx.Status("Searching knowledge base...");
                retrievalResult = await retrieval.SearchAsync(searchQuery, new RetrievalOptions
                {
                    SourceFilters = effectiveSources,
                    CollectionName = collectionName,
                    TopK = conceptBudget * 2,
                    MinRelevance = 0.15f,
                    IsKnowledgeBase = true,
                    UseEmbeddingDedup = true,
                }, ct);

                // Phase 2b: Merge with salient cached segments
                var freshItems = retrievalResult.Items;
                if (history.Count > 0)
                {
                    ctx.Status("Merging cached context...");
                    try
                    {
                        var queryEmb = await embedding.EmbedAsync(searchQuery, ct);
                        var cachedSalient = segmentCache.GetSalient(queryEmb, turnNumber);

                        // Merge: fresh results take priority, cached fill remaining slots
                        var freshIds = new HashSet<string>(freshItems.Select(f => f.Id));
                        var mergedItems = freshItems
                            .Concat(cachedSalient.Where(c => !freshIds.Contains(c.Id)))
                            .Take(conceptBudget * 2)
                            .ToList();
                        evidence = mergedItems;
                    }
                    catch
                    {
                        evidence = freshItems;
                    }
                }
                else
                {
                    evidence = freshItems;
                }

                // Add fresh results to cache for future turns
                segmentCache.AddRange(freshItems, turnNumber);
                segmentCache.Evict(turnNumber);

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

                // Phase 4: Generate answer with conversation context
                topEvidence = evidence.Take(_options.TopK).ToList();
                sourceIds = topEvidence.Select(e => e.Id).ToList();

                ctx.Status("Verifying terms...");
                var missingTerms = DoomSummarizer.Core.Services.TermVerifier.Verify(
                    question, storage.DataPath, collectionName,
                    sourceFilters: effectiveSources);

                ctx.Status("Synthesizing answer...");
                answer = await SynthesizeAnswer(
                    question, topEvidence, embedding, ollama, ollamaAvailable,
                    history, sentinelResult, personalContext, missingTerms, ct);
            });

        // Show sentinel diagnostic
        if (!_options.Quiet && sentinelResult is { IsFollowUp: true })
        {
            AnsiConsole.MarkupLine(
                $"[dim]Resolved: \"{Markup.Escape(sentinelResult.ResolvedQuery)}\" ({sentinelResult.Reason})[/]");
        }

        // Show personal context diagnostic
        if (!_options.Quiet && personalContext != null)
        {
            AnsiConsole.MarkupLine(
                $"[dim]Personal: {Markup.Escape(personalContext)}[/]");
        }

        // Show cache stats
        if (!_options.Quiet && history.Count > 0)
        {
            var (count, capacity, evicted) = segmentCache.Stats;
            var (pEntries, pHits, pMisses, pRate) = segmentCache.PromptIndex.Stats;
            var indexNote = pHits + pMisses > 0
                ? $", prompt index: {pHits}/{pHits + pMisses} hits ({pRate:P0})"
                : "";
            AnsiConsole.MarkupLine($"[dim]Cache: {count}/{capacity} segments, {evicted} evicted{indexNote}[/]");
        }

        if (!_options.Quiet && evidence.Count > 0)
            AnsiConsole.MarkupLine($"[dim]{evidence.Count} results ({retrievalResult.Elapsed.TotalMilliseconds:F0}ms)[/]");

        if (evidence.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No matching evidence found in the knowledge base.[/]");
            if (_options.IsCrawlRunning?.Invoke() == true)
                AnsiConsole.MarkupLine("[grey]Crawl is still running — more items will become available soon.[/]");
            else
                AnsiConsole.MarkupLine("[grey]Try: doomsummarizer scroll \"your topic\" first to fetch content.[/]");
            return null;
        }

        // Interactive disambiguation (must run outside spinner for prompt)
        if (disambiguation is { IsAmbiguous: true, TooMany: true })
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Found {disambiguation.Clusters.Count} distinct entities matching \"{Markup.Escape(question)}\".[/]");
            AnsiConsole.MarkupLine("[yellow]Please be more specific.[/]");
            return null;
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
                    answer = await SynthesizeAnswer(
                        question, topEvidence, embedding, ollama, ollamaAvailable,
                        history, sentinelResult, personalContext, missingTerms, ct);
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

        // Background: index turn into chat corpus + detect personal facts + record feedback
        // Returned so RunAsync can await before exit (prevents ONNX disposal crash)
        return Task.Run(async () =>
        {
            try
            {
                await chatCorpus.IndexTurnAsync(sessionId, turnNumber,
                    question, answer, sourceIds, ct);

                // Record implicit feedback if this was a follow-up (continued topic = positive)
                if (sentinelResult is { IsFollowUp: true } && turnNumber > 1)
                {
                    await chatCorpus.RecordFeedbackAsync(sessionId, turnNumber - 1,
                        continued: true, ct);
                }

                // Detect and index personal facts from user's question
                if (personalCorpus != null)
                {
                    // Check for name introduction first (e.g., "My name is Scott")
                    await personalCorpus.DetectAndSetNameAsync(question, ct);

                    var factStatement = await personalCorpus.DetectSelfDisclosureAsync(question, ct);
                    if (factStatement != null)
                    {
                        await personalCorpus.IndexFactAsync(
                            personalCorpus.ActiveCorpusName, factStatement, question, ct);
                    }
                }
            }
            catch
            {
                // Chat corpus / personal corpus indexing failure is non-fatal
            }
        }, ct);
    }

    /// <summary>
    /// Synthesize an answer using conversation context via ask-answer.txt or SynthesizeSummaryAsync.
    /// </summary>
    private async Task<string> SynthesizeAnswer(
        string question,
        List<ContentItem> topEvidence,
        IEmbeddingService embedding,
        OllamaService? ollama,
        bool ollamaAvailable,
        List<(string question, string answer, List<string> sourceIds)> history,
        ConversationSentinelResult? sentinelResult,
        string? personalContext,
        List<string>? missingTerms,
        CancellationToken ct)
    {
        if (!ollamaAvailable || ollama == null)
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
            return sb.ToString();
        }

        // Build conversation context via ContextCompressor when we have history
        var conversationContext = "";
        if (history.Count > 0)
        {
            var compressor = new ContextCompressor(embedding, ollama);
            var resolvedQuery = sentinelResult?.ResolvedQuery ?? question;
            conversationContext = await compressor.CompressAsync(
                history.Select(h => (h.question, h.answer)).ToList(),
                resolvedQuery, targetTokens: 2000, ct);
        }

        // Build user context line from personal corpus (empty string if none)
        var userContextLine = !string.IsNullOrEmpty(personalContext)
            ? $"USER CONTEXT: {personalContext}\n"
            : "";

        // If we have conversation context or personal context, use the ask-answer.txt template directly
        if (!string.IsNullOrEmpty(conversationContext) || !string.IsNullOrEmpty(personalContext))
        {
            var evidenceBlock = BuildEvidenceBlock(topEvidence);
            var templateVars = new Dictionary<string, object?>
            {
                ["USER_CONTEXT"] = userContextLine,
                ["CONVERSATION_CONTEXT"] = conversationContext,
                ["QUESTION"] = question, // Original question (not resolved)
                ["TODAY"] = DateTime.Now.ToString("MMMM d, yyyy"),
                ["EVIDENCE"] = evidenceBlock,
            };

            var prompt = PromptTemplateService.Render("ask-answer", templateVars);
            return await ollama.GenerateAsync(prompt, temperature: 0.6, ct: ct);
        }

        // First turn: use the full SynthesizeSummaryAsync pipeline (template selection, re-ranking, etc.)
        var analyzedItems = topEvidence.Select(e => (
            title: e.Title,
            summary: e.Summary ?? "",
            topic: e.DetectedTopic ?? "general",
            sentiment: e.SentimentScore,
            url: e.Url ?? "",
            relevance: (double)e.RelevanceScore
        )).ToList();

        return await ollama.SynthesizeSummaryAsync(
            analyzedItems,
            "neutral",
            "",
            question,
            topEvidence,
            embedder: text => embedding.EmbedAsync(text).GetAwaiter().GetResult(),
            batchEmbedder: texts => embedding.EmbedBatchAsync(texts).GetAwaiter().GetResult(),
            forceAnswer: true,
            promptTemplate: _options.PromptTemplate,
            missingTerms: missingTerms,
            ct: ct);
    }

    /// <summary>
    /// Build evidence block for the ask-answer.txt template.
    /// </summary>
    private static string BuildEvidenceBlock(List<ContentItem> items)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            sb.AppendLine($"\n[{i + 1}] {item.Title}");

            if (!string.IsNullOrEmpty(item.Content))
            {
                var content = item.Content.Length > 1500
                    ? item.Content[..1500] + "..."
                    : item.Content;
                sb.AppendLine(content);
            }
            else if (!string.IsNullOrEmpty(item.Summary))
            {
                sb.AppendLine(item.Summary);
            }
        }
        return sb.ToString();
    }

    // --- Slash Commands ---

    private async Task HandleSlashCommandAsync(
        string cmd, string rawInput,
        List<(string question, string answer, List<string> sourceIds)> history,
        SalientSegmentCache segmentCache,
        RetrievalPipeline retrieval,
        IReadOnlyList<string>? effectiveSources,
        string collectionName,
        int turnNumber,
        PersonalCorpusService? personalCorpus,
        CancellationToken ct)
    {
        if (cmd == "/docs" || cmd == "/documents")
        {
            ShowMatchingDocuments(history);
        }
        else if (cmd == "/segments" || cmd == "/seg")
        {
            ShowSegmentDetails(history);
        }
        else if (cmd == "/cache")
        {
            ShowCacheContents(segmentCache, turnNumber);
        }
        else if (cmd.StartsWith("/resolve "))
        {
            var query = rawInput[9..].Trim();
            ShowResolvedQuery(query, history);
        }
        else if (cmd == "/stats")
        {
            ShowSessionStats(history, segmentCache, turnNumber);
        }
        else if (cmd == "/me")
        {
            await ShowPersonalFacts(personalCorpus, ct);
        }
        else if (cmd.StartsWith("/remember "))
        {
            var statement = rawInput[10..].Trim();
            await RememberFact(personalCorpus, statement, ct);
        }
        else if (cmd.StartsWith("/forget"))
        {
            var category = cmd.Length > 7 ? rawInput[7..].Trim() : null;
            if (string.IsNullOrWhiteSpace(category)) category = "all";
            await ForgetFacts(personalCorpus, category, ct);
        }
        else if (cmd == "/personal")
        {
            await ShowPersonalEntities(personalCorpus, ct);
        }
        else if (cmd == "/whois" || cmd.StartsWith("/whois "))
        {
            var name = cmd.Length > 6 ? rawInput[6..].Trim() : null;
            await SwitchPersonalCorpus(personalCorpus, name, ct);
        }
        else if (cmd == "/help" || cmd == "/?")
        {
            ShowSlashHelp();
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]Unknown command: {Markup.Escape(cmd)}[/]");
            AnsiConsole.MarkupLine("[grey]Type /help for available slash commands.[/]");
        }
    }

    private static void ShowMatchingDocuments(
        List<(string question, string answer, List<string> sourceIds)> history)
    {
        if (history.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No questions asked yet.[/]");
            return;
        }

        var lastTurn = history[^1];
        AnsiConsole.MarkupLine($"[bold]Documents from last query ({lastTurn.sourceIds.Count} sources):[/]");

        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumn("#")
            .AddColumn("Source ID");

        for (var i = 0; i < lastTurn.sourceIds.Count; i++)
            table.AddRow($"{i + 1}", Markup.Escape(lastTurn.sourceIds[i]));

        AnsiConsole.Write(table);
    }

    private static void ShowSegmentDetails(
        List<(string question, string answer, List<string> sourceIds)> history)
    {
        if (history.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No questions asked yet.[/]");
            return;
        }

        // Show all unique source IDs across the conversation with frequency
        var sourceFreq = new Dictionary<string, int>();
        foreach (var (_, _, ids) in history)
        {
            foreach (var id in ids)
            {
                sourceFreq.TryGetValue(id, out var count);
                sourceFreq[id] = count + 1;
            }
        }

        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumn("Source ID")
            .AddColumn("Appearances");

        foreach (var (id, count) in sourceFreq.OrderByDescending(kv => kv.Value))
            table.AddRow(Markup.Escape(id), count.ToString());

        AnsiConsole.MarkupLine($"[bold]All segments across {history.Count} turn(s):[/]");
        AnsiConsole.Write(table);
    }

    private static void ShowCacheContents(SalientSegmentCache cache, int turnNumber)
    {
        var (count, capacity, evicted) = cache.Stats;
        AnsiConsole.MarkupLine($"[bold]Segment Cache[/] ({count}/{capacity}, {evicted} evicted)");

        var ids = cache.GetAllIds();
        if (ids.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]Cache is empty.[/]");
        }
        else
        {
            var table = new Table()
                .Border(TableBorder.Simple)
                .AddColumn("#")
                .AddColumn("Segment ID");

            for (var i = 0; i < Math.Min(ids.Count, 20); i++)
                table.AddRow($"{i + 1}", Markup.Escape(ids[i]));

            if (ids.Count > 20)
                AnsiConsole.MarkupLine($"[grey]... and {ids.Count - 20} more[/]");

            AnsiConsole.Write(table);
        }

        // Prompt salience index stats
        var (pEntries, pHits, pMisses, pRate) = cache.PromptIndex.Stats;
        AnsiConsole.MarkupLine($"\n[bold]Prompt Salience Index[/] ({pEntries} entries)");
        if (pHits + pMisses > 0)
        {
            AnsiConsole.MarkupLine($"  Hits: {pHits}, Misses: {pMisses}, Hit rate: {pRate:P0}");
            AnsiConsole.MarkupLine($"[grey]  (Each hit saves {count} cosine similarity computations)[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[grey]  No lookups yet.[/]");
        }
    }

    private void ShowResolvedQuery(
        string query,
        List<(string question, string answer, List<string> sourceIds)> history)
    {
        if (history.Count == 0)
        {
            AnsiConsole.MarkupLine($"[grey]No history — query is already standalone: \"{Markup.Escape(query)}\"[/]");
            return;
        }

        // Quick synchronous resolution (rule-based only, no LLM)
        var sentinel = new ConversationSentinel(null, _boot.Embedding, false);
        var result = sentinel.ResolveAsync(query, history, CancellationToken.None)
            .GetAwaiter().GetResult();

        AnsiConsole.MarkupLine($"[bold]Resolved query:[/] {Markup.Escape(result.ResolvedQuery)}");
        AnsiConsole.MarkupLine($"[grey]Follow-up: {result.IsFollowUp} | Confidence: {result.Confidence:F2} | {result.Reason}[/]");
    }

    private static void ShowSessionStats(
        List<(string question, string answer, List<string> sourceIds)> history,
        SalientSegmentCache cache,
        int turnNumber)
    {
        var (count, capacity, evicted) = cache.Stats;
        var (pEntries, pHits, pMisses, pRate) = cache.PromptIndex.Stats;
        var totalSources = history.SelectMany(h => h.sourceIds).Distinct().Count();
        var savedComputations = pHits * count; // each hit skips N cosine checks

        AnsiConsole.MarkupLine("[bold]Session Statistics[/]");
        AnsiConsole.MarkupLine($"  Turns: {turnNumber}");
        AnsiConsole.MarkupLine($"  Unique sources: {totalSources}");
        AnsiConsole.MarkupLine($"  Cache: {count}/{capacity} ({evicted} evicted)");
        AnsiConsole.MarkupLine($"  Prompt index: {pEntries} entries, {pHits} hits / {pHits + pMisses} lookups ({pRate:P0})");
        if (savedComputations > 0)
            AnsiConsole.MarkupLine($"  Saved computations: ~{savedComputations} cosine similarities");
        AnsiConsole.MarkupLine($"  History entries: {history.Count}");
    }

    private static void ShowSlashHelp()
    {
        AnsiConsole.MarkupLine("[bold]Slash Commands:[/]");
        AnsiConsole.MarkupLine("  [cyan]/docs[/]              Show documents from last query");
        AnsiConsole.MarkupLine("  [cyan]/segments[/]          Show all source segments across conversation");
        AnsiConsole.MarkupLine("  [cyan]/cache[/]             Show cached segment IDs");
        AnsiConsole.MarkupLine("  [cyan]/resolve <q>[/]       Preview how a query would be resolved");
        AnsiConsole.MarkupLine("  [cyan]/stats[/]             Show session statistics");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[bold]Personal Memory:[/]");
        AnsiConsole.MarkupLine("  [cyan]/me[/]                Show all personal facts");
        AnsiConsole.MarkupLine("  [cyan]/remember <fact>[/]   Save a personal fact (e.g. /remember I live in London)");
        AnsiConsole.MarkupLine("  [cyan]/forget [type][/]     Forget facts: location, org, all (default: all)");
        AnsiConsole.MarkupLine("  [cyan]/personal[/]          Show personal entity graph");
        AnsiConsole.MarkupLine("  [cyan]/whois [name][/]      List/switch personal corpuses (e.g. /whois scott)");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("  [cyan]/help[/]              Show this help");
    }

    // --- Original display helpers ---

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

    // --- Personal corpus command handlers ---

    private static async Task ShowPersonalFacts(PersonalCorpusService? corpus, CancellationToken ct)
    {
        if (corpus == null)
        {
            AnsiConsole.MarkupLine("[yellow]Personal corpus not available.[/]");
            return;
        }

        var corpusName = corpus.ActiveCorpusName;
        var facts = await corpus.GetAllFactsAsync(corpusName, ct);
        if (facts.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No personal facts stored. Use /remember or share facts in your questions.[/]");
            return;
        }

        var label = corpusName == "default" ? "Personal Facts" : $"Personal Facts ({corpusName})";
        AnsiConsole.MarkupLine($"[bold]{label} ({facts.Count}):[/]");
        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumn("#")
            .AddColumn("Fact")
            .AddColumn("Stored");

        for (var i = 0; i < facts.Count; i++)
        {
            var fact = facts[i];
            var age = FormattingHelpers.FormatAge(fact.CreatedAt);
            table.AddRow($"{i + 1}", Markup.Escape(fact.Content ?? fact.Title), $"[grey]{age}[/]");
        }

        AnsiConsole.Write(table);
    }

    private static async Task RememberFact(
        PersonalCorpusService? corpus, string statement, CancellationToken ct)
    {
        if (corpus == null)
        {
            AnsiConsole.MarkupLine("[yellow]Personal corpus not available.[/]");
            return;
        }

        if (string.IsNullOrWhiteSpace(statement))
        {
            AnsiConsole.MarkupLine("[yellow]Usage: /remember <fact>[/]");
            return;
        }

        await corpus.IndexFactAsync(corpus.ActiveCorpusName, statement, null, ct);
        AnsiConsole.MarkupLine($"[green]Remembered:[/] {Markup.Escape(statement)}");
    }

    private static async Task ForgetFacts(
        PersonalCorpusService? corpus, string category, CancellationToken ct)
    {
        if (corpus == null)
        {
            AnsiConsole.MarkupLine("[yellow]Personal corpus not available.[/]");
            return;
        }

        var count = await corpus.ForgetAsync(corpus.ActiveCorpusName, category, ct);
        if (count > 0)
            AnsiConsole.MarkupLine($"[green]Forgot {count} personal fact(s) ({Markup.Escape(category)}).[/]");
        else
            AnsiConsole.MarkupLine($"[grey]No personal facts matching \"{Markup.Escape(category)}\" to forget.[/]");
    }

    private async Task ShowPersonalEntities(PersonalCorpusService? corpus, CancellationToken ct)
    {
        if (corpus == null)
        {
            AnsiConsole.MarkupLine("[yellow]Personal corpus not available.[/]");
            return;
        }

        var types = new[] { ("LOC", "Locations"), ("ORG", "Organizations"), ("PER", "People"), ("MISC", "Other") };
        var hasAny = false;

        var corpusName = corpus.ActiveCorpusName;
        var graphLabel = corpusName == "default" ? "Personal Entity Graph" : $"Personal Entity Graph ({corpusName})";
        AnsiConsole.MarkupLine($"[bold]{graphLabel}:[/]");

        foreach (var (type, label) in types)
        {
            var entities = await corpus.GetPersonalEntitiesAsync(corpusName, type, limit: 5, ct);
            if (entities.Count == 0) continue;
            hasAny = true;

            AnsiConsole.MarkupLine($"\n  [bold]{label}:[/]");
            foreach (var (name, _, lastSeen) in entities)
            {
                var age = FormattingHelpers.FormatAge(lastSeen);
                AnsiConsole.MarkupLine($"    {Markup.Escape(name)} [grey](seen {age})[/]");
            }
        }

        if (!hasAny)
        {
            AnsiConsole.MarkupLine("[grey]No personal entities yet. Share facts about yourself to build your profile.[/]");
        }
    }

    private static async Task SwitchPersonalCorpus(
        PersonalCorpusService? corpus, string? name, CancellationToken ct)
    {
        if (corpus == null)
        {
            AnsiConsole.MarkupLine("[yellow]Personal corpus not available.[/]");
            return;
        }

        var corpuses = await corpus.GetActiveCorpusNamesAsync(ct);

        if (string.IsNullOrWhiteSpace(name))
        {
            // List all personal corpuses
            if (corpuses.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]No personal corpuses yet. Share facts about yourself to get started.[/]");
                return;
            }

            AnsiConsole.MarkupLine("[bold]Personal Corpuses:[/]");
            foreach (var c in corpuses)
            {
                var active = c.Equals(corpus.ActiveCorpusName, StringComparison.OrdinalIgnoreCase)
                    ? " [green](active)[/]" : "";
                var facts = await corpus.GetAllFactsAsync(c, ct);
                AnsiConsole.MarkupLine($"  {Markup.Escape(c)}{active} — {facts.Count} fact(s)");
            }
            AnsiConsole.MarkupLine("[grey]Usage: /whois <name> to switch[/]");
            return;
        }

        // Switch to a specific corpus
        var match = corpuses.FirstOrDefault(c =>
            c.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (match != null)
        {
            corpus.SetActiveCorpus(match);
            AnsiConsole.MarkupLine($"[green]Switched to personal corpus:[/] {Markup.Escape(match)}");
        }
        else
        {
            // Create new corpus
            corpus.SetActiveCorpus(name);
            AnsiConsole.MarkupLine($"[green]Created new personal corpus:[/] {Markup.Escape(name)}");
            AnsiConsole.MarkupLine("[grey]Use /remember to add facts, or share them in conversation.[/]");
        }
    }

    // --- Personal corpus factory ---

    private PersonalCorpusService? CreatePersonalCorpus()
    {
        // NER: try to create, non-fatal if unavailable
        INerService? ner = null;
        try
        {
            var nerService = new NerService();
            if (nerService.IsAvailable)
            {
                nerService.InitializeAsync().GetAwaiter().GetResult();
                ner = nerService;
            }
            else
            {
                nerService.Dispose();
            }
        }
        catch
        {
            // NER unavailable — personal corpus works without it (no entity extraction)
        }

        // KnowledgeGraph: requires both VectorStore and EntityStore
        KnowledgeGraphService? knowledgeGraph = null;
        if (_boot.VectorStore != null && _boot.EntityStore != null)
        {
            var entityProfileService = new EntityProfileService(_boot.Embedding, _boot.EntityStore);
            knowledgeGraph = new KnowledgeGraphService(
                _boot.VectorStore, _boot.EntityStore, entityProfileService);
        }

        return new PersonalCorpusService(
            _boot.Storage, _boot.Embedding, ner, knowledgeGraph,
            _ollama, _ollamaAvailable);
    }
}
