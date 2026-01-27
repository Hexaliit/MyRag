using System.ComponentModel;
using System.Text;
using DoomSummarizer.Models;
using DoomSummarizer.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DoomSummarizer.Commands;

/// <summary>
/// Download and summarize a single web page.
/// Usage: doomsummarizer page https://example.com/article
/// </summary>
public sealed class PageCommand : AsyncCommand<PageCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<url>")]
        [Description("URL of the page to summarize")]
        public string Url { get; init; } = "";

        [CommandOption("-v|--vibe")]
        [Description("Tone: neutral, upbeat, snarky, doom, or any custom text")]
        [DefaultValue("neutral")]
        public string Vibe { get; init; } = "neutral";

        [CommandOption("-t|--template")]
        [Description("Output template: default, blog-article, blog-timeline, detailed, file, json")]
        [DefaultValue("default")]
        public string Template { get; init; } = "default";

        [CommandOption("-o|--output")]
        [Description("Output file path (.md, .txt, .html)")]
        public string? Output { get; init; }

        [CommandOption("-q|--quiet")]
        [Description("Minimal output, just the summary")]
        public bool Quiet { get; init; }

        [CommandOption("--raw")]
        [Description("Show raw extracted content before summarization")]
        public bool ShowRaw { get; init; }

        [CommandOption("--no-llm|--nollm")]
        [Description("Skip LLM — show extracted content and signals only")]
        public bool NoLlm { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var config = await ConfigService.LoadAsync();
        var dbPath = ConfigService.GetDbPath(config);

        await using var storage = new StorageService(dbPath);
        await storage.InitializeAsync();

        using var embedding = new EmbeddingService();
        var ollama = new OllamaService(config.Ollama);

        await embedding.EnsureReadyAsync(msg =>
        {
            if (!settings.Quiet)
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(msg)}[/]");
        });

        var ollamaAvailable = !settings.NoLlm && await ollama.IsAvailableAsync();
        if (!ollamaAvailable && !settings.NoLlm && !settings.Quiet)
            AnsiConsole.MarkupLine("[yellow]Ollama not available. Will show extracted content only.[/]");

        // Get vibe prompt
        var vibePrompt = config.Vibes.TryGetValue(settings.Vibe, out var vp)
            ? vp
            : $"Apply this tone: {settings.Vibe}";

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        httpClient.DefaultRequestHeaders.Add("User-Agent", "MostlyLucid-DoomSummarizer/1.0");

        ContentItem? item = null;
        ProcessedArticle? processed = null;
        string finalOutput;

        await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                // Stage 1: Fetch page
                var fetchTask = ctx.AddTask("[cyan]Fetching page[/]", maxValue: 100);

                try
                {
                    var response = await httpClient.GetAsync(settings.Url, ct);
                    response.EnsureSuccessStatusCode();
                    var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                    var html = await response.Content.ReadAsStringAsync(ct);
                    fetchTask.Value = 50;

                    // Extract readable content
                    var content = contentType.Contains("markdown") || contentType.Contains("text/plain")
                        ? html
                        : ExtractReadableContent(html);

                    var title = ExtractTitle(html) ?? new Uri(settings.Url).Host;

                    item = new ContentItem
                    {
                        Id = $"page-{Guid.NewGuid():N}",
                        Source = "page",
                        Title = title,
                        Url = settings.Url,
                        Content = content,
                        IsEnriched = true,
                        CreatedAt = DateTimeOffset.UtcNow,
                        FetchedAt = DateTimeOffset.UtcNow
                    };

                    fetchTask.Value = 100;
                    fetchTask.Description = $"[green]Fetched: {Markup.Escape(title.Length > 60 ? title[..57] + "..." : title)}[/]";
                }
                catch (Exception ex)
                {
                    fetchTask.Value = 100;
                    fetchTask.Description = $"[red]Fetch failed: {Markup.Escape(ex.Message)}[/]";
                    return;
                }

                if (item == null) return;

                // Show raw content if requested
                if (settings.ShowRaw)
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[bold yellow]Raw Extracted Content:[/]");
                    var rawPreview = item.Content?.Length > 2000
                        ? item.Content[..2000] + "..."
                        : item.Content ?? "(empty)";
                    AnsiConsole.Write(new Panel(Markup.Escape(rawPreview))
                        .Header("[cyan]Content[/]")
                        .Border(BoxBorder.Rounded));
                    AnsiConsole.WriteLine();
                }

                // Stage 2: Process through article pipeline
                var processTask = ctx.AddTask("[cyan]Processing content[/]", maxValue: 100);

                // Compute embedding
                var textToEmbed = $"{item.Title} {item.Content ?? ""}".Trim();
                item.Embedding = embedding.Embed(textToEmbed);
                processTask.Value = 30;

                // Process through article processor for segmentation
                using var articleProcessor = new ArticleProcessor();
                processed = await articleProcessor.ProcessAsync(item, ct);
                processTask.Value = 60;

                // Structural analysis
                if (item.Content != null)
                {
                    item.ContentStructure = MarkdownContentAnalyzer.Analyze(item.Content);
                }
                processTask.Value = 80;

                // Sentiment + topic via embedding (no LLM)
                var positiveAnchor = embedding.Embed(RelevanceScorer.PositiveAnchorText);
                var negativeAnchor = embedding.Embed(RelevanceScorer.NegativeAnchorText);
                var topicAnchors = RelevanceScorer.TopicAnchorTexts.ToDictionary(
                    kv => kv.Key, kv => embedding.Embed(kv.Value));

                item.SentimentScore = RelevanceScorer.ComputeEmbeddingSentiment(
                    item.Embedding, positiveAnchor, negativeAnchor);
                item.DetectedTopic = RelevanceScorer.InferTopic(item.Embedding, topicAnchors);

                processTask.Value = 100;
                processTask.Description = $"[green]Processed: {processed.Strategy}, {processed.Segments.Count} segments[/]";

                if (!settings.Quiet)
                {
                    var structure = item.ContentStructure;
                    if (structure != null)
                    {
                        AnsiConsole.MarkupLine(
                            $"[grey]Structure: {structure.ContentType}, quality={structure.QualityScore:F2}, " +
                            $"{structure.Paragraphs}p {structure.Headings}h {structure.WordCount}w[/]");
                    }
                }
            });

        if (item == null)
            return 1;

        // Stage 3: Generate summary
        if (ollamaAvailable && processed != null)
        {
            AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .Start("[cyan]Generating summary...[/]", _ =>
                {
                    // Synchronous wrapper for display; actual generation below
                });

            // Analyze article via sentinel
            var analysis = await ollama.AnalyzeProcessedArticleAsync(
                processed, vibePrompt, includeReferences: true, ct: ct);

            item.Summary = analysis.Summary;
            item.DetectedTopic = analysis.Topic;
            item.SentimentScore = analysis.Sentiment;

            var template = settings.Template.ToLowerInvariant();

            if (template is "blog-article" or "blog-timeline")
            {
                // Full blog article synthesis from single page
                var queryType = template == "blog-timeline"
                    ? QueryType.Timeline
                    : QueryTypeDetector.Detect(item.Title);

                var analyzedItems = new List<(string title, string summary, string topic, float sentiment, string url, double relevance)>
                {
                    (item.Title, analysis.Summary, analysis.Topic, analysis.Sentiment, item.Url ?? "", 1.0)
                };

                var blogResult = await ollama.SynthesizeBlogArticleAsync(
                    analyzedItems, settings.Vibe, vibePrompt,
                    item.Title, queryType,
                    [item], embedding.Embed, ct);

                var templateService = new TemplateService();
                var digestData = new DigestData
                {
                    Date = DateTimeOffset.Now,
                    Vibe = settings.Vibe,
                    Query = item.Title,
                    ArticleTitle = blogResult.Title,
                    Introduction = blogResult.Introduction,
                    Sections = blogResult.Sections
                        .Select(s => new DigestSection(s.Heading, s.Content, s.SourceUrls))
                        .ToList(),
                    Conclusion = blogResult.Conclusion,
                    SourceUrls = blogResult.SourceUrls
                };

                finalOutput = templateService.Render(digestData,
                    template == "blog-timeline" ? "blog-timeline" : "blog-article");
            }
            else
            {
                // Standard summary
                var summaryContext = new StringBuilder();
                summaryContext.AppendLine($"# {item.Title}");
                summaryContext.AppendLine($"URL: {item.Url}");
                summaryContext.AppendLine($"Topic: {analysis.Topic} | Sentiment: {analysis.Sentiment:F2}");
                summaryContext.AppendLine();

                // Key points
                if (analysis.KeyPoints.Count > 0)
                {
                    summaryContext.AppendLine("## Key Points");
                    foreach (var point in analysis.KeyPoints)
                        summaryContext.AppendLine($"- {point}");
                    summaryContext.AppendLine();
                }

                // Full summary via main model
                var contentSnippet = item.Content ?? "";
                if (contentSnippet.Length > 3000)
                {
                    contentSnippet = TextRankExtractor.ExtractKeySentences(
                        contentSnippet, embedding.Embed, maxChars: 3000);
                }

                var prompt = $"""
                    Summarize this web page thoroughly:

                    TITLE: {item.Title}
                    URL: {item.Url}
                    TOPIC: {analysis.Topic}
                    DATE: {DateTime.Now:MMMM d, yyyy}

                    CONTENT:
                    {contentSnippet}

                    INSTRUCTIONS:
                    1. Write a comprehensive summary (300-500 words)
                    2. Extract key facts, findings, and takeaways
                    3. Organize with clear headings
                    4. Include specific details (names, dates, numbers)
                    5. Apply this tone: {vibePrompt}

                    FORMAT:
                    ## Summary
                    [2-3 sentence overview]

                    ## Key Points
                    - [Specific finding or fact]
                    - [Another finding]

                    ## Details
                    [Additional context and explanation]

                    Source: {item.Url}
                    """;

                var fullSummary = await ollama.GenerateAsync(prompt, null, 0.4, ct);
                summaryContext.AppendLine(fullSummary);

                finalOutput = summaryContext.ToString();
            }
        }
        else
        {
            // No LLM fallback: show extracted signals
            var sb = new StringBuilder();
            sb.AppendLine($"# {item.Title}");
            sb.AppendLine($"URL: {item.Url}");
            sb.AppendLine($"Topic: {item.DetectedTopic} | Sentiment: {item.SentimentScore:F2}");

            if (item.ContentStructure != null)
            {
                var s = item.ContentStructure;
                sb.AppendLine($"Structure: {s.ContentType} | Quality: {s.QualityScore:F2} | Words: {s.WordCount}");
            }
            sb.AppendLine();

            // Show top segments by salience
            if (processed != null)
            {
                sb.AppendLine("## Top Segments (by salience)");
                foreach (var seg in processed.TopSegments.Take(10))
                {
                    var text = seg.Text.Length > 200 ? seg.Text[..197] + "..." : seg.Text;
                    sb.AppendLine($"- [{seg.SalienceScore:F2}] {text}");
                }
            }
            else
            {
                // Just show content preview
                var preview = item.Content?.Length > 1000
                    ? item.Content[..1000] + "..."
                    : item.Content ?? "";
                sb.AppendLine(preview);
            }

            finalOutput = sb.ToString();
        }

        // Output
        if (!string.IsNullOrEmpty(settings.Output))
        {
            await File.WriteAllTextAsync(settings.Output, finalOutput, ct);
            AnsiConsole.MarkupLine($"[green]Summary saved to:[/] {settings.Output}");
        }
        else
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Panel(ScrollCommand.MarkdownToSpectre(finalOutput))
                .Header($"[bold cyan]Page Summary ({settings.Vibe})[/]")
                .Border(BoxBorder.Rounded)
                .Padding(1, 1));
        }

        // Save to storage for future reference
        await storage.SaveItemAsync(item);

        return 0;
    }

    private static string ExtractReadableContent(string html)
    {
        // Strip HTML tags, scripts, styles for readable text extraction
        // This is a simple extraction — for complex pages, LinkFollowingService does better
        var text = html;

        // Remove scripts and styles
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"<script[^>]*>[\s\S]*?</script>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"<style[^>]*>[\s\S]*?</style>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Convert common HTML to markdown-like text
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<h1[^>]*>(.*?)</h1>", "# $1\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<h2[^>]*>(.*?)</h2>", "## $1\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<h3[^>]*>(.*?)</h3>", "### $1\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<li[^>]*>(.*?)</li>", "- $1\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<br\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<p[^>]*>(.*?)</p>", "$1\n\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

        // Strip remaining tags
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]+>", "");

        // Decode HTML entities
        text = System.Net.WebUtility.HtmlDecode(text);

        // Clean up whitespace
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{3,}", "\n\n");
        text = text.Trim();

        return text;
    }

    private static string? ExtractTitle(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, @"<title[^>]*>(.*?)</title>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        if (match.Success)
            return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value.Trim());

        // Try og:title
        var ogMatch = System.Text.RegularExpressions.Regex.Match(
            html, @"<meta\s+property=""og:title""\s+content=""([^""]+)""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (ogMatch.Success)
            return System.Net.WebUtility.HtmlDecode(ogMatch.Groups[1].Value.Trim());

        return null;
    }
}
