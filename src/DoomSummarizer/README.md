# DoomSummarizer

AI-powered news aggregator that doom-scrolls HN, Reddit, BBC, Google News and more so you don't have to.
Single binary, no API keys required. ONNX embeddings + DuckDB vectors + smart RRF ranking.

## Installation

Download the single-file binary for your platform from the [release assets](#platforms), or build from source:

```bash
# Build from source
dotnet build src/DoomSummarizer/DoomSummarizer.csproj

# Run setup to download ONNX models (~80MB one-time)
doomsummarizer setup

# Install Ollama and pull model (optional but recommended)
# https://ollama.com
ollama pull qwen2.5:3b
```

## Quick Start

```bash
# Topic-routed query (auto-detects topic, routes to right sources)
doomsummarizer scroll "new pharmaceutical news" --vibe hopeful

# Tech news from HN + Reddit + BBC Technology
doomsummarizer scroll "latest AI developments"

# Fast evaluation mode (no LLM — still does full RRF + embeddings + sentiment)
doomsummarizer scroll "climate policy updates" --no-llm --force

# Fact-checking sources
doomsummarizer scroll -s factcheck --vibe neutral
doomsummarizer scroll -s factcheck:snopes -s factcheck:politifact

# Space news
doomsummarizer scroll -s spaceflight "Mars mission"
doomsummarizer scroll "SpaceX launch" --vibe hopeful

# Earthquakes (USGS real-time seismic data)
doomsummarizer scroll -s earthquake --vibe doom
doomsummarizer scroll -s earthquake:significant_month

# Wikipedia current events + on-this-day
doomsummarizer scroll -s wiki
doomsummarizer scroll -s wiki:news -s wiki:history

# Specify sources manually
doomsummarizer scroll -s hn -s reddit -s bbc --vibe doom

# Custom vibe (arbitrary text)
doomsummarizer scroll "startup funding" --vibe "excited about innovation"

# Export to file
doomsummarizer scroll "cybersecurity news" --output report.md --template newsletter
```

## Features

- **13+ sources, zero API keys**: Google News, BBC, HN, Reddit, Guardian, Ars Technica, Verge, StackOverflow, DuckDuckGo, fact-checkers, spaceflight, earthquakes, Wikipedia
- **Smart source routing**: YAML-driven topic detection routes queries to the right sources (health -> BBC Health + fact-checkers, space -> Spaceflight News + Ars)
- **Semantic topic matching**: ONNX embedding-based fuzzy topic detection with keyword fallback
- **Multi-signal RRF ranking**: BM25 keyword match + embedding similarity + vibe alignment + freshness decay + source authority, fused via Reciprocal Rank Fusion
- **Full ranking without LLM**: `--no-llm` mode still runs embeddings, BM25, sentiment scoring, topic inference — stores all signals for later use
- **Two-phase salience filter**: Fast BM25/freshness pre-filter discards non-relevant items before expensive processing
- **Knowledge graph**: ONNX NER entity extraction with DuckDB-backed co-occurrence graph (zero LLM calls)
- **Fact-checking**: Snopes, PolitiFact, FactCheck.org, FullFact RSS feeds
- **Real-time earthquake data**: USGS GeoJSON feeds with magnitude, location, tsunami alerts
- **Spaceflight news**: NASA, ESA, SpaceX launches and events via SNAPI
- **Wikipedia current events**: Today's news, on-this-day, featured articles
- **Vibe steering**: Predefined vibes (doom, hopeful, snarky, neutral) or arbitrary text
- **One-hop link following**: Fetches linked content for richer context
- **Multiple output formats**: Console, markdown, JSON, email, newsletter, Slack
- **Extensible**: Add new sources in ~50 lines (see [Adding New Sources](#adding-new-sources))
- **Single-file deployment**: Self-contained binary, no runtime required

## Sources

All sources are free, no API keys required.

| Source | Type | Example |
|--------|------|---------|
| Google News | RSS search + topic feeds | `gnews:query` or auto-routed |
| BBC News | RSS (category feeds) | `-s bbc` or `-s bbc:health` |
| Hacker News | REST API | `-s hn` |
| Reddit | JSON API | `-s reddit` or `-s reddit:dotnet` |
| StackOverflow | REST API | `-s so` or `-s so:csharp` |
| DuckDuckGo | HTML search | `-s "search:rust programming"` |
| Guardian | RSS | `-s guardian` |
| Ars Technica | RSS | `-s ars` |
| The Verge | RSS | `-s verge` |
| **Fact Check** | RSS (Snopes, PolitiFact, FactCheck.org, FullFact) | `-s factcheck` or `-s factcheck:snopes` |
| **Spaceflight News** | REST API (NASA, ESA, SpaceX) | `-s spaceflight` or `-s space` |
| **USGS Earthquakes** | GeoJSON (real-time seismic) | `-s earthquake` or `-s earthquake:4.5_week` |
| **Wikipedia** | REST API (current events, on-this-day) | `-s wiki` or `-s wiki:news` |

Run `doomsummarizer sources` for full list and routing info.

## Vibes

- **doom**: Focus on concerning trends, vulnerabilities, layoffs
- **hopeful**: Highlight innovations, opportunities, positive developments
- **snarky**: Dry wit, hype vs reality, entertaining but informative
- **neutral**: Objective, balanced, just the facts
- **Custom**: Any text, e.g. `--vibe "excited about space exploration"`

## Architecture

```
Query -> PromptInterpreter -> SourceRouter (YAML) -> Parallel Fetchers
  -> URL/Title Dedup
  -> Phase 1 RRF (BM25 + Freshness + Authority) -> Discard bottom 25%
  -> ONNX Embeddings (always — powers ranking even without LLM)
  -> Phase 2 RRF (+ Query Similarity + Vibe Alignment)
  -> One-hop Link Following (content enrichment)
  ├─ WITH LLM: Ollama Analysis -> Segment Extraction -> LLM Synthesis
  └─ NO LLM:   Embedding Sentiment + Topic Inference + Store Signals
  -> NER Entity Extraction (ONNX, no LLM)
  -> Knowledge Graph (DuckDB) -> Template Rendering
```

### --no-llm Mode

Even without Ollama, the full signal pipeline runs:
- ONNX embeddings for all items
- BM25 + TF-IDF keyword matching
- Embedding-based sentiment scoring (cosine similarity to positive/negative anchors)
- Embedding-based topic inference (best-match against 10 topic categories)
- Full RRF ranking across all signals
- NER entity extraction (with `--entities`)
- All signals stored to SQLite for later use

### Ranking Signals (RRF Fusion)

| Signal | Weight | Phase | Description |
|--------|--------|-------|-------------|
| BM25 | 1.0 | 1 | TF-IDF keyword match against query |
| Freshness | 0.5 | 1 | Exponential decay (48h half-life) |
| Authority | 0.3 | 1 | Platform score (HN upvotes, etc.) |
| Query Similarity | 0.8 | 2 | Embedding cosine similarity to query |
| Vibe Alignment | 0.4 | 2 | Embedding cosine similarity to vibe |

Phase 1 runs without embeddings for fast discard. Phase 2 adds semantic signals after embedding.

## Examples

### Pharmaceutical News (Topic Routing)

```bash
doomsummarizer scroll "new pharmaceutical news" --no-llm --force
```

Automatically routes to Google News (health search) + BBC Health + fact-checkers. Full RRF ranking even without LLM.

### Fact-Checking with Entity Extraction

```bash
doomsummarizer scroll -s factcheck --entities --vibe neutral
```

Fetches from Snopes, PolitiFact, FactCheck.org, FullFact. Use `factcheck:snopes` to target a specific site.

### Earthquake Doom

```bash
doomsummarizer scroll -s earthquake --vibe doom
doomsummarizer scroll -s earthquake:significant_month --vibe doom
```

Available feeds: `significant_week`, `significant_month`, `4.5_day`, `4.5_week`, `2.5_day`, `all_hour`, `all_day`.

### Space News

```bash
doomsummarizer scroll -s spaceflight "Mars mission" --vibe hopeful
```

### Wikipedia Current Events

```bash
doomsummarizer scroll -s wiki:news -s wiki:history --no-llm
```

Sections: `news` (In the news), `history` (On this day), `featured` (Featured article).

### Hacker News + BBC with Snarky Vibe

```bash
doomsummarizer scroll -s hn -s bbc --vibe snarky --force
```

### Reddit Programming

```bash
doomsummarizer scroll -s reddit:programming --vibe doom --entities
```

## Flags

| Flag | Description |
|------|-------------|
| `--vibe` | Set mood: doom, hopeful, snarky, neutral, or custom text |
| `--source` | Override sources (hn, reddit, bbc, gnews:query, etc.) |
| `--limit N` | Maximum items to fetch (default: 30) |
| `--force` | Ignore cache and fetch fresh |
| `--no-llm` | Skip LLM — still runs embeddings, BM25, sentiment, topic inference |
| `--entities` | Enable NER entity extraction |
| `--graph` | Enable knowledge graph build and display |
| `--no-links` | Skip one-hop link following |
| `--output FILE` | Export to file (.md, .json, .html, .txt) |
| `--template` | Output template: default, console, compact, detailed, email, newsletter, slack, json |
| `--json` | Output as JSON (for automation/LLM tools) |
| `--raw` | Show raw fetched content before processing |
| `--images` | Display inline thumbnails |
| `-q, --quiet` | Minimal output |

## Configuration

Config file: `~/.doomsummarizer/config.json`

```bash
doomsummarizer config     # Show/edit configuration
doomsummarizer sources    # List available sources and routing
```

Custom templates: place `.liquid` files in `~/.doomsummarizer/templates/`.

## Model Selection

Tested with local Ollama:

| Model | Speed | Quality | Recommendation |
|-------|-------|---------|----------------|
| qwen2.5:1.5b | ~3s | Good | Fastest option |
| qwen2.5:3b | ~5s | Better | **Default (balanced)** |
| gemma3:4b | ~8s | Good | Alternative |
| llama3.2:3b | ~12s | Good | Slower |

## Commands

```bash
doomsummarizer scroll     # Main summarization command
doomsummarizer setup      # Download ONNX models, setup Playwright
doomsummarizer trends     # Show trends over time
doomsummarizer config     # Show/edit configuration
doomsummarizer sources    # List available sources and routing info
```

## Adding New Sources

DoomSummarizer is designed to make adding new no-auth API sources straightforward. Here's the pattern:

### 1. Create a Fetcher (`Services/MyFetcher.cs`)

```csharp
public class MyFetcher(HttpClient httpClient)
{
    public async Task<List<ContentItem>> FetchAsync(int limit = 20, string? query = null)
    {
        var items = new List<ContentItem>();
        try
        {
            // Fetch from your API (REST, RSS, GeoJSON, etc.)
            var response = await httpClient.GetStringAsync("https://api.example.com/items");
            // Parse and convert to ContentItem
            items.Add(new ContentItem
            {
                Id = $"mysource_{uniqueId}",     // Prefix with source name
                Source = "mysource",              // Source identifier
                Title = "...",
                Url = "...",
                Content = "...",                  // As much text as possible for BM25
                Author = "...",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning: MySource failed: {ex.Message}[/]");
        }
        return items.Take(limit).ToList();
    }
}
```

### 2. Register in ScrollCommand (`Commands/ScrollCommand.cs`)

Add a dispatch branch in the source matching block:

```csharp
else if (src == "mysource" || src.StartsWith("mysource:"))
{
    var query = src.Contains(':') ? src.Split(':')[1] : null;
    fetchTasks.Add(Task.Run(async () =>
    {
        var fetcher = new MyFetcher(httpClient);
        return await fetcher.FetchAsync(perSourceLimit, query);
    }));
}
```

### 3. Add to Topic Routing (`Resources/sources.yaml`)

```yaml
sources:
  mysource:
    type: api
    description: "My data source (no auth)"

routing:
  mytopic:
    sources: [mysource, google_news, bbc]

topic_keywords:
  mytopic: [keyword1, keyword2, keyword3]
```

### 4. Add to topic-aware sources (if pre-filtered)

In ScrollCommand.cs, add to `topicAwareSources` set so the topic filter doesn't remove your items.

### Finding APIs

No-auth public APIs: [github.com/public-api-lists/public-api-lists](https://github.com/public-api-lists/public-api-lists)

Good candidates for news aggregation:
- RSS feeds (most news sites)
- Government data APIs (USGS, NASA, EPA, FDA)
- Open data portals (data.gov, eurostat)
- Research APIs (arXiv, PubMed, Crossref)
- Social/community APIs (Lobsters, Dev.to)

## Requirements

- **Ollama** (optional): For LLM-powered summaries. Without it, `--no-llm` mode works fully with RRF ranking.
- **No API keys**: All sources use free RSS/REST APIs. Embeddings and NER run locally via ONNX.

## Platforms

- Windows x64/ARM64
- Linux x64/ARM64
- macOS x64/ARM64 (Apple Silicon)

## License

MIT
