using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DoomSummarizer.Models;
using Polly;
using Polly.Retry;
namespace DoomSummarizer.Services;

public partial class OllamaService
{
    private readonly HttpClient _httpClient;
    private readonly OllamaConfig _config;
    private readonly ResiliencePipeline<string> _pipeline;

    /// <summary>
    /// Optional LLM router for cloud provider fallback.
    /// When set, GenerateWithModelAsync delegates to the router, which checks budgets
    /// and tries cloud providers (OpenAI/Anthropic) before falling back to Ollama.
    /// </summary>
    public LlmRouter? Router { get; set; }

    public OllamaService(OllamaConfig config)
    {
        _config = config;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(config.BaseUrl),
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
        };

        _pipeline = new ResiliencePipelineBuilder<string>()
            .AddRetry(new RetryStrategyOptions<string>
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Exponential
            })
            .Build();
    }

    /// <summary>
    /// Get max evidence chars per item, using the cloud provider's context window if routed.
    /// </summary>
    internal int GetMaxEvidenceCharsPerItem(bool sentinel, int itemCount)
    {
        if (Router != null)
            return Router.MaxEvidenceCharsPerItem(sentinel, itemCount);

        // Local Ollama fallback: use configured context size
        // Reserve 400 tokens for compact prompt template overhead (reduced from 800)
        var ctx = sentinel ? _config.SentinelContextSize : _config.ContextSize;
        var availableTokens = ctx - 400;
        var perItem = Math.Max(100, availableTokens / Math.Max(1, itemCount));
        return (int)(perItem * 3.5);
    }

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await _httpClient.GetAsync("/api/tags", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get list of locally available Ollama model names.
    /// </summary>
    public async Task<List<string>> GetAvailableModelsAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await _httpClient.GetAsync("/api/tags", cts.Token);
            if (!response.IsSuccessStatusCode) return [];

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            var result = JsonSerializer.Deserialize(json, OllamaJsonContext.Default.OllamaTagsResponse);
            return result?.Models?.Select(m => m.Name ?? "").Where(n => n.Length > 0).ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task<string> GenerateAsync(string prompt, string? systemPrompt = null, double? temperature = null, CancellationToken ct = default)
        => await GenerateWithModelAsync(_config.Model, prompt, systemPrompt, temperature, format: null, ct);

    /// <summary>
    /// Generate using the sentinel (fast/small) model — for per-article triage.
    /// </summary>
    public async Task<string> SentinelGenerateAsync(string prompt, string? systemPrompt = null, double? temperature = null, CancellationToken ct = default)
        => await GenerateWithModelAsync(_config.SentinelModel, prompt, systemPrompt, temperature, format: null, ct);

    /// <summary>
    /// Generate using the main model with forced JSON output.
    /// Ollama's format:"json" ensures the response is always valid JSON.
    /// </summary>
    public async Task<string> GenerateJsonAsync(string prompt, string? systemPrompt = null, double? temperature = null, CancellationToken ct = default)
        => await GenerateWithModelAsync(_config.Model, prompt, systemPrompt, temperature, format: "json", ct);

    private async Task<string> GenerateWithModelAsync(string model, string prompt, string? systemPrompt = null, double? temperature = null, string? format = null, CancellationToken ct = default)
    {
        // When a cloud router is configured, delegate to it for budget-checked multi-provider fallback
        if (Router != null)
        {
            var isSentinel = model == _config.SentinelModel;
            return await Router.GenerateAsync(
                prompt, systemPrompt,
                temperature ?? _config.Temperature,
                role: isSentinel ? "sentinel" : "main",
                jsonMode: format == "json",
                ct: ct);
        }

        // Direct Ollama call (default when no router)
        return await _pipeline.ExecuteAsync(async token =>
        {
            var isSentinel = model == _config.SentinelModel;
            var numCtx = isSentinel ? _config.SentinelContextSize : _config.ContextSize;

            var request = new OllamaGenerateRequest
            {
                Model = model,
                Prompt = prompt,
                System = systemPrompt,
                Stream = false,
                Format = format,
                Options = new OllamaOptions
                {
                    Temperature = temperature ?? _config.Temperature,
                    NumCtx = numCtx
                }
            };

            var json = JsonSerializer.Serialize(request, OllamaJsonContext.Default.OllamaGenerateRequest);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("/api/generate", content, token);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(token);
            var result = JsonSerializer.Deserialize(responseJson, OllamaJsonContext.Default.OllamaGenerateResponse);

            return result?.Response ?? "";
        }, ct);
    }

    /// <summary>
    /// Generate with a specific model and return timing data for benchmarking.
    /// </summary>
    public async Task<BenchmarkResult> GenerateWithTimingAsync(string model, string prompt, string? systemPrompt = null, double temperature = 0.4, CancellationToken ct = default)
    {
        var isSentinel = model == _config.SentinelModel;
        var numCtx = isSentinel ? _config.SentinelContextSize : _config.ContextSize;

        var request = new OllamaGenerateRequest
        {
            Model = model,
            Prompt = prompt,
            System = systemPrompt,
            Stream = false,
            Options = new OllamaOptions { Temperature = temperature, NumCtx = numCtx }
        };

        var json = JsonSerializer.Serialize(request, OllamaJsonContext.Default.OllamaGenerateRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await _httpClient.PostAsync("/api/generate", content, ct);
        response.EnsureSuccessStatusCode();
        sw.Stop();

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize(responseJson, OllamaJsonContext.Default.OllamaGenerateResponse);

        var evalCount = result?.EvalCount ?? 0;
        var evalDurationNs = result?.EvalDuration ?? 0;
        var tokensPerSecond = evalDurationNs > 0 ? evalCount / (evalDurationNs / 1_000_000_000.0) : 0;

        return new BenchmarkResult
        {
            Model = model,
            Response = result?.Response ?? "",
            TokensGenerated = evalCount,
            PromptTokens = result?.PromptEvalCount ?? 0,
            TokensPerSecond = tokensPerSecond,
            TotalDurationMs = result?.TotalDuration > 0 ? result.TotalDuration / 1_000_000.0 : sw.Elapsed.TotalMilliseconds,
            LoadDurationMs = result?.LoadDuration > 0 ? result.LoadDuration / 1_000_000.0 : 0,
            EvalDurationMs = evalDurationNs > 0 ? evalDurationNs / 1_000_000.0 : 0
        };
    }

    public async Task<(string summary, string topic, float sentiment)> AnalyzeContentAsync(
        string title, string? content, string vibePrompt, CancellationToken ct = default)
    {
        var maxContentChars = GetMaxEvidenceCharsPerItem(sentinel: true, 1);
        var textToAnalyze = string.IsNullOrEmpty(content)
            ? title
            : $"{title}\n\n{content[..Math.Min(content.Length, maxContentChars)]}";

        var prompt = $$"""
            Analyze this content and respond with JSON only:

            TITLE: {{title}}
            CONTENT: {{textToAnalyze}}

            Respond with this exact JSON structure:
            {
                "summary": "2-3 sentence summary",
                "topic": "single word topic category (e.g., ai, security, career, tools, language, cloud, database)",
                "sentiment": 0.0
            }

            For sentiment, use: -1.0 (very negative) to 1.0 (very positive), 0.0 is neutral.

            Vibe instruction: {{vibePrompt}}
            """;

        var response = await SentinelGenerateAsync(prompt, null, 0.3, ct);

        // Parse JSON from response
        try
        {
            // Find JSON in response
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonStr = response[jsonStart..(jsonEnd + 1)];
                var analysis = JsonSerializer.Deserialize(jsonStr, OllamaJsonContext.Default.ContentAnalysis);
                if (analysis != null)
                {
                    return (analysis.Summary ?? title, analysis.Topic ?? "general", analysis.Sentiment);
                }
            }
        }
        catch
        {
            // Fall back to raw response as summary
        }

        return (response.Length > 200 ? response[..200] : response, "general", 0f);
    }

    public async Task<string> SynthesizeSummaryAsync(
        List<(string title, string summary, string topic, float sentiment, string url, double relevance)> items,
        string vibe,
        string vibePrompt,
        string? userQuery = null,
        List<ContentItem>? contentItems = null,
        Func<string, float[]>? embedder = null,
        Func<string[], float[][]>? batchEmbedder = null,
        bool forceAnswer = false,
        string? promptTemplate = null,
        SentinelIntent? sentinelIntent = null,
        List<string>? missingTerms = null,
        CancellationToken ct = default)
    {
        var today = DateTime.Now.ToString("MMMM d, yyyy");

        // Build prompt based on whether we have a user query
        string prompt;
        if (!string.IsNullOrEmpty(userQuery))
        {
            // Query mode: include actual content snippets from the top relevant items
            // so the LLM has real material to extract facts from (not just summaries).
            // Two-stage filter: relevance score floor + semantic similarity to query.
            var queryType = QueryTypeDetector.Detect(userQuery);
            var isRoundup = !forceAnswer && queryType == QueryType.Roundup;
            var evidence = new StringBuilder();
            var sortedItems = items.OrderByDescending(i => i.relevance).ToList();
            var bestRelevance = sortedItems.FirstOrDefault().relevance;
            var relevanceFloor = Math.Max(0.15, bestRelevance * 0.30); // at least 30% of top item
            var topItems = sortedItems
                .Where(i => i.relevance >= relevanceFloor)
                // Deduplicate by URL (keep the higher-relevance duplicate)
                // For unresolved Google News URLs, deduplicate by title instead
                .GroupBy(i => i.url.Contains("news.google.com", StringComparison.OrdinalIgnoreCase)
                    ? i.title : i.url)
                .Select(g => g.First())
                .Take(15)
                .ToList();

            // Roundup source diversity: cap items per domain so no single source dominates
            if (isRoundup && topItems.Count > 5)
            {
                const int maxPerDomain = 3;
                var domainCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                topItems = topItems.Where(item =>
                {
                    var domain = GetSourceFromUrl(item.url);
                    domainCounts.TryGetValue(domain, out var count);
                    if (count >= maxPerDomain) return false;
                    domainCounts[domain] = count + 1;
                    return true;
                }).ToList();
            }

            // Confidence + re-ranking both need the query embedding — compute once.
            double? avgQuerySimilarity = null;
            float[]? queryEmb = embedder != null ? embedder(userQuery) : null;

            // Confidence: use raw pipeline embeddings (ContentItem.Embedding) which reflect
            // the actual indexed content, not re-embedded title+summary which inflates similarity.
            if (queryEmb != null && contentItems != null)
            {
                var matchedItems = topItems
                    .Select(ti => contentItems.FirstOrDefault(c => c.Url == ti.url || c.Title == ti.title))
                    .Where(c => c?.Embedding != null)
                    .Take(5)
                    .ToList();

                if (matchedItems.Count > 0)
                    avgQuerySimilarity = matchedItems
                        .Average(c => (double)VectorMath.CosineSimilarity(queryEmb, c!.Embedding!));
            }

            // Re-rank by semantic similarity to query when embedder is available.
            // This promotes the most query-relevant items to the top of the evidence
            // without discarding items (the upstream ScoreFast/ScoreFull already gated).
            if (queryEmb != null && topItems.Count > 1)
            {
                var itemTexts = topItems.Select(item => $"{item.title} {item.summary}").ToArray();

                // Batch-embed all item texts at once when batch embedder is available
                var itemEmbeddings = batchEmbedder != null
                    ? batchEmbedder(itemTexts)
                    : itemTexts.Select(t => embedder!(t)).ToArray();

                topItems = topItems
                    .Select((item, idx) =>
                    {
                        var sim = (double)VectorMath.CosineSimilarity(queryEmb, itemEmbeddings[idx]);
                        return (item, sim);
                    })
                    .OrderByDescending(x => x.sim)
                    .Take(10)
                    .Select(x => x.item)
                    .ToList();
            }
            else
            {
                topItems = topItems.Take(10).ToList();
            }

            // Smart evidence budgeting: redistribute unused chars from short items to long ones
            var totalBudget = GetMaxEvidenceCharsPerItem(sentinel: false, topItems.Count) * topItems.Count;

            // Pass 1: measure actual content lengths and resolve ContentItems
            var itemContents = new List<(
                (string title, string summary, string topic, float sentiment, string url, double relevance) item,
                string? rawContent, int rawLen)>();

            foreach (var item in topItems)
            {
                var contentItem = contentItems?.FirstOrDefault(c =>
                    c.Url == item.url || c.Title == item.title);
                var raw = contentItem?.Content;
                var len = raw?.Length ?? item.summary?.Length ?? 0;
                itemContents.Add((item, raw, len));
            }

            // Pass 2: compute per-item budgets — short items donate surplus to long ones
            var shortTotal = itemContents.Where(ic => ic.rawLen <= totalBudget / topItems.Count)
                .Sum(ic => ic.rawLen);
            var longCount = itemContents.Count(ic => ic.rawLen > totalBudget / topItems.Count);
            var remainingBudget = totalBudget - shortTotal;
            var longBudget = longCount > 0 ? remainingBudget / longCount : totalBudget / topItems.Count;

            // Pass 3: build evidence with smart budgets
            for (var ei = 0; ei < itemContents.Count; ei++)
            {
                var (item, rawContent, rawLen) = itemContents[ei];
                var isShort = rawLen <= totalBudget / topItems.Count;
                var itemBudget = isShort ? rawLen : longBudget;

                // Evidence header: numbered title only (no metadata — it leaks into LLM output)
                evidence.AppendLine($"\n[{ei + 1}] {item.title}");

                if (!string.IsNullOrEmpty(rawContent))
                {
                    if (rawContent.Length > itemBudget)
                    {
                        var snippet = embedder != null
                            ? TextRankExtractor.ExtractKeySentences(rawContent, embedder, batchEmbedder, maxChars: itemBudget)
                            : rawContent[..itemBudget] + "...";
                        evidence.AppendLine(snippet);
                    }
                    else
                    {
                        evidence.AppendLine(rawContent);
                    }
                }
                else
                {
                    evidence.AppendLine(item.summary);
                }
            }

            // Safety: if too few items survived filtering, report it honestly
            if (topItems.Count == 0)
            {
                return "### Answer\nNo relevant evidence found for this query. Try a more specific search or different sources.\n";
            }

            // Term verification is the strongest signal: if the sentinel/NER extracted
            // specific terms from the query and Lucene found zero hits in the corpus,
            // those terms are definitively not covered by the evidence.
            var confidenceNote = "";
            if (missingTerms is { Count: > 0 })
            {
                var termList = string.Join(", ", missingTerms.Select(t => $"\"{t}\""));
                confidenceNote = $"IMPORTANT: The following terms from the question do NOT appear anywhere in the source material: {termList}. " +
                    "Do NOT claim support for features, formats, or concepts that are not mentioned. " +
                    "If the question is specifically about one of these missing terms, say \"No\" or \"Not directly\" and explain what IS supported instead.";
            }
            else
            {
                // Fallback: use query similarity (semantic match) for confidence.
                // MiniLM cosine similarity ranges: strong >0.50, decent 0.30-0.50, weak <0.30
                var matchConfidence = avgQuerySimilarity ?? topItems.Average(i => i.relevance);
                confidenceNote = matchConfidence switch
                {
                    >= 0.50 => "",
                    >= 0.30 => "NOTE: The source material is only a partial match. If the question asks about a specific format, feature, or term, check whether that EXACT term appears in the evidence. Do NOT say 'Yes' for something the evidence never mentions by name.",
                    _ => $"NOTE: The source material is a weak match for this question. Start your answer with: \"I don't have a strong match for that question, but here's what I found in the {(topItems.Count == 1 ? "closest source" : $"{topItems.Count} closest sources")}:\" Then summarize what the sources actually cover."
                };
            }

            var templateVars = new Dictionary<string, object?>
            {
                ["VIBE_PROMPT"] = vibePrompt,
                ["USER_QUERY"] = userQuery,
                ["TODAY"] = today,
                ["EVIDENCE"] = evidence.ToString(),
                ["CONFIDENCE_NOTE"] = confidenceNote
            };

            // Template selection priority:
            // 1. Explicit promptTemplate (from caller, e.g. --style or manual-answer)
            // 2. Sentinel intent + query pattern (auto-detected: howto, yesno)
            // 3. Default "answer" template
            var answerTemplate = promptTemplate
                ?? QueryTypeDetector.ResolveAnswerTemplate(userQuery, sentinelIntent);
            prompt = isRoundup
                ? PromptTemplateService.Render("roundup", templateVars)
                : PromptTemplateService.Render(answerTemplate, templateVars);
        }
        else
        {
            // Digest mode: group by topic
            var byTopic = items
                .GroupBy(x => x.topic)
                .OrderByDescending(g => g.Max(i => i.relevance))
                .Take(8);

            var itemsList = new StringBuilder();
            foreach (var group in byTopic)
            {
                itemsList.AppendLine($"\n## {group.Key.ToUpperInvariant()}");
                foreach (var item in group.OrderByDescending(i => i.relevance).Take(5))
                {
                    itemsList.AppendLine($"- {item.title}: {item.summary}");
                }
            }

            prompt = PromptTemplateService.Render("digest", new Dictionary<string, object?>
            {
                ["TODAY"] = today,
                ["VIBE"] = vibe,
                ["VIBE_PROMPT"] = vibePrompt,
                ["ITEMS"] = itemsList.ToString(),
                ["USER_QUERY"] = userQuery ?? ""
            });
        }

        return await GenerateAsync(prompt, null, 0.6, ct);
    }

    /// <summary>
    /// Analyze a processed article using its top segments (signal-aware).
    /// Returns structured analysis with evidence references.
    /// </summary>
    public async Task<ArticleAnalysis> AnalyzeProcessedArticleAsync(
        ProcessedArticle article,
        string vibePrompt,
        bool includeReferences = true,
        CancellationToken ct = default)
    {
        // Build context from top segments by salience
        var segmentContext = new StringBuilder();
        var segmentRefs = new List<SegmentReference>();

        foreach (var seg in article.TopSegments.OrderByDescending(s => s.SalienceScore).Take(10))
        {
            var salience = seg.SalienceScore;
            var importance = salience > 0.8 ? "KEY" : salience > 0.5 ? "IMPORTANT" : "SUPPORTING";

            segmentContext.AppendLine($"[{importance}] {seg.Text}");

            segmentRefs.Add(new SegmentReference
            {
                SegmentId = seg.Id,
                Text = seg.Text.Length > 100 ? seg.Text[..100] + "..." : seg.Text,
                Salience = salience,
                Type = seg.Type.ToString()
            });
        }

        var prompt = $$"""
            Analyze this article using the extracted key segments:

            TITLE: {{article.Item.Title}}
            SOURCE: {{article.Item.Source}}
            URL: {{article.Item.Url ?? "N/A"}}

            KEY SEGMENTS (ranked by importance):
            {{segmentContext}}

            Respond with JSON only:
            {
                "summary": "2-3 sentence summary focusing on the KEY and IMPORTANT segments",
                "topic": "single word topic (technology, health, business, politics, science, world, entertainment, sports, security, climate, general)",
                "sentiment": 0.0,
                "keyPoints": ["point 1", "point 2"],
                "confidence": 0.0
            }

            Confidence should reflect how well the segments support a coherent summary (0.0-1.0).
            Sentiment: -1.0 (very negative) to 1.0 (very positive).

            Vibe instruction: {{vibePrompt}}
            """;

        var response = await SentinelGenerateAsync(prompt, null, 0.3, ct);

        try
        {
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonStr = response[jsonStart..(jsonEnd + 1)];
                var analysis = JsonSerializer.Deserialize(jsonStr, OllamaJsonContext.Default.ExtendedContentAnalysis);
                if (analysis != null)
                {
                    return new ArticleAnalysis
                    {
                        Summary = analysis.Summary ?? article.Item.Title,
                        Topic = analysis.Topic ?? "general",
                        Sentiment = analysis.Sentiment,
                        KeyPoints = analysis.KeyPoints ?? [],
                        Confidence = analysis.Confidence,
                        Strategy = article.Strategy,
                        SegmentReferences = includeReferences ? segmentRefs : []
                    };
                }
            }
        }
        catch
        {
            // Fall back
        }

        return new ArticleAnalysis
        {
            Summary = response.Length > 200 ? response[..200] : response,
            Topic = "general",
            Sentiment = 0f,
            Confidence = 0.5,
            Strategy = article.Strategy,
            SegmentReferences = includeReferences ? segmentRefs : []
        };
    }

    /// <summary>
    /// Synthesize a multi-section blog article using two-pass generation:
    /// 1. Sentinel generates an outline (section headings + evidence assignment)
    /// 2. Main model generates each section with context bridging
    /// </summary>
    public async Task<BlogArticleResult> SynthesizeBlogArticleAsync(
        List<(string title, string summary, string topic, float sentiment, string url, double relevance)> items,
        string vibe,
        string vibePrompt,
        string query,
        QueryType queryType,
        List<ContentItem>? contentItems = null,
        Func<string, float[]>? embedder = null,
        TemplateDefinition? templateDef = null,
        CancellationToken ct = default)
    {
        var today = DateTime.Now.ToString("MMMM d, yyyy");
        var topItems = items.OrderByDescending(i => i.relevance).Take(15).ToList();

        // Build evidence block with content snippets — budget is model-context-aware
        var outlineMaxChars = GetMaxEvidenceCharsPerItem(sentinel: true, topItems.Count);
        var evidenceBlock = new StringBuilder();
        for (var i = 0; i < topItems.Count; i++)
        {
            var item = topItems[i];
            evidenceBlock.AppendLine($"[{i}] \"{item.title}\" ({item.url})");
            var contentItem = contentItems?.FirstOrDefault(c =>
                c.Url == item.url || c.Title == item.title);
            var snippet = contentItem?.Content;
            if (!string.IsNullOrEmpty(snippet))
            {
                if (snippet.Length > outlineMaxChars)
                {
                    snippet = embedder != null
                        ? TextRankExtractor.ExtractKeySentences(snippet, embedder, maxChars: outlineMaxChars)
                        : snippet[..outlineMaxChars] + "...";
                }
                evidenceBlock.AppendLine($"    {snippet}");
            }
            else
            {
                evidenceBlock.AppendLine($"    {item.summary}");
            }
        }

        // Pass 1: Generate outline
        BlogOutline? outline = null;

        if (templateDef is { HasFixedSections: true })
        {
            // YAML-defined fixed structure — skip sentinel, use template sections
            var itemsPerSection = Math.Max(1, topItems.Count / Math.Max(1, templateDef.Sections.Count));
            var sectionIdx = 0;
            outline = new BlogOutline
            {
                Title = $"{query}",
                Sections = templateDef.Sections.Select(s =>
                {
                    var startIdx = sectionIdx * itemsPerSection;
                    var keyItems = Enumerable.Range(startIdx, Math.Min(itemsPerSection, topItems.Count - startIdx))
                        .Select(i => (int)i).ToList();
                    sectionIdx++;
                    return new BlogOutlineSection
                    {
                        Heading = s.Heading,
                        KeyItems = keyItems,
                        Notes = s.Prompt
                    };
                }).ToList(),
                ConclusionAngle = templateDef.Conclusion?.Prompt ?? "Forward-looking conclusion"
            };
        }
        else
        {
            // Sentinel-generated outline (default)
            var outlineInstructions = templateDef?.OutlineInstructions
                ?? (queryType == QueryType.Timeline
                    ? """
                      Create a CHRONOLOGICAL outline with eras/periods as sections.
                      Each section heading should include a year range (e.g., "2017-2018: The Transformer Revolution").
                      Order sections from earliest to most recent.
                      """
                    : """
                      Create a logical outline with 4-6 sections that flow naturally.
                      Start broad (context/background), go deep (key developments), end forward-looking.
                      """);

            var outlinePrompt = PromptTemplateService.Render("blog-outline", new Dictionary<string, object?>
            {
                ["QUERY"] = query,
                ["EVIDENCE"] = evidenceBlock.ToString(),
                ["OUTLINE_INSTRUCTIONS"] = outlineInstructions
            });

            try
            {
                var outlineJson = await SentinelGenerateAsync(outlinePrompt, null, 0.1, ct);
                var jsonStart = outlineJson.IndexOf('{');
                var jsonEnd = outlineJson.LastIndexOf('}');
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    outline = JsonSerializer.Deserialize(
                        outlineJson[jsonStart..(jsonEnd + 1)],
                        OllamaJsonContext.Default.BlogOutline);
                }
            }
            catch
            {
                // Fallback outline
            }
        }

        // Fallback: create a default outline if sentinel failed
        outline ??= new BlogOutline
        {
            Title = $"{query} — A Deep Dive",
            Sections =
            [
                new BlogOutlineSection { Heading = "Background", KeyItems = [0, 1, 2], Notes = "Set the scene" },
                new BlogOutlineSection { Heading = "Key Developments", KeyItems = [3, 4, 5, 6], Notes = "Main findings" },
                new BlogOutlineSection { Heading = "Current State", KeyItems = [7, 8, 9], Notes = "Where things stand" }
            ],
            ConclusionAngle = "What's next"
        };

        // Pass 2: Generate each section with context bridging
        var sections = new List<BlogSectionResult>();
        var allSourceUrls = new List<string>();
        var previousContext = "";

        // Introduction
        var introEvidence = new StringBuilder();
        foreach (var idx in outline.Sections.SelectMany(s => s.KeyItems).Distinct().Take(5))
        {
            if (idx >= 0 && idx < topItems.Count)
                introEvidence.AppendLine($"- {topItems[idx].title}: {topItems[idx].summary}");
        }

        var introExtra = templateDef?.Introduction?.Prompt
            ?? (queryType == QueryType.Timeline
                ? "Include the time span covered (e.g., 'from the 1920s to today')."
                : "");
        var introWords = templateDef?.Introduction?.TargetWords ?? 100;

        var introPrompt = PromptTemplateService.Render("blog-intro", new Dictionary<string, object?>
        {
            ["INTRO_WORDS"] = introWords.ToString(),
            ["TITLE"] = outline.Title,
            ["QUERY"] = query,
            ["TODAY"] = today,
            ["VIBE_PROMPT"] = vibePrompt,
            ["INTRO_EXTRA"] = introExtra,
            ["EVIDENCE"] = introEvidence.ToString()
        });
        var introduction = await GenerateAsync(introPrompt, null, 0.5, ct);

        // Generate each body section — evidence budget is model-context-aware
        var sectionEvidenceMaxChars = GetMaxEvidenceCharsPerItem(sentinel: false,
            outline.Sections.SelectMany(s => s.KeyItems).Distinct().Count());

        // Pair outline sections with template section defs (if available)
        var templateSections = templateDef?.Sections;

        foreach (var (section, sectionIndex) in outline.Sections.Select((s, i) => (s, i)))
        {
            var sectionEvidence = new StringBuilder();
            var sectionUrls = new List<string>();

            foreach (var idx in section.KeyItems)
            {
                if (idx < 0 || idx >= topItems.Count) continue;
                var item = topItems[idx];
                sectionUrls.Add(item.url);

                var contentItem = contentItems?.FirstOrDefault(c =>
                    c.Url == item.url || c.Title == item.title);
                var content = contentItem?.Content;
                if (!string.IsNullOrEmpty(content))
                {
                    if (content.Length > sectionEvidenceMaxChars)
                    {
                        content = embedder != null
                            ? TextRankExtractor.ExtractKeySentences(content, embedder, maxChars: sectionEvidenceMaxChars)
                            : content[..sectionEvidenceMaxChars] + "...";
                    }
                    sectionEvidence.AppendLine($"### {item.title}");
                    sectionEvidence.AppendLine($"URL: {item.url}");
                    sectionEvidence.AppendLine($"CONTENT: {content}");
                    sectionEvidence.AppendLine();
                }
                else
                {
                    sectionEvidence.AppendLine($"### {item.title} ({item.url})");
                    sectionEvidence.AppendLine($"SUMMARY: {item.summary}");
                    sectionEvidence.AppendLine();
                }
            }

            var contextBridge = !string.IsNullOrEmpty(previousContext)
                ? $"PREVIOUS SECTION ENDED WITH: \"{previousContext}\"\nMaintain narrative flow from this."
                : "";

            // Template-driven section prompt: use per-section prompt and word count if available
            var sectionDef = templateSections != null && sectionIndex < templateSections.Count
                ? templateSections[sectionIndex]
                : null;

            var sectionFocus = sectionDef?.Prompt ?? section.Notes;
            var targetWords = sectionDef?.TargetWords ?? 300;
            var wordRange = $"{Math.Max(100, targetWords - 100)}-{targetWords + 100}";

            var timelineSectionExtra = queryType == QueryType.Timeline
                ? """
                  Structure as a timeline. For key milestones use:
                  **Year — What happened** — Why it mattered (cite source)
                  Use concrete names, dates, paper titles, and model names.
                  """
                : "";

            var sectionPrompt = PromptTemplateService.Render("blog-section", new Dictionary<string, object?>
            {
                ["HEADING"] = section.Heading,
                ["QUERY"] = query,
                ["FOCUS"] = sectionFocus != null ? $"Focus: {sectionFocus}" : "",
                ["CONTEXT_BRIDGE"] = contextBridge,
                ["VIBE_PROMPT"] = vibePrompt,
                ["TIMELINE_EXTRA"] = timelineSectionExtra,
                ["EVIDENCE"] = sectionEvidence.ToString(),
                ["WORD_RANGE"] = wordRange
            });

            var sectionContent = await GenerateAsync(sectionPrompt, null, 0.5, ct);

            sections.Add(new BlogSectionResult
            {
                Heading = section.Heading,
                Content = sectionContent,
                SourceUrls = sectionUrls
            });
            allSourceUrls.AddRange(sectionUrls);

            // Extract last 2 sentences for context bridge
            var sentences = sectionContent.Split('.', StringSplitOptions.RemoveEmptyEntries);
            previousContext = sentences.Length >= 2
                ? string.Join(".", sentences[^2..]).Trim() + "."
                : sectionContent.Length > 200 ? sectionContent[^200..] : sectionContent;
        }

        // Conclusion — use template definition if available
        var conclusionExtra = templateDef?.Conclusion?.Prompt ?? "";
        var conclusionWords = templateDef?.Conclusion?.TargetWords ?? 80;
        var conclusionPrompt = PromptTemplateService.Render("blog-conclusion", new Dictionary<string, object?>
        {
            ["CONCLUSION_WORDS"] = conclusionWords.ToString(),
            ["TITLE"] = outline.Title,
            ["CONCLUSION_ANGLE"] = outline.ConclusionAngle ?? "forward-looking insights",
            ["VIBE_PROMPT"] = vibePrompt,
            ["PREVIOUS_CONTEXT"] = previousContext,
            ["CONCLUSION_EXTRA"] = conclusionExtra
        });
        var conclusion = await GenerateAsync(conclusionPrompt, null, 0.5, ct);

        return new BlogArticleResult
        {
            Title = outline.Title,
            Introduction = introduction,
            Sections = sections,
            Conclusion = conclusion,
            SourceUrls = allSourceUrls.Distinct().ToList(),
            QueryType = queryType
        };
    }

    /// <summary>
    /// Synthesize a curated newsletter with editorial commentary.
    /// </summary>
    public async Task<NewsletterResult> SynthesizeNewsletterAsync(
        List<(string title, string summary, string topic, float sentiment, string url, double relevance)> items,
        string vibe,
        string vibePrompt,
        string? query,
        List<ContentItem>? contentItems = null,
        Func<string, float[]>? embedder = null,
        CancellationToken ct = default)
    {
        var today = DateTime.Now.ToString("MMMM d, yyyy");
        var topItems = items.OrderByDescending(i => i.relevance).Take(20).ToList();
        var topPicks = topItems.Take(5).ToList();
        var quickHitItems = topItems.Skip(5).Take(10).ToList();

        // Build evidence for top picks (with full content)
        var topPicksEvidence = new StringBuilder();
        foreach (var item in topPicks)
        {
            topPicksEvidence.AppendLine($"### {item.title}");
            topPicksEvidence.AppendLine($"URL: {item.url}");
            topPicksEvidence.AppendLine($"Topic: {item.topic} | Relevance: {item.relevance:F2}");

            var contentItem = contentItems?.FirstOrDefault(c =>
                c.Url == item.url || c.Title == item.title);
            var content = contentItem?.Content;
            if (!string.IsNullOrEmpty(content))
            {
                if (content.Length > 600)
                {
                    content = embedder != null
                        ? TextRankExtractor.ExtractKeySentences(content, embedder, maxChars: 600)
                        : content[..600] + "...";
                }
                topPicksEvidence.AppendLine($"CONTENT: {content}");
            }
            else
            {
                topPicksEvidence.AppendLine($"SUMMARY: {item.summary}");
            }
            topPicksEvidence.AppendLine();
        }

        // Quick hits list
        var quickHitsList = new StringBuilder();
        foreach (var item in quickHitItems)
        {
            quickHitsList.AppendLine($"- [{item.title}]({item.url}): {item.summary}");
        }

        var topicDesc = !string.IsNullOrEmpty(query) ? $" about \"{query}\"" : "";
        var prompt = PromptTemplateService.Render("newsletter", new Dictionary<string, object?>
        {
            ["TODAY"] = today,
            ["TOPIC_DESC"] = topicDesc,
            ["VIBE_PROMPT"] = vibePrompt,
            ["TOP_PICKS_EVIDENCE"] = topPicksEvidence.ToString(),
            ["QUICK_HITS"] = quickHitsList.ToString()
        });

        var response = await GenerateAsync(prompt, null, 0.6, ct);

        // Parse structured response
        return ParseNewsletterResponse(response, topPicks, quickHitItems, query ?? "");
    }

    private static NewsletterResult ParseNewsletterResponse(
        string response,
        List<(string title, string summary, string topic, float sentiment, string url, double relevance)> topPicks,
        List<(string title, string summary, string topic, float sentiment, string url, double relevance)> quickHitItems,
        string query)
    {
        var result = new NewsletterResult { Topic = query };

        // Parse INTRO
        var introMatch = IntroRegex().Match(response);
        if (introMatch.Success)
            result = result with { Introduction = introMatch.Groups[1].Value.Trim() };

        // Parse PICKs
        var picks = new List<NewsletterPick>();
        var pickMatches = PickRegex().Matches(response);

        foreach (Match match in pickMatches)
        {
            picks.Add(new NewsletterPick
            {
                Title = match.Groups[1].Value.Trim(),
                Url = match.Groups[2].Value.Trim(),
                Source = match.Groups[3].Value.Trim(),
                Commentary = match.Groups[4].Value.Trim()
            });
        }

        // Fallback: if parsing failed, create picks from evidence
        if (picks.Count == 0)
        {
            picks = topPicks.Select(p => new NewsletterPick
            {
                Title = p.title,
                Url = p.url,
                Source = GetSourceFromUrl(p.url),
                Commentary = p.summary
            }).ToList();
        }
        result = result with { TopPicks = picks };

        // Parse QUICK_HITS
        var quickHits = new List<NewsletterQuickHit>();
        var qhMatch = QuickHitsRegex().Match(response);
        if (qhMatch.Success)
        {
            var lines = qhMatch.Groups[1].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var titleMatch = QuickHitLineRegex().Match(line);
                if (titleMatch.Success)
                {
                    quickHits.Add(new NewsletterQuickHit
                    {
                        Title = titleMatch.Groups[1].Value.Trim(),
                        Url = titleMatch.Groups[2].Value.Trim(),
                        OneLiner = titleMatch.Groups[3].Value.Trim()
                    });
                }
            }
        }

        // Fallback
        if (quickHits.Count == 0)
        {
            quickHits = quickHitItems.Select(q => new NewsletterQuickHit
            {
                Title = q.title,
                Url = q.url,
                OneLiner = q.summary.Length > 80 ? q.summary[..77] + "..." : q.summary
            }).ToList();
        }
        result = result with { QuickHits = quickHits };

        // Parse SIGN_OFF
        var signOffMatch = SignOffRegex().Match(response);
        if (signOffMatch.Success)
            result = result with { SignOff = signOffMatch.Groups[1].Value.Trim() };
        else
            result = result with { SignOff = "Until next time, keep scrolling." };

        return result;
    }

    [GeneratedRegex(@"INTRO:\s*\n(.+?)(?=\nPICK_\d|\z)", RegexOptions.Singleline)]
    private static partial Regex IntroRegex();

    [GeneratedRegex(@"PICK_\d+:\s*\nTITLE:\s*(.+?)\nURL:\s*(.+?)\nSOURCE:\s*(.+?)\nCOMMENTARY:\s*(.+?)(?=\nPICK_\d|\nQUICK_HITS|\z)", RegexOptions.Singleline)]
    private static partial Regex PickRegex();

    [GeneratedRegex(@"QUICK_HITS:\s*\n(.+?)(?=\nSIGN_OFF|\z)", RegexOptions.Singleline)]
    private static partial Regex QuickHitsRegex();

    [GeneratedRegex(@"TITLE:\s*(.+?)\s*\|\s*URL:\s*(.+?)\s*\|\s*LINE:\s*(.+)")]
    private static partial Regex QuickHitLineRegex();

    [GeneratedRegex(@"SIGN_OFF:\s*\n(.+?)$", RegexOptions.Singleline)]
    private static partial Regex SignOffRegex();

    private static string GetSourceFromUrl(string url)
    {
        try { return new Uri(url).Host.Replace("www.", ""); }
        catch { return url; }
    }

    /// <summary>
    /// Synthesize summary from multiple processed articles with evidence.
    /// </summary>
    public async Task<SynthesizedSummary> SynthesizeFromProcessedAsync(
        List<(ProcessedArticle article, ArticleAnalysis analysis)> items,
        string vibe,
        string vibePrompt,
        bool includeEvidence = true,
        CancellationToken ct = default)
    {
        // Group by topic, weighted by confidence and salience
        var byTopic = items
            .GroupBy(x => x.analysis.Topic)
            .OrderByDescending(g => g.Sum(x => x.analysis.Confidence))
            .Take(8);

        var itemsList = new StringBuilder();
        var allEvidence = new List<EvidenceItem>();

        foreach (var group in byTopic)
        {
            itemsList.AppendLine($"\n## {group.Key.ToUpperInvariant()}");

            foreach (var (article, analysis) in group.OrderByDescending(x => x.analysis.Confidence).Take(5))
            {
                var confMarker = analysis.Confidence > 0.8 ? "[HIGH-CONF] " : "";
                itemsList.AppendLine($"- {confMarker}{article.Item.Title}: {analysis.Summary}");

                // Key points as sub-items
                foreach (var point in analysis.KeyPoints.Take(2))
                {
                    itemsList.AppendLine($"  - {point}");
                }

                // Collect evidence
                if (includeEvidence && analysis.SegmentReferences.Count > 0)
                {
                    allEvidence.Add(new EvidenceItem
                    {
                        ArticleId = article.Item.Id,
                        ArticleTitle = article.Item.Title,
                        ArticleUrl = article.Item.Url,
                        Topic = analysis.Topic,
                        TopSegments = analysis.SegmentReferences.Take(3).ToList()
                    });
                }
            }
        }

        var today = DateTime.Now.ToString("MMMM d, yyyy");
        var prompt = PromptTemplateService.Render("processed-digest", new Dictionary<string, object?>
        {
            ["TODAY"] = today,
            ["VIBE"] = vibe,
            ["VIBE_PROMPT"] = vibePrompt,
            ["ITEMS"] = itemsList.ToString()
        });

        var summaryText = await GenerateAsync(prompt, null, 0.6, ct);

        return new SynthesizedSummary
        {
            Text = summaryText,
            Vibe = vibe,
            GeneratedAt = DateTimeOffset.UtcNow,
            ArticleCount = items.Count,
            TopicBreakdown = byTopic.ToDictionary(g => g.Key, g => g.Count()),
            Evidence = allEvidence
        };
    }
}

/// <summary>
/// Extended analysis result with key points and confidence.
/// </summary>
public class ArticleAnalysis
{
    public string Summary { get; init; } = "";
    public string Topic { get; init; } = "general";
    public float Sentiment { get; init; }
    public List<string> KeyPoints { get; init; } = [];
    public double Confidence { get; init; }
    public ProcessingStrategy Strategy { get; init; }
    public List<SegmentReference> SegmentReferences { get; init; } = [];
}

/// <summary>
/// Reference to a source segment (evidence).
/// </summary>
public class SegmentReference
{
    public string SegmentId { get; init; } = "";
    public string Text { get; init; } = "";
    public double Salience { get; init; }
    public string Type { get; init; } = "";
}

/// <summary>
/// Evidence linking summary to source segments.
/// </summary>
public class EvidenceItem
{
    public string ArticleId { get; init; } = "";
    public string ArticleTitle { get; init; } = "";
    public string? ArticleUrl { get; init; }
    public string Topic { get; init; } = "";
    public List<SegmentReference> TopSegments { get; init; } = [];
}

/// <summary>
/// Final synthesized summary with evidence.
/// </summary>
public class SynthesizedSummary
{
    public string Text { get; init; } = "";
    public string Vibe { get; init; } = "";
    public DateTimeOffset GeneratedAt { get; init; }
    public int ArticleCount { get; init; }
    public Dictionary<string, int> TopicBreakdown { get; init; } = new();
    public List<EvidenceItem> Evidence { get; init; } = [];
}

public record OllamaGenerateRequest
{
    [JsonPropertyName("model")] public string Model { get; init; } = "";
    [JsonPropertyName("prompt")] public string Prompt { get; init; } = "";
    [JsonPropertyName("system")] public string? System { get; init; }
    [JsonPropertyName("stream")] public bool Stream { get; init; }
    [JsonPropertyName("options")] public OllamaOptions? Options { get; init; }

    /// <summary>
    /// Output format. Set to "json" to force Ollama to output valid JSON.
    /// </summary>
    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; init; }
}

public record OllamaGenerateResponse
{
    [JsonPropertyName("response")] public string? Response { get; init; }
    [JsonPropertyName("done")] public bool Done { get; init; }
    [JsonPropertyName("total_duration")] public long TotalDuration { get; init; }
    [JsonPropertyName("load_duration")] public long LoadDuration { get; init; }
    [JsonPropertyName("prompt_eval_count")] public int PromptEvalCount { get; init; }
    [JsonPropertyName("prompt_eval_duration")] public long PromptEvalDuration { get; init; }
    [JsonPropertyName("eval_count")] public int EvalCount { get; init; }
    [JsonPropertyName("eval_duration")] public long EvalDuration { get; init; }
}

public record OllamaOptions
{
    [JsonPropertyName("temperature")] public double Temperature { get; init; }
    [JsonPropertyName("num_ctx")] public int? NumCtx { get; init; }
}

public record ContentAnalysis
{
    [JsonPropertyName("summary")] public string? Summary { get; init; }
    [JsonPropertyName("topic")] public string? Topic { get; init; }
    [JsonPropertyName("sentiment")] public float Sentiment { get; init; }
}

public record ExtendedContentAnalysis
{
    [JsonPropertyName("summary")] public string? Summary { get; init; }
    [JsonPropertyName("topic")] public string? Topic { get; init; }
    [JsonPropertyName("sentiment")] public float Sentiment { get; init; }
    [JsonPropertyName("keyPoints")] public List<string>? KeyPoints { get; init; }
    [JsonPropertyName("confidence")] public double Confidence { get; init; }
}

public record OllamaTagsResponse
{
    [JsonPropertyName("models")] public List<OllamaModelEntry>? Models { get; init; }
}

public record OllamaModelEntry
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("size")] public long Size { get; init; }
}

/// <summary>
/// Benchmark timing result from a single model run.
/// </summary>
public record BenchmarkResult
{
    public string Model { get; init; } = "";
    public string Response { get; init; } = "";
    public int TokensGenerated { get; init; }
    public int PromptTokens { get; init; }
    public double TokensPerSecond { get; init; }
    public double TotalDurationMs { get; init; }
    public double LoadDurationMs { get; init; }
    public double EvalDurationMs { get; init; }
}

/// <summary>
/// Sentinel LLM response for entity feature extraction (disambiguation).
/// </summary>
public record FeatureExtractionResponse
{
    [JsonPropertyName("items")] public List<FeatureExtractionItem>? Items { get; init; }
}

/// <summary>
/// A single extracted entity feature from the sentinel LLM.
/// </summary>
public record FeatureExtractionItem
{
    [JsonPropertyName("idx")] public int Idx { get; init; }
    [JsonPropertyName("org")] public string? Org { get; init; }
    [JsonPropertyName("loc")] public string? Loc { get; init; }
    [JsonPropertyName("industry")] public string? Industry { get; init; }
    [JsonPropertyName("desc")] public string? Desc { get; init; }
}

// --- Blog Article Models ---

/// <summary>
/// LLM-generated outline for a blog article.
/// </summary>
public record BlogOutline
{
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("sections")] public List<BlogOutlineSection> Sections { get; init; } = [];
    [JsonPropertyName("conclusion_angle")] public string? ConclusionAngle { get; init; }
}

public record BlogOutlineSection
{
    [JsonPropertyName("heading")] public string Heading { get; init; } = "";
    [JsonPropertyName("key_items")] public List<int> KeyItems { get; init; } = [];
    [JsonPropertyName("notes")] public string? Notes { get; init; }
}

/// <summary>
/// Result of multi-section blog article synthesis.
/// </summary>
public record BlogArticleResult
{
    public string Title { get; init; } = "";
    public string Introduction { get; init; } = "";
    public List<BlogSectionResult> Sections { get; init; } = [];
    public string Conclusion { get; init; } = "";
    public List<string> SourceUrls { get; init; } = [];
    public QueryType QueryType { get; init; }
}

public record BlogSectionResult
{
    public string Heading { get; init; } = "";
    public string Content { get; init; } = "";
    public List<string> SourceUrls { get; init; } = [];
}

// --- Newsletter Models ---

/// <summary>
/// Result of newsletter synthesis with editorial commentary.
/// </summary>
public record NewsletterResult
{
    public string Introduction { get; init; } = "";
    public List<NewsletterPick> TopPicks { get; init; } = [];
    public List<NewsletterQuickHit> QuickHits { get; init; } = [];
    public string SignOff { get; init; } = "";
    public string Topic { get; init; } = "";
}

public record NewsletterPick
{
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string Commentary { get; init; } = "";
    public string Source { get; init; } = "";
}

public record NewsletterQuickHit
{
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string OneLiner { get; init; } = "";
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(OllamaGenerateRequest))]
[JsonSerializable(typeof(OllamaGenerateResponse))]
[JsonSerializable(typeof(OllamaOptions))]
[JsonSerializable(typeof(ContentAnalysis))]
[JsonSerializable(typeof(ExtendedContentAnalysis))]
[JsonSerializable(typeof(OllamaTagsResponse))]
[JsonSerializable(typeof(OllamaModelEntry))]
[JsonSerializable(typeof(FeatureExtractionResponse))]
[JsonSerializable(typeof(FeatureExtractionItem))]
[JsonSerializable(typeof(List<FeatureExtractionItem>))]
[JsonSerializable(typeof(BlogOutline))]
[JsonSerializable(typeof(BlogOutlineSection))]
[JsonSerializable(typeof(List<BlogOutlineSection>))]
[JsonSerializable(typeof(SentinelIntent))]
[JsonSerializable(typeof(DateRangeIntent))]
[JsonSerializable(typeof(Dictionary<string, double>))]
[JsonSerializable(typeof(List<string>))]  // For SentinelIntent's list properties
public partial class OllamaJsonContext : JsonSerializerContext;
