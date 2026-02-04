using System.Text;
using DoomSummarizer.Helpers;
using DoomSummarizer.Models;
using DoomSummarizer.Services;
using Mostlylucid.DocSummarizer.Services.Onnx;
using Spectre.Console;

namespace DoomSummarizer.Commands;

/// <summary>
/// Output rendering for the scroll command: JSON, markdown panel, briefing, entities, images, email.
/// </summary>
public sealed partial class ScrollCommand
{
    /// <summary>
    /// Stream LLM synthesis tokens directly to console as they arrive.
    /// Used for perceived speed: evidence/links are shown first, then tokens stream in.
    /// Includes repetition detection to halt degenerate model output.
    /// </summary>
    private static async Task<string> RenderStreamingOutputAsync(
        IAsyncEnumerable<string> tokens, string title)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[cyan]{Markup.Escape(title)}[/]").RuleStyle("grey").LeftJustified());
        AnsiConsole.WriteLine();

        var sb = new StringBuilder();

        // Repetition detection: track recent output windows to detect looping
        const int windowSize = 120; // chars to compare
        const int maxRepetitions = 2; // stop after this many consecutive repeats
        var repetitionCount = 0;

        await foreach (var token in tokens)
        {
            Console.Write(token);
            sb.Append(token);

            // Check for repetition once we have enough output
            if (sb.Length > windowSize * 2)
            {
                var text = sb.ToString();
                var tail = text[^windowSize..];
                var preceding = text[^(windowSize * 2)..^windowSize];
                if (string.Equals(tail, preceding, StringComparison.Ordinal))
                {
                    repetitionCount++;
                    if (repetitionCount >= maxRepetitions)
                    {
                        // Truncate the repeated content and stop
                        var cleanLength = sb.Length - (windowSize * repetitionCount);
                        if (cleanLength > windowSize)
                        {
                            sb.Length = cleanLength;
                            Console.WriteLine();
                            AnsiConsole.MarkupLine("[dim](output truncated — repetition detected)[/]");
                        }
                        break;
                    }
                }
                else
                {
                    repetitionCount = 0;
                }
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule().RuleStyle("grey"));
        return sb.ToString();
    }

    private async Task RenderOutputAsync(
        Settings settings,
        CommandBootstrap boot,
        string vibe,
        string finalSummary,
        string template,
        bool isBlogTemplate,
        DigestData? templateData,
        List<ContentItem> uniqueItems,
        List<(string title, string summary, string topic, float sentiment, string url, double relevance)> analyzedItems,
        List<NerEntity> allEntities,
        List<(ContentItem item, List<NerEntity> entities)> articleEntityMap,
        bool extractEntities,
        bool ollamaAvailable,
        InterpretedPrompt? interpreted,
        int linkCacheHits,
        int linksSkippedByRelevance,
        TemplateService outputTemplates,
        HttpClient httpClient,
        bool isImageSource,
        CancellationToken ct)
    {
        // === Output (outside Progress block — no more progress bar overlap) ===
        if (settings.Json)
        {
            await RenderJsonOutputAsync(settings, boot, vibe, finalSummary, uniqueItems, analyzedItems,
                allEntities, extractEntities, ollamaAvailable, interpreted, linkCacheHits, linksSkippedByRelevance);
        }
        else if (!string.IsNullOrEmpty(settings.Output))
        {
            await File.WriteAllTextAsync(settings.Output, finalSummary, ct);
            AnsiConsole.MarkupLine($"[green]Summary saved to:[/] {FormattingHelpers.Esc(settings.Output)}");
        }
        else
        {
            AnsiConsole.WriteLine();
            var renderedMarkup = MarkdownToSpectre(finalSummary);
            var header = isBlogTemplate
                ? $"[bold cyan]{Markup.Escape(template)}[/]"
                : $"[bold cyan]Doom Scroll Digest ({vibe})[/]";
            var maxContentWidth = Math.Min(AnsiConsole.Profile.Width - 6, 94);
            var wrappedMarkup = WordWrapMarkup(renderedMarkup, maxContentWidth);
            try
            {
                AnsiConsole.Write(new Panel(wrappedMarkup)
                    .Header(header)
                    .Border(BoxBorder.Rounded)
                    .Padding(1, 1));
            }
            catch (Exception ex) when (ex.Message.Contains("color or style") || ex.Message.Contains("markup"))
            {
                // Markdown-to-Spectre conversion produced invalid markup — show raw summary
                Console.WriteLine(finalSummary);
            }

            // Deterministic sources section
            RenderSourcesUsed(analyzedItems, uniqueItems, maxContentWidth);

            // Evidence briefing (--full or --briefing)
            if ((settings.Full || settings.Briefing) && analyzedItems.Count > 0)
                await RenderEvidenceBriefingAsync(boot, uniqueItems, analyzedItems, articleEntityMap, maxContentWidth);

            // Named entities
            if (extractEntities && allEntities.Count > 0)
                RenderEntities(allEntities, articleEntityMap);

            // Knowledge graph
            if (settings.Graph && boot.VectorStore != null && boot.EntityStore != null)
            {
                var graphService = new KnowledgeGraphService(boot.VectorStore, boot.EntityStore);
                await graphService.DisplayGraphAsync(topN: 15, daysBack: 7);
            }

            // Image gallery (complete build only)
#if FEATURE_COMPLETE
            await RenderImageGalleryAsync(settings, uniqueItems, httpClient, isImageSource);
#endif
        }

        // Email (complete build only)
#if FEATURE_COMPLETE
        if (settings.SendEmail)
            await SendEmailAsync(settings, boot, vibe, finalSummary, templateData, analyzedItems, interpreted, outputTemplates, ct);
#endif
    }

    private async Task RenderJsonOutputAsync(
        Settings settings,
        CommandBootstrap boot,
        string vibe,
        string finalSummary,
        List<ContentItem> uniqueItems,
        List<(string title, string summary, string topic, float sentiment, string url, double relevance)> analyzedItems,
        List<NerEntity> allEntities,
        bool extractEntities,
        bool ollamaAvailable,
        InterpretedPrompt? interpreted,
        int linkCacheHits,
        int linksSkippedByRelevance)
    {
        var jsonQuery = interpreted?.RawPrompt ?? settings.Prompt;

        // Per-item TextRank excerpts
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

                    if (item.RelevanceScore > 0.3)
                    {
                        var dotIdx = excerpt.IndexOf(". ", StringComparison.Ordinal);
                        var leadFact = dotIdx > 20 ? excerpt[..(dotIdx + 1)] : excerpt;
                        if (leadFact.Length > 250) leadFact = leadFact[..250] + "...";
                        keyFactCandidates.Add((leadFact, item.RelevanceScore, item.Title, item.Url ?? ""));
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

        var keyFacts = keyFactCandidates
            .OrderByDescending(k => k.relevance)
            .Take(7)
            .Select(k => new { fact = k.fact, source = GetSourceFromUrl(k.url), url = k.url })
            .ToArray();

        var itemsForStats = uniqueItems.Take(settings.Limit).ToList();
        var sourceDistribution = itemsForStats.GroupBy(i => i.Source).ToDictionary(g => g.Key, g => g.Count());
        var sentimentBreakdown = new
        {
            positive = analyzedItems.Count(i => i.sentiment > 0.15f),
            neutral = analyzedItems.Count(i => i.sentiment is >= -0.15f and <= 0.15f),
            negative = analyzedItems.Count(i => i.sentiment < -0.15f)
        };

        var themeData = ExtractKeyThemes(analyzedItems, uniqueItems);

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
                sources = new { unique = sourceDistribution.Count, distribution = sourceDistribution },
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
                    && itemExcerpts.TryGetValue(contentItem.Id, out _);
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
                AnsiConsole.MarkupLine($"[green]JSON saved to:[/] {FormattingHelpers.Esc(settings.Output)}");
        }
        else
        {
            Console.WriteLine(json);
        }
    }

    private async Task RenderEvidenceBriefingAsync(
        CommandBootstrap boot,
        List<ContentItem> uniqueItems,
        List<(string title, string summary, string topic, float sentiment, string url, double relevance)> analyzedItems,
        List<(ContentItem item, List<NerEntity> entities)> articleEntityMap,
        int maxContentWidth)
    {
        Dictionary<string, List<(string name, string type, double confidence)>>? itemEntities = null;

        if (articleEntityMap.Count > 0)
        {
            itemEntities = articleEntityMap.ToDictionary(
                ae => ae.item.Id,
                ae => ae.entities.Select(e => (e.Text, e.Type, (double)e.Confidence)).ToList(),
                StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            var itemIds = uniqueItems.Select(u => u.Id).ToList();
            itemEntities = await boot.Storage.GetEntitiesForItemsAsync(itemIds);
        }

        var briefing = ExtractThemeBriefing(analyzedItems, uniqueItems, itemEntities);
        if (briefing.Themes.Count == 0) return;

        AnsiConsole.WriteLine();
        var briefingParts = new List<string>();

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

            foreach (var (snippet, eid) in theme.Snippets)
            {
                var tag = eid.HasValue ? $" [dim][[E{eid.Value}]][/]" : "";
                var truncSnippet = snippet.Length > 90 ? snippet[..87] + "..." : snippet;
                briefingParts.Add($"  [italic dim]\"{Markup.Escape(truncSnippet)}\"[/]{tag}");
            }

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

        if (briefing.MissingTopics.Count > 0)
        {
            var missing = string.Join(", ", briefing.MissingTopics.Select(t =>
                Markup.Escape(char.ToUpper(t[0]) + t[1..])));
            briefingParts.Add($"[dim]Not strongly represented: {missing}[/]");
        }

        var briefingContent = string.Join("\n", briefingParts).TrimEnd('\n');
        var wrappedBriefing = WordWrapMarkup(briefingContent, maxContentWidth);
        try
        {
            AnsiConsole.Write(new Panel(wrappedBriefing)
                .Header("[bold yellow]Evidence Briefing[/]")
                .Border(BoxBorder.Rounded)
                .Padding(1, 0));
        }
        catch (Exception ex) when (ex.Message.Contains("color or style") || ex.Message.Contains("markup"))
        {
            Console.WriteLine("Evidence Briefing:");
            Console.WriteLine(FormattingHelpers.SafeStripMarkup(briefingContent));
        }
    }

    private static void RenderEntities(
        List<NerEntity> allEntities,
        List<(ContentItem item, List<NerEntity> entities)> articleEntityMap)
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

        if (articleEntityMap.Count >= 2)
            DisplayStoryConnections(articleEntityMap);
    }

#if FEATURE_COMPLETE
    private static async Task RenderImageGalleryAsync(
        Settings settings,
        List<ContentItem> uniqueItems,
        HttpClient httpClient,
        bool isImageSource)
    {
        var effectiveShowImages = settings.ShowImages || isImageSource;
        if (!effectiveShowImages) return;

        // Restore ImageUrl for local files (not persisted in SQLite)
        foreach (var item in uniqueItems)
        {
            if (string.IsNullOrEmpty(item.ImageUrl))
            {
                var localImg = ImageService.GetLocalImagePath(item.Url);
                if (localImg != null) item.ImageUrl = localImg;
            }
        }

        var itemsWithImages = uniqueItems
            .Where(i => !string.IsNullOrEmpty(i.ImageUrl))
            .OrderByDescending(i => i.RelevanceScore)
            .Take(6)
            .ToList();

        // For remote sources, try og:image for high-scoring items without images
        if (!isImageSource)
        {
            var highScoringWithoutImages = uniqueItems
                .Where(i => string.IsNullOrEmpty(i.ImageUrl) && i.Score > 50 && !string.IsNullOrEmpty(i.Url))
                .OrderByDescending(i => i.Score)
                .Take(2);

            using var imgSvc = new ImageService(httpClient);
            foreach (var item in highScoringWithoutImages)
            {
                var ogImage = await imgSvc.FetchOgImageAsync(item.Url!);
                if (!string.IsNullOrEmpty(ogImage))
                {
                    item.ImageUrl = ogImage;
                    itemsWithImages.Add(item);
                }
            }
        }

        if (itemsWithImages.Count == 0) return;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold yellow]Featured Images[/]");

        const int thumbWidth = 25;
        var perRow = Math.Max(1, (AnsiConsole.Profile.Width - 4) / (thumbWidth + 2));
        using var imageService = new ImageService(httpClient);

        for (var i = 0; i < itemsWithImages.Count; i += perRow)
        {
            var batch = itemsWithImages.Skip(i).Take(perRow).ToList();

            // Title row
            var titles = batch.Select(item =>
            {
                var t = item.Title.Length > thumbWidth
                    ? item.Title[..(thumbWidth - 1)] + "..."
                    : item.Title;
                return $"[cyan]{Markup.Escape(t.PadRight(thumbWidth))}[/]";
            });
            AnsiConsole.MarkupLine(string.Join("  ", titles));

            // Thumbnail row (side-by-side)
            var renders = new List<string>();
            foreach (var item in batch)
            {
                var localPath = ImageService.GetLocalImagePath(item.Url)
                    ?? await imageService.DownloadImageAsync(item.ImageUrl!, item.Id);
                var thumb = localPath != null
                    ? ImageService.RenderThumbnail(localPath, thumbWidth) : null;
                if (thumb != null) renders.Add(thumb);
            }
            if (renders.Count > 0)
            {
                Console.Write(ImageService.MergeHorizontal(renders));
                Console.WriteLine();
            }
            AnsiConsole.WriteLine();
        }
    }

    private static async Task SendEmailAsync(
        Settings settings,
        CommandBootstrap boot,
        string vibe,
        string finalSummary,
        DigestData? templateData,
        List<(string title, string summary, string topic, float sentiment, string url, double relevance)> analyzedItems,
        InterpretedPrompt? interpreted,
        TemplateService outputTemplates,
        CancellationToken ct)
    {
        string emailHtml;
        if (templateData != null)
        {
            emailHtml = outputTemplates.Render(templateData, boot.Config.Email.Template);
        }
        else
        {
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
        await emailService.SendAsync(emailHtml, subject, settings.EmailTo, ct);
    }
#endif
}
