using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using DoomSummarizer.Helpers;
using DoomSummarizer.Models;
using DoomSummarizer.Services;
using Mostlylucid.DocSummarizer.Content;
using Mostlylucid.DocSummarizer.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DoomSummarizer.Commands;

/// <summary>
/// Download and summarize a single web page.
/// Usage: doomsummarizer page https://example.com/article
/// </summary>
public sealed partial class PageCommand : AsyncCommand<PageCommand.Settings>
{
    public sealed class Settings : ContentProcessingSettings
    {
        [CommandArgument(0, "<url>")]
        [Description("URL of the page to summarize")]
        public string Url { get; init; } = "";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        await using var boot = await CommandBootstrap.CreateAsync(ct);
        var ollama = boot.CreateOllama();

        using var processor = await ItemProcessor.CreateAsync(boot.Embedding, boot.Storage, collectionName: "default", ct: ct);

        var ollamaAvailable = !settings.NoLlm && await ollama.IsAvailableAsync();
        if (!ollamaAvailable && !settings.NoLlm && !settings.Quiet)
            AnsiConsole.MarkupLine("[yellow]Ollama not available. Will show extracted content only.[/]");

        // Resolve vibe via VibeResolver (checks lens YAML files, then config vibes, then custom text)
        var resolvedVibe = boot.VibeResolver.Resolve(settings.Vibe);
        var vibePrompt = resolvedVibe.Prompt;

        using var httpClient = HttpClientFactory.CreateDefault();

        ContentItem? item = null;
        ProcessedArticle? processed = null;
        string finalOutput;

        await ProgressHelper.RunAsync(async ctx =>
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

                    // Use named collection if specified, otherwise derive from URL
                    var source = !string.IsNullOrWhiteSpace(settings.Name)
                        ? $"page:{settings.Name}"
                        : $"page:{CollectionNaming.FromUrl(settings.Url!)}";

                    item = new ContentItem
                    {
                        Id = $"page-{Guid.NewGuid():N}",
                        Source = source,
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
                var textToEmbed = ItemProcessor.PrepareEmbeddingText(item.Title, item.Content);
                item.Embedding = await boot.Embedding.EmbedAsync(textToEmbed, ct);
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
                processor.ScoreSentimentAndTopic(item);

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

            // Deterministic analysis from top segments (no LLM for analysis)
            var topSegments = processed.TopSegments
                .OrderByDescending(s => s.SalienceScore)
                .Take(3)
                .ToList();
            var segmentSummary = topSegments.Count > 0
                ? string.Join(" ", topSegments.Select(s =>
                    s.Text.Length > 200 ? s.Text[..200] : s.Text))
                : (item.Content?.Length > 300
                    ? item.Content[..300] + "..."
                    : item.Content ?? item.Title);
            item.Summary = segmentSummary;

            // Topic and sentiment already set by embedding analysis in Stage 2
            var analysis = new ArticleAnalysis
            {
                Summary = segmentSummary,
                Topic = item.DetectedTopic ?? "general",
                Sentiment = item.SentimentScore,
                KeyPoints = topSegments.Select(s =>
                    s.Text.Length > 100 ? s.Text[..100] : s.Text).ToList(),
                Confidence = topSegments.Count > 0 ? topSegments.Average(s => s.SalienceScore) : 0.5
            };

            var template = settings.Template.ToLowerInvariant();

            // Load templates including YAML definitions
            var templateService = new TemplateService();
            var templatesDir = Path.Combine(ConfigService.GetConfigDir(), "templates");
            await templateService.LoadCustomTemplatesAsync(templatesDir);

            // Resolve YAML template definition (if any)
            var templateDef = templateService.GetDefinition(template);
            var effectiveBase = templateDef?.BaseTemplate ?? template;
            var isBlogArticle = effectiveBase is "blog-article" or "blog-timeline"
                                || template is "blog-article" or "blog-timeline";

            if (isBlogArticle)
            {
                // Full blog article synthesis from single page
                var queryType = effectiveBase == "blog-timeline" || template == "blog-timeline"
                    ? QueryType.Timeline
                    : QueryTypeDetector.Detect(item.Title);

                var analyzedItems = new List<(string title, string summary, string topic, float sentiment, string url, double relevance)>
                {
                    (item.Title, analysis.Summary, analysis.Topic, analysis.Sentiment, item.Url ?? "", 1.0)
                };

                var blogResult = await ollama.SynthesizeBlogArticleAsync(
                    analyzedItems, settings.Vibe, vibePrompt,
                    item.Title, queryType,
                    [item], text => boot.Embedding.EmbedAsync(text).GetAwaiter().GetResult(), templateDef, ct);

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

                var renderTemplate = templateDef?.Template != null ? template
                    : effectiveBase == "blog-timeline" ? "blog-timeline"
                    : "blog-article";
                finalOutput = templateService.Render(digestData, renderTemplate);
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
                        contentSnippet, text => boot.Embedding.EmbedAsync(text).GetAwaiter().GetResult(), maxChars: 3000);
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
            var renderedMarkup = ScrollCommand.MarkdownToSpectre(finalOutput);
            var maxContentWidth = Math.Min(AnsiConsole.Profile.Width - 6, 94);
            var wrappedMarkup = ScrollCommand.WordWrapMarkup(renderedMarkup, maxContentWidth);
            AnsiConsole.Write(new Panel(wrappedMarkup)
                .Header($"[bold cyan]Page Summary ({settings.Vibe})[/]")
                .Border(BoxBorder.Rounded)
                .Padding(1, 1));
        }

        // Compute keyword profile and save to storage + FTS5
        await processor.IndexItemAsync(item);

        return 0;
    }

    private static string ExtractReadableContent(string html)
    {
        // Strip HTML tags, scripts, styles for readable text extraction
        // This is a simple extraction — for complex pages, LinkFollowingService does better
        var text = html;

        // Remove scripts and styles
        text = ScriptTagRegex().Replace(text, "");
        text = StyleTagRegex().Replace(text, "");

        // Convert common HTML to markdown-like text
        text = H1TagRegex().Replace(text, "# $1\n");
        text = H2TagRegex().Replace(text, "## $1\n");
        text = H3TagRegex().Replace(text, "### $1\n");
        text = LiTagRegex().Replace(text, "- $1\n");
        text = BrTagRegex().Replace(text, "\n");
        text = PTagRegex().Replace(text, "$1\n\n");

        // Strip remaining tags
        text = HtmlTagRegex().Replace(text, "");

        // Decode HTML entities
        text = System.Net.WebUtility.HtmlDecode(text);

        // Clean up whitespace
        text = ExcessiveNewlinesRegex().Replace(text, "\n\n");
        text = text.Trim();

        return text;
    }

    private static string? ExtractTitle(string html)
    {
        var match = TitleTagRegex().Match(html);
        if (match.Success)
            return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value.Trim());

        // Try og:title
        var ogMatch = OgTitleRegex().Match(html);
        if (ogMatch.Success)
            return System.Net.WebUtility.HtmlDecode(ogMatch.Groups[1].Value.Trim());

        return null;
    }

    [GeneratedRegex(@"<script[^>]*>[\s\S]*?</script>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptTagRegex();

    [GeneratedRegex(@"<style[^>]*>[\s\S]*?</style>", RegexOptions.IgnoreCase)]
    private static partial Regex StyleTagRegex();

    [GeneratedRegex(@"<h1[^>]*>(.*?)</h1>", RegexOptions.IgnoreCase)]
    private static partial Regex H1TagRegex();

    [GeneratedRegex(@"<h2[^>]*>(.*?)</h2>", RegexOptions.IgnoreCase)]
    private static partial Regex H2TagRegex();

    [GeneratedRegex(@"<h3[^>]*>(.*?)</h3>", RegexOptions.IgnoreCase)]
    private static partial Regex H3TagRegex();

    [GeneratedRegex(@"<li[^>]*>(.*?)</li>", RegexOptions.IgnoreCase)]
    private static partial Regex LiTagRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BrTagRegex();

    [GeneratedRegex(@"<p[^>]*>(.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex PTagRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessiveNewlinesRegex();

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTagRegex();

    [GeneratedRegex(@"<meta\s+property=""og:title""\s+content=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex OgTitleRegex();
}
