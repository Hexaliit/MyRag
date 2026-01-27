# Output Templates Spec: Blog Article + Newsletter + Query-Aware Synthesis

## Problem

Current synthesis produces flat markdown regardless of query type. For history/timeline
queries, it lacks chronological structure. For topic roundups, it lacks editorial curation.
Low-signal sources (LinkedIn, Dev.to, Medium) are weighted equally with primary sources,
and Key Findings lack evidence structure (Year — Artifact — Contribution).

## Solution: Query-Aware Multi-Section Synthesis

Three new template modes that select different synthesis strategies based on query type:

1. **blog-article** — Long-form structured article with sections, intro, conclusion
2. **blog-timeline** — Timeline variant: Year → Milestone → Why it mattered
3. **blog-newsletter** — Curated weekly roundup with editorial voice

Plus: **QueryTypeDetector** that auto-selects the right synthesis prompt structure.

---

## 1. Query Type Detection

New static class `QueryTypeDetector` in `Services/`:

```
QueryType DetectQueryType(string query)
```

Returns enum:
- `Timeline` — query contains: history, evolution, timeline, origin, how did X develop, chronology
- `Comparison` — query contains: vs, versus, compare, difference between, which is better
- `Explainer` — query contains: how does X work, what is, explain, why does
- `Roundup` — query contains: this week, latest, recent, news, interesting, roundup
- `General` — default fallback

This drives:
- Which synthesis prompt template to use
- Source quality weighting adjustments
- Output structure expectations

---

## 2. Source Quality Scoring (Primary Source Boost)

New method on `RelevanceScorer` or inline in `ScrollCommand`:

For `Timeline` and `Explainer` query types, apply domain-based quality multipliers:

```
Primary (1.3x):   arxiv.org, openreview.net, aclanthology.org, neurips.cc,
                   proceedings.mlr.press, official lab blogs (openai.com/blog,
                   ai.google/research, research.facebook.com)
Standard (1.0x):  bbc.co.uk, theguardian.com, reuters.com, arstechnica.com,
                   theregister.com, techcrunch.com, nature.com, wired.com
Low-signal (0.7x): medium.com, dev.to, linkedin.com, towardsdatascience.com,
                    hackernoon.com, substack.com (unless exact domain match)
```

For `Roundup` queries, invert: news sources get 1.2x, academic gets 0.9x.

Implemented as a dictionary lookup in ScrollCommand after existing source weight stage.

---

## 3. Blog Article Synthesis (`SynthesizeBlogArticleAsync`)

### Architecture: Two-Pass Synthesis

**Pass 1: Outline Generation** (sentinel model, fast)

Prompt the sentinel LLM to produce a JSON outline from the evidence:

```json
{
  "title": "The Rise of Transformers: From Attention to AGI",
  "sections": [
    {"heading": "Before Attention: The RNN Era", "key_items": [0, 2, 5], "notes": "Cover seq2seq limitations"},
    {"heading": "Attention Is All You Need (2017)", "key_items": [1, 3], "notes": "Self-attention, parallelism"},
    ...
  ],
  "conclusion_angle": "What's next for transformer architectures"
}
```

Each section references evidence items by index. The outline ensures:
- Logical flow between sections
- Each section grounded in specific evidence
- No section depends on content not in the evidence

**Pass 2: Section-by-Section Generation** (main model)

For each section in the outline:
- Build a prompt with: section heading, assigned evidence items' full content/TextRank excerpts,
  the previous section's last paragraph (context bridge), and section-specific instructions
- Temperature: 0.5 (more creative than factual extraction but still grounded)
- Each section targets 200-400 words

Context bridge: pass the last 2-3 sentences of the previous section to maintain narrative flow.

### Timeline Variant

For `QueryType.Timeline`, the outline prompt changes to:

```
Create a chronological outline with eras/periods as sections.
Each section = one era (e.g., "2017-2018: The Transformer Revolution").
Include approximate year ranges.
```

And each section's generation prompt adds:
```
Structure this section as a timeline. For each milestone:
- Year — What happened — Why it mattered — Source
Use concrete names, dates, and artifact names (paper titles, model names).
```

### Models

```csharp
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
```

### Method Signature

```csharp
public async Task<BlogArticleResult> SynthesizeBlogArticleAsync(
    List<(string title, string summary, string topic, float sentiment, string url, double relevance)> items,
    string vibe,
    string vibePrompt,
    string query,
    QueryType queryType,
    List<ContentItem>? contentItems = null,
    Func<string, float[]>? embedder = null,
    CancellationToken ct = default)
```

---

## 4. Newsletter Synthesis (`SynthesizeNewsletterAsync`)

### Architecture: Single-Pass with Structured Output

One LLM call with a heavily structured prompt that produces editorial content:

**Prompt structure:**
```
You are writing a curated weekly newsletter called "The Doom Scroll" about {topic}.

DATE: {today}
AUDIENCE: developers/tech enthusiasts
TONE: {vibe}

TOP PICKS (select 3-5 most interesting/impactful items and write 2-3 sentence commentary):
{top items by relevance with full content snippets}

ALL ITEMS (for Quick Hits section):
{remaining items with summaries}

OUTPUT FORMAT:
1. INTRO: 2-3 sentences setting the theme for this edition
2. TOP PICKS: For each, write:
   ## [Article Title](url)
   2-3 sentences of editorial commentary explaining why this matters
3. QUICK HITS: Bullet list of remaining items, one line each
4. SIGN-OFF: 1-2 sentences looking ahead
```

### Models

```csharp
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
```

---

## 5. Template Rendering

### Extended DigestData

Add optional fields to `DigestData`:

```csharp
// Blog article sections (populated for blog-article template)
public List<DigestSection>? Sections { get; init; }
public string? ArticleTitle { get; init; }
public string? Introduction { get; init; }
public string? Conclusion { get; init; }

// Newsletter picks (populated for blog-newsletter template)
public List<DigestPick>? TopPicks { get; init; }
public List<DigestQuickHit>? QuickHits { get; init; }
public string? SignOff { get; init; }
```

### New Liquid Templates

**blog-article:**
```liquid
# {{ article_title }}

{{ introduction }}

{% for section in sections %}
## {{ section.heading }}

{{ section.content }}

{% endfor %}

## Conclusion

{{ conclusion }}

---
*Sources: {% for url in source_urls %}[{{ forloop.index }}]({{ url }}) {% endfor %}*
*Generated by DoomSummarizer | {{ vibe }} vibe*
```

**blog-timeline:** Same as blog-article but sections are timeline eras.

**blog-newsletter:**
```liquid
# The Doom Scroll — {{ date | date: "%B %d, %Y" }}

*{{ introduction }}*

---

{% for pick in top_picks %}
## [{{ pick.title }}]({{ pick.url }})
{{ pick.commentary }}
*via {{ pick.source }}*

{% endfor %}

---

### Quick Hits
{% for hit in quick_hits %}
- **[{{ hit.title }}]({{ hit.url }})** — {{ hit.one_liner }}
{% endfor %}

---

{{ sign_off }}

*Curated by DoomSummarizer*
```

---

## 6. ScrollCommand Integration

In Stage 4 (summary generation), after existing disambiguation:

```csharp
if (template is "blog-article" or "blog-timeline")
{
    var queryType = QueryTypeDetector.Detect(userQuery);
    if (template == "blog-timeline" || queryType == QueryType.Timeline)
        queryType = QueryType.Timeline;

    var blogResult = await ollama.SynthesizeBlogArticleAsync(
        analyzedItems, vibe, vibePrompt, userQuery, queryType,
        uniqueItems, embedding.Embed, ct);

    // Build DigestData with sections
    digestData = BuildBlogDigestData(blogResult, ...);
    finalSummary = outputTemplates.Render(digestData, "blog-article");
}
else if (template == "blog-newsletter")
{
    var newsletterResult = await ollama.SynthesizeNewsletterAsync(
        analyzedItems, vibe, vibePrompt, userQuery,
        uniqueItems, embedding.Embed, ct);

    digestData = BuildNewsletterDigestData(newsletterResult, ...);
    finalSummary = outputTemplates.Render(digestData, "blog-newsletter");
}
else
{
    // Existing SynthesizeSummaryAsync path
}
```

Also apply source quality scoring before synthesis when query type is Timeline or Explainer.

---

## 7. Files Changed

| File | Action | Purpose |
|------|--------|---------|
| `Services/QueryTypeDetector.cs` | NEW | Detect query intent (timeline, comparison, roundup, etc.) |
| `Services/OllamaService.cs` | MODIFY | Add SynthesizeBlogArticleAsync, SynthesizeNewsletterAsync, new models, JSON context |
| `Services/TemplateService.cs` | MODIFY | Add blog-article, blog-timeline, blog-newsletter templates; extend DigestData |
| `Commands/ScrollCommand.cs` | MODIFY | Wire template selection → synthesis method; apply source quality scoring |

---

## 8. Verification

1. `dotnet build` — zero errors
2. `dotnet test` — all existing tests pass
3. Manual: `dotnet run -- scroll "history of transformers in NLP" --template blog-article --vibe upbeat`
   - Should produce multi-section article with timeline structure (auto-detected)
4. Manual: `dotnet run -- scroll "interesting .NET articles this week on AI" --template blog-newsletter --vibe neutral`
   - Should produce curated newsletter with top picks + quick hits
5. Manual: `dotnet run -- scroll "what's the history of the bbc" --template blog-timeline --vibe upbeat`
   - Should produce timeline-structured article
6. `dotnet run -- scroll --list-templates` — should show new templates
