using System.ComponentModel;
using System.Text;
using DoomSummarizer.Helpers;
using DoomSummarizer.Models;
using DoomSummarizer.Services;
using Mostlylucid.DocSummarizer.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DoomSummarizer.Commands;

/// <summary>
/// Interactive Q&A over stored evidence. Provides a chat loop with multi-turn
/// conversation, semantic search, and evidence-grounded answers.
/// </summary>
public sealed class AskCommand : AsyncCommand<AskCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[question]")]
        [Description("Initial question to ask (enters interactive mode after answering)")]
        public string? Question { get; init; }

        [CommandOption("-s|--source")]
        [Description("Filter to a source (e.g., crawl:mysite, hn, reddit)")]
        public string? Source { get; init; }

        [CommandOption("-n|--name")]
        [Description("Query a named knowledge base collection (shorthand for --source crawl:<name>)")]
        public string? Name { get; init; }

        [CommandOption("--days <DAYS>")]
        [Description("How far back to search (default: 30 for general, 365 for crawl sources)")]
        [DefaultValue(0)]
        public int Days { get; init; }

        [CommandOption("--top <N>")]
        [Description("Number of evidence items to use (default: 10)")]
        [DefaultValue(10)]
        public int TopK { get; init; } = 10;

        [CommandOption("--once")]
        [Description("Answer once and exit (no interactive loop)")]
        public bool Once { get; init; }

        [CommandOption("-q|--quiet")]
        [Description("Hide evidence details, show only the answer")]
        public bool Quiet { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        await using var boot = await CommandBootstrap.CreateAsync(cancellationToken);
        var ollama = boot.CreateOllama();
        var llmRouter = await boot.InitializeLlmStackAsync(ct: cancellationToken);

        // Initialize entity store for entity profile HNSW search
        try { await boot.InitializeEntityGraphStoreAsync(); }
        catch { /* Entity store is optional */ }

        var ollamaAvailable = await ollama.IsAvailableAsync();
        var hasCloudLlm = llmRouter.HasCloudProvider;
        if (!ollamaAvailable && !hasCloudLlm)
        {
            AnsiConsole.MarkupLine("[yellow]No LLM available (Ollama down, no cloud keys).[/] Answers will be limited to evidence listing.");
            AnsiConsole.MarkupLine("[grey]Start Ollama: ollama serve  —or—  set OPENAI_API_KEY / ANTHROPIC_API_KEY[/]");
        }
        else if (!ollamaAvailable && hasCloudLlm)
        {
            AnsiConsole.MarkupLine("[cyan]Ollama not available — using cloud LLM provider[/]");
        }

        // Derive effective source: --name takes priority over --source
        var effectiveSource = !string.IsNullOrWhiteSpace(settings.Name)
            ? $"crawl:{settings.Name}"
            : settings.Source;

        // Build the shared retrieval pipeline (Lucene + embedding + entity HNSW + RRF)
        var retrieval = new RetrievalPipeline(boot.Embedding, boot.Storage, boot.EntityStore);

        // Conversation history for multi-turn context
        var history = new List<(string question, string answer, List<string> sourceIds)>();

        // Show header
        if (!settings.Once)
        {
            AnsiConsole.Write(new Rule("[bold cyan]DoomSummarizer Ask Mode[/]").LeftJustified());
            AnsiConsole.MarkupLine("[grey]Interactive Q&A over your stored knowledge base.[/]");
            AnsiConsole.MarkupLine("[grey]Type your questions. Use 'quit' to exit, 'sources' to list evidence.[/]");
            AnsiConsole.WriteLine();
        }

        // First question from command line or prompt
        var question = settings.Question;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (string.IsNullOrWhiteSpace(question))
            {
                if (settings.Once) break;
                question = AnsiConsole.Prompt(
                    new TextPrompt<string>("[bold cyan]>[/]")
                        .AllowEmpty());

                if (string.IsNullOrWhiteSpace(question)) continue;
            }

            // Meta commands
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

            // Search and answer — cloud LLM counts as available
            var llmAvailable = ollamaAvailable || hasCloudLlm;
            await AnswerQuestion(question, settings, effectiveSource, retrieval,
                boot.Storage, boot.Embedding, ollama,
                llmAvailable, history, cancellationToken);

            if (settings.Once) break;
            question = null;
        }

        if (!settings.Once)
            AnsiConsole.MarkupLine("\n[grey]Goodbye.[/]");

        return 0;
    }

    private static async Task AnswerQuestion(
        string question,
        Settings settings,
        string? effectiveSource,
        RetrievalPipeline retrieval,
        StorageService storage,
        IEmbeddingService embedding,
        DoomSummarizer.Services.OllamaService ollama,
        bool ollamaAvailable,
        List<(string question, string answer, List<string> sourceIds)> history,
        CancellationToken ct)
    {
        // 1. Full retrieval pipeline: Lucene FTS + embedding HNSW + entity profiles + 6-signal RRF
        var collectionName = settings.Name ?? "default";
        var retrievalResult = await retrieval.SearchAsync(question, new RetrievalOptions
        {
            SourceFilter = effectiveSource,
            CollectionName = collectionName,
            TopK = settings.TopK * 2, // Grab extras for disambiguation/diversity
            MinRelevance = 0.15f,
            IsKnowledgeBase = true,
            UseEmbeddingDedup = true,
        }, ct);

        var evidence = retrievalResult.Items;

        if (evidence.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No matching evidence found in the knowledge base.[/]");
            AnsiConsole.MarkupLine("[grey]Try: doomsummarizer scroll \"your topic\" first to fetch content.[/]");
            return;
        }

        // 1b. Entity disambiguation
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

            if (!settings.Once)
            {
                // Interactive: present selection prompt
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
                // --once mode: auto-select highest-relevance cluster
                var best = disambiguation.Clusters
                    .OrderByDescending(c => c.AverageRelevance)
                    .First();
                evidence = best.Items;

                if (!settings.Quiet)
                    AnsiConsole.MarkupLine(
                        $"[grey]Auto-selected: {Markup.Escape(best.Label)} ({best.Items.Count} sources, method: {disambiguation.Method})[/]");
            }
        }

        // 2. Show evidence summary
        if (!settings.Quiet)
        {
            var evidenceTable = new Table()
                .Border(TableBorder.Simple)
                .AddColumn(new TableColumn("#").RightAligned())
                .AddColumn("Title")
                .AddColumn(new TableColumn("Score").RightAligned())
                .AddColumn("Source")
                .AddColumn("Fetched");

            var rank = 1;
            foreach (var item in evidence.Take(settings.TopK))
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

        // 3. Generate answer
        var topEvidence = evidence.Take(settings.TopK).ToList();
        var sourceIds = topEvidence.Select(e => e.Id).ToList();
        string answer;

        if (ollamaAvailable)
        {
            answer = await GenerateAnswer(question, topEvidence, history, ollama, ct);
        }
        else
        {
            // Fallback: just list the evidence
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

        // 4. Display answer
        AnsiConsole.WriteLine();
        var panel = new Panel(Markup.Escape(answer))
            .Header("[bold green]Answer[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Green)
            .Padding(1, 0);
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();

        // 5. Log query and update history
        history.Add((question, answer, sourceIds));
        var logEmbedding = await embedding.EmbedAsync(question, ct);
        await storage.LogQueryAsync(question, logEmbedding, null, sourceIds);
    }

    private static async Task<string> GenerateAnswer(
        string question,
        List<ContentItem> evidence,
        List<(string question, string answer, List<string> sourceIds)> history,
        DoomSummarizer.Services.OllamaService ollama,
        CancellationToken ct)
    {
        var evidenceBlock = new StringBuilder();
        // Filter out items with unresolvable URLs — can't cite what we can't link to
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

        // Build conversation context for follow-ups
        var conversationContext = new StringBuilder();
        if (history.Count > 0)
        {
            conversationContext.AppendLine("PRIOR CONVERSATION (for context on follow-up questions):");
            foreach (var (q, a, _) in history.TakeLast(3)) // last 3 exchanges
            {
                conversationContext.AppendLine($"Q: {q}");
                // Truncate prior answers to save context window
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

}
