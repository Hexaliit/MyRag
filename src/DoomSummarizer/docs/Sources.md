# Sources and `-s/--source` syntax

`doomsummarizer scroll` builds its fetch list from:

- `-s/--source` values you pass (repeatable)
- Sources inferred from the prompt (topic routing via `Resources/sources.yaml`)
- Search queries inferred from the prompt (added as `search:<query>`)

Use `doomsummarizer sources` for a quick table, or this doc for the complete reference.

## Local sources (your knowledge base)

- `crawl:<name>`: items produced by `doomsummarizer crawl … --name <name>`
- `page`: items created by `doomsummarizer page …`

Local examples:

```bash
doomsummarizer scroll --local -s crawl:docs "how does auth work?"
doomsummarizer ask --source crawl:docs "what are the rate limits?"
```

## News / RSS sources (no API keys)

Most RSS/news sources support optional filtering via `source:<query-or-category>`. For sources with category feeds
defined in `Resources/sources.yaml` (notably BBC, Guardian, CNN, NPR, Reuters proxy), passing a known category name
selects that category feed.

Examples:

```bash
doomsummarizer scroll -s bbc
doomsummarizer scroll -s bbc:health
doomsummarizer scroll -s guardian:technology
doomsummarizer scroll -s reuters:business
```

Built-in RSS/news sources:

- `bbc`, `guardian`, `cnn`, `reuters`
- `ars`, `verge`, `wired`, `techcrunch`, `theregister`
- `npr`, `sciencedaily`, `phys`, `carbonbrief`
- `lobsters`, `devto`, `hackernoon`, `slashdot`
- `engadget`, `zdnet`, `thenextweb`, `nytimes`
- `mostlylucid`, `theonion`, `babylonbee`

Tip: run `doomsummarizer scroll --debug` to see which sources were detected and used for your prompt.

## Community / Q&A sources (no API keys)

- `hn`: Hacker News (uses sections configured in `config.json`)
- `reddit`: Reddit using configured subreddits
- `reddit:<sub>`: a specific subreddit
- `so`: StackOverflow “hot”
- `so:<tag>`: by tag
- `so:search:<query>`: StackOverflow search

Examples:

```bash
doomsummarizer scroll -s hn -s reddit:dotnet
doomsummarizer scroll -s so:csharp -s "so:search:async await"
```

## Search sources (mix of free + optional API keys)

### `gnews:*` (no API key)

- `gnews:<query>`: Google News RSS search
- `gnews_topic:<TOPIC>`: Google News topic feeds

Examples:

```bash
doomsummarizer scroll -s "gnews:ai regulation"
doomsummarizer scroll -s gnews_topic:HEALTH
```

### `search:<query>` (auto-selects best provider)

`search:<query>` chooses the best configured provider in this priority order:

1. Google Custom Search (`google_search`) if configured
2. Brave Search (`brave_search`) if configured
3. Serper (`serper`) if configured
4. Tavily (`tavily`) if configured
5. DuckDuckGo HTML (no key) as a final fallback

Example:

```bash
doomsummarizer scroll -s "search:rust async runtimes"
```

### Force a specific API-backed provider

These require API keys (see `docs/Config.md`):

- `gsearch:<query>`: Google Custom Search API (`DOOM_GOOGLE_SEARCH` + `DOOM_GOOGLE_SEARCH_CX`)
- `gplaces:<query>`: Google Places Text Search (`DOOM_GOOGLE_PLACES` or shared Google Search key)
- `brave:<query>` / `bravenews:<query>`: Brave web/news search (`DOOM_BRAVE_SEARCH`)
- `serper:<query>` / `serpernews:<query>`: Serper web/news search (`DOOM_SERPER`)
- `tavily:<query>`: Tavily search (`DOOM_TAVILY`)
- `newsapi:<query>`: NewsAPI.org (`DOOM_NEWSAPI`)
- `newsdata:<query>`: NewsData.io (`DOOM_NEWSDATA`)
- `jina:<query>`: Jina AI search (`DOOM_JINA`)

Examples:

```bash
doomsummarizer scroll -s "bravenews:ai regulation"
doomsummarizer scroll -s "serper:site:reuters.com semiconductor export controls"
doomsummarizer scroll -s "newsapi:quantum computing"
```

## Reference + specialized sources (no API keys)

- `factcheck` or `factcheck:<site>`: Snopes / PolitiFact / FactCheck.org / FullFact
- `wikipedia` / `wiki` / `wiki:<section>`: Wikipedia “current events” style feeds
- `arxiv` / `arxiv:<query>` / `arxiv:cat:<category>`: arXiv search or category browse
- `earthquake` / `quake` / `earthquake:<feed>`: USGS earthquake feeds
- `spaceflight` / `space`: Spaceflight News API

Examples:

```bash
doomsummarizer scroll -s factcheck:snopes -s "gnews:viral claim"
doomsummarizer scroll -s wiki:news
doomsummarizer scroll -s "arxiv:llm security" -s "arxiv:cat:cs.AI"
doomsummarizer scroll -s earthquake:significant_week
doomsummarizer scroll -s spaceflight
```

## Arbitrary URLs

You can pass URLs as sources:

- RSS/Atom feeds: fetched directly
- Normal web pages: feed discovery is attempted first; otherwise DoomSummarizer scrapes the page into content items

Examples:

```bash
doomsummarizer scroll -s https://example.com/feed.xml
doomsummarizer scroll -s https://techcrunch.com
```

## Topic routing (natural language)

When you provide a prompt, DoomSummarizer uses the sentinel LLM to extract **intent**, **categories**, and **time
sensitivity**, then scores every source using metadata declared in `Resources/sources.yaml`.

### How scoring works

Each source declares two metadata fields in YAML:

- **`intent_affinity`** — per-intent scores (0–1): how well the source serves `news`, `qa`, `research`, `howto`,
  `roundup`, `deep_dive`, `search_only`, `trend`
- **`capabilities`** — tags: `search`, `knowledge`, `news`, `realtime`, `tech_only`, `archive`, `academic`, `reference`,
  `government`, `satire`

Sources are scored with:

```
score = (intentAffinity × 0.6) + (categoryMatch × 0.3) + (capabilityBonus × 0.1)
```

- **intentAffinity**: how well the source matches the detected intent (e.g., Wikipedia scores 0.95 for `qa`, 0.1 for `news`)
- **categoryMatch**: whether the source appears in routing rules for the query's topic categories
- **capabilityBonus**: extra points for capability+intent matches (e.g., `knowledge` + `qa` = +0.5, `archive` + `breaking` = -0.3)

Sources are selected by score, search sources first (with generated search queries), then feed/RSS sources.

### Hard filters

Two capability-based filters prevent irrelevant sources:

- **`tech_only`** sources (hn, lobsters, techcrunch, etc.) are excluded when the query has no technology/programming category
- **`archive`** sources (wikipedia, arxiv) are excluded for `roundup` + `today`/`breaking` time sensitivity

### Example: factual QA vs news

| Query | Intent | Top Sources |
|-------|--------|-------------|
| "How much can a swallow carry?" | `qa` | search:..., wikipedia, sciencedaily |
| "latest AI news" | `news` | gnews:..., hn, techcrunch, verge, ars |
| "latest papers on transformer scaling" | `research` | arxiv:..., gnews:..., sciencedaily |
| "earthquake just hit Turkey" | `roundup` | gnews:..., bbc, reuters, guardian (no arxiv/wikipedia) |

### Topic categories

Categories currently include:
`technology`, `programming`, `health`, `pharma`, `science`, `environment`, `climate`, `business`, `finance`, `politics`,
`world`, `entertainment`, `sports`, `ai`, `security`, `space`, `disaster`, `humor`, `satire`, `factcheck`, plus
`default`.

To see scores and selected sources, use `--debug`.

