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
    string? Source,
    string? Name,
    int Days,
    int TopK,
    bool Once,
    bool Quiet,
    string? InitialQuestion,
    ChannelReader<CrawlProgressUpdate>? CrawlProgress = null,
    Func<bool>? IsCrawlRunning = null);

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
        var effectiveSource = !string.IsNullOrWhiteSpace(_options.Name)
            ? $"crawl:{_options.Name}"
            : _options.Source;

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

            await AnswerQuestion(question, effectiveSource, retrieval,
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
        string? effectiveSource,
        RetrievalPipeline retrieval,
        StorageService storage,
        IEmbeddingService embedding,
        OllamaService ollama,
        bool ollamaAvailable,
        List<(string question, string answer, List<string> sourceIds)> history,
        CancellationToken ct)
    {
        // Decomposer: classify question complexity and concept
        DecompositionResult? decomposition = null;
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

        var conceptBudget = _options.TopK;
        if (decomposition != null)
        {
            var policy = new ConceptRegistry().GetPolicy(decomposition.Concept);
            conceptBudget = Math.Max(_options.TopK, policy.FetchBudget / 2);
        }

        var collectionName = _options.Name ?? "default";
        var retrievalResult = await retrieval.SearchAsync(question, new RetrievalOptions
        {
            SourceFilter = effectiveSource,
            CollectionName = collectionName,
            TopK = conceptBudget * 2,
            MinRelevance = 0.15f,
            IsKnowledgeBase = true,
            UseEmbeddingDedup = true,
        }, ct);

        var evidence = retrievalResult.Items;

        if (evidence.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No matching evidence found in the knowledge base.[/]");
            if (_options.IsCrawlRunning?.Invoke() == true)
                AnsiConsole.MarkupLine("[grey]Crawl is still running — more items will become available soon.[/]");
            else
                AnsiConsole.MarkupLine("[grey]Try: doomsummarizer scroll \"your topic\" first to fetch content.[/]");
            return;
        }

        // Entity disambiguation
        var disambiguator = new EntityDisambiguationService();
        var disambiguation = await disambiguator.DisambiguateAsync(
            evidence, question, embedding, storage, ollama, ollamaAvailable, ct);

        if (disambiguation.IsAmbiguous)
        {
            if (disambiguation.TooMany)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Found {disambiguation.Clusters.Count} distinct entities matching \"{Markup.Escape(question)}\".[/]");
                AnsiConsole.MarkupLine("[yellow]Please be more specific.[/]");
                return;
            }

            if (!_options.Once && !Console.IsInputRedirected)
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
            }
            else
            {
                var best = disambiguation.Clusters
                    .OrderByDescending(c => c.AverageRelevance)
                    .First();
                evidence = best.Items;

                if (!_options.Quiet)
                    AnsiConsole.MarkupLine(
                        $"[grey]Auto-selected: {Markup.Escape(best.Label)} ({best.Items.Count} sources, method: {disambiguation.Method})[/]");
            }
        }

        // Show evidence summary
        if (!_options.Quiet)
        {
            var evidenceTable = new Table()
                .Border(TableBorder.Simple)
                .AddColumn(new TableColumn("#").RightAligned())
                .AddColumn("Title")
                .AddColumn(new TableColumn("Score").RightAligned())
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
                    $"[cyan]{item.RelevanceScore:F2}[/]",
                    $"[grey]{item.Source}[/]",
                    $"[grey]{fetchedAge}[/]");
                rank++;
            }

            AnsiConsole.Write(evidenceTable);
        }

        // Generate answer
        var topEvidence = evidence.Take(_options.TopK).ToList();
        var sourceIds = topEvidence.Select(e => e.Id).ToList();
        string answer;

        if (ollamaAvailable)
        {
            answer = await GenerateAnswer(question, topEvidence, history, ollama, ct);
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Found {topEvidence.Count} relevant items:\n");
            foreach (var item in topEvidence)
            {
                sb.AppendLine($"- **{item.Title}** ({item.RelevanceScore:F2})");
                if (!string.IsNullOrEmpty(item.Summary))
                    sb.AppendLine($"  {item.Summary}");
                if (!string.IsNullOrEmpty(item.Url))
                    sb.AppendLine($"  {item.Url}");
            }
            answer = sb.ToString();
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

    private static async Task<string> GenerateAnswer(
        string question,
        List<ContentItem> evidence,
        List<(string question, string answer, List<string> sourceIds)> history,
        OllamaService ollama,
        CancellationToken ct)
    {
        var evidenceBlock = new StringBuilder();
        var citableEvidence = evidence
            .Where(e => !string.IsNullOrEmpty(e.Url) &&
                        !e.Url.Contains("news.google.com/rss/articles/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        for (var ei = 0; ei < citableEvidence.Count; ei++)
        {
            var item = citableEvidence[ei];
            evidenceBlock.AppendLine($"\n[E{ei + 1}] ### {item.Title}");
            evidenceBlock.AppendLine($"URL: {item.Url}");
            evidenceBlock.AppendLine($"Source: {item.Source} | Relevance: {item.RelevanceScore:F2} | Fetched: {item.FetchedAt:yyyy-MM-dd HH:mm} UTC");

            var content = item.Content ?? item.Summary ?? "";
            if (content.Length > 600)
                content = content[..600] + "...";
            if (!string.IsNullOrEmpty(content))
                evidenceBlock.AppendLine($"CONTENT: {content}");
        }

        var conversationContext = new StringBuilder();
        if (history.Count > 0)
        {
            conversationContext.AppendLine("PRIOR CONVERSATION (for context on follow-up questions):");
            foreach (var (q, a, _) in history.TakeLast(3))
            {
                conversationContext.AppendLine($"Q: {q}");
                var truncAnswer = a.Length > 300 ? a[..300] + "..." : a;
                conversationContext.AppendLine($"A: {truncAnswer}");
            }
            conversationContext.AppendLine();
        }

        var prompt = PromptTemplateService.Render("ask-answer", new Dictionary<string, object?>
        {
            ["CONVERSATION_CONTEXT"] = conversationContext.ToString(),
            ["QUESTION"] = question,
            ["TODAY"] = DateTime.Now.ToString("MMMM d, yyyy"),
            ["EVIDENCE"] = evidenceBlock.ToString()
        });

        return await ollama.GenerateAsync(prompt, null, 0.4, ct);
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
