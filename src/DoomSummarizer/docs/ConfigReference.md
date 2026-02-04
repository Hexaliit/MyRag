# Configuration Reference

Complete glossary of every DoomSummarizer setting.

## Loading Order

Configuration is layered — each level overrides the previous:

1. **Built-in defaults** — embedded `default-config.yaml` (ships with the app)
2. **User config** — `~/.doomsummarizer/config.json` (your personal overrides)
3. **Local config** — `./doomsummarizer.json` (project/directory overrides)
4. **Environment variables / .NET user secrets** — API keys and secrets (highest priority)

Your `config.json` only needs the settings you want to change. Everything else uses defaults.

```bash
# View the full YAML reference with comments
doomsummarizer config --reference

# Generate a minimal starter config.json
doomsummarizer config --init

# Generate a full config.json with all settings
doomsummarizer config --init --full

# Show effective config with load sources and overrides
doomsummarizer config --show
```

## Key Conventions

- **YAML** uses `snake_case` keys: `max_stories`, `base_url`, `timeout_seconds`
- **JSON** uses `camelCase` keys: `maxStories`, `baseUrl`, `timeoutSeconds`
- Both map to the same C# properties (`MaxStories`, `BaseUrl`, `TimeoutSeconds`)

---

## Settings

### sources.hacker_news

Hacker News content source.

| YAML Key                          | JSON Key                        | Type     | Default            | Description                                           |
|-----------------------------------|---------------------------------|----------|--------------------|-------------------------------------------------------|
| `sources.hacker_news.enabled`     | `sources.hackerNews.enabled`    | bool     | `true`             | Enable HN source                                      |
| `sources.hacker_news.sections`    | `sources.hackerNews.sections`   | string[] | `[top, best, new]` | HN sections to fetch (top, best, new, ask, show, job) |
| `sources.hacker_news.max_stories` | `sources.hackerNews.maxStories` | int      | `30`               | Max stories per run                                   |
| `sources.hacker_news.min_score`   | `sources.hackerNews.minScore`   | int      | `50`               | Minimum upvote threshold                              |

### sources.reddit

Reddit content source.

| YAML Key                    | JSON Key                    | Type     | Default                                                      | Description               |
|-----------------------------|-----------------------------|----------|--------------------------------------------------------------|---------------------------|
| `sources.reddit.enabled`    | `sources.reddit.enabled`    | bool     | `true`                                                       | Enable Reddit source      |
| `sources.reddit.subreddits` | `sources.reddit.subreddits` | string[] | `[programming, csharp, dotnet, ExperiencedDevs, technology]` | Subreddits to monitor     |
| `sources.reddit.sort`       | `sources.reddit.sort`       | string   | `hot`                                                        | Sort order: hot, new, top |
| `sources.reddit.max_posts`  | `sources.reddit.maxPosts`   | int      | `25`                                                         | Max posts per run         |
| `sources.reddit.min_score`  | `sources.reddit.minScore`   | int      | `20`                                                         | Minimum upvote threshold  |

### sources.websites

Custom website entries. An array of objects. Default: empty.

| YAML Key         | JSON Key        | Type    | Default | Description                                |
|------------------|-----------------|---------|---------|--------------------------------------------|
| `url`            | `url`           | string  | `""`    | Website URL to scrape                      |
| `selector`       | `selector`      | string? | `null`  | CSS selector for content extraction        |
| `use_playwright` | `usePlaywright` | bool    | `false` | Use headless browser for JS-rendered sites |

### source_filter

Global source filtering and reliability weighting.

| YAML Key                        | JSON Key                      | Type     | Default       | Description                                           |
|---------------------------------|-------------------------------|----------|---------------|-------------------------------------------------------|
| `source_filter.allowed_domains` | `sourceFilter.allowedDomains` | string[] | `[]`          | If non-empty, ONLY these domains are kept (allowlist) |
| `source_filter.blocked_domains` | `sourceFilter.blockedDomains` | string[] | `[]`          | Domains to block post-fetch                           |
| `source_filter.weights`         | `sourceFilter.weights`        | dict     | *(see below)* | RRF score multipliers per source                      |

**Default weights:**

| Source       | Weight | Notes                |
|--------------|--------|----------------------|
| reuters      | 1.4    | High-trust news wire |
| bbc          | 1.3    |                      |
| guardian     | 1.2    |                      |
| ars          | 1.2    | Ars Technica         |
| arxiv        | 1.3    | Academic papers      |
| hn           | 1.1    | Hacker News          |
| brave_news   | 1.1    |                      |
| newsapi      | 1.1    |                      |
| serper_news  | 1.1    |                      |
| brave_search | 1.0    | Neutral              |
| serper       | 1.0    |                      |
| tavily       | 1.0    |                      |
| newsdata     | 1.0    |                      |
| currents     | 1.0    |                      |
| reddit       | 0.9    | Slightly penalized   |
| jina         | 0.9    |                      |
| search       | 0.8    | Generic search       |

### ollama

Local LLM backend (Ollama).

| YAML Key                       | JSON Key                     | Type   | Default                  | Description                                |
|--------------------------------|------------------------------|--------|--------------------------|--------------------------------------------|
| `ollama.base_url`              | `ollama.baseUrl`             | string | `http://localhost:11434` | Ollama API endpoint                        |
| `ollama.model`                 | `ollama.model`               | string | `gemma3:4b`              | Primary LLM model for synthesis            |
| `ollama.sentinel_model`        | `ollama.sentinelModel`       | string | `qwen3:0.6b`             | Fast triage/sentinel model                 |
| `ollama.embed_model`           | `ollama.embedModel`          | string | `nomic-embed-text`       | Embedding model (when backend=ollama)      |
| `ollama.temperature`           | `ollama.temperature`         | double | `0.4`                    | LLM temperature (0.0-1.0)                  |
| `ollama.timeout_seconds`       | `ollama.timeoutSeconds`      | int    | `300`                    | Request timeout in seconds                 |
| `ollama.context_size`          | `ollama.contextSize`         | int    | `8192`                   | Context window (tokens) for primary model  |
| `ollama.sentinel_context_size` | `ollama.sentinelContextSize` | int    | `32768`                  | Context window (tokens) for sentinel model |

### embedding

Embedding backend configuration.

| YAML Key                         | JSON Key                        | Type   | Default            | Description                                  |
|----------------------------------|---------------------------------|--------|--------------------|----------------------------------------------|
| `embedding.backend`              | `embedding.backend`             | string | `onnx`             | Backend: `onnx` (local), `ollama`            |
| `embedding.model`                | `embedding.model`               | string | `all-MiniLM-L6-v2` | Embedding model name                         |
| `embedding.similarity_threshold` | `embedding.similarityThreshold` | double | `0.95`             | Deduplication similarity threshold (0.0-1.0) |

### output

Output formatting.

| YAML Key                    | JSON Key                  | Type   | Default    | Description                         |
|-----------------------------|---------------------------|--------|------------|-------------------------------------|
| `output.format`             | `output.format`           | string | `markdown` | Output format: markdown, html, text |
| `output.max_summary_length` | `output.maxSummaryLength` | int    | `500`      | Max chars per article summary       |
| `output.include_links`      | `output.includeLinks`     | bool   | `true`     | Include source URLs in output       |
| `output.group_by_topic`     | `output.groupByTopic`     | bool   | `true`     | Group articles by detected topic    |

### link_following

One-hop link following to enrich article content.

| YAML Key                               | JSON Key                           | Type     | Default                  | Description                 |
|----------------------------------------|------------------------------------|----------|--------------------------|-----------------------------|
| `link_following.enabled`               | `linkFollowing.enabled`            | bool     | `true`                   | Enable link following       |
| `link_following.max_links_per_article` | `linkFollowing.maxLinksPerArticle` | int      | `3`                      | Max links per article       |
| `link_following.max_total_links`       | `linkFollowing.maxTotalLinks`      | int      | `15`                     | Max total linked pages      |
| `link_following.max_content_length`    | `linkFollowing.maxContentLength`   | int      | `2000`                   | Max chars per linked page   |
| `link_following.timeout_seconds`       | `linkFollowing.timeoutSeconds`     | int      | `10`                     | Timeout per fetch (seconds) |
| `link_following.blocked_domains`       | `linkFollowing.blockedDomains`     | string[] | *(social, auth, stores)* | Domains to never follow     |
| `link_following.blocked_extensions`    | `linkFollowing.blockedExtensions`  | string[] | *(media, archives)*      | File extensions to skip     |

**Default blocked domains:** facebook.com, twitter.com, x.com, instagram.com, linkedin.com, youtube.com, tiktok.com,
accounts.google.com, play.google.com, apps.apple.com

**Default blocked extensions:** .pdf, .zip, .tar, .gz, .exe, .dmg, .png, .jpg, .jpeg, .gif, .svg, .webp, .mp3, .mp4,
.mov, .avi, .mkv

### email

Email delivery configuration. Supports SMTP and SendGrid.

| YAML Key                 | JSON Key                | Type   | Default                         | Description                    |
|--------------------------|-------------------------|--------|---------------------------------|--------------------------------|
| `email.provider`         | `email.provider`        | string | `smtp`                          | Provider: `smtp` or `sendgrid` |
| `email.enabled`          | `email.enabled`         | bool   | `false`                         | Enable email delivery          |
| `email.from_address`     | `email.fromAddress`     | string | `""`                            | Sender email address           |
| `email.from_name`        | `email.fromName`        | string | `DoomSummarizer`                | Sender display name            |
| `email.to_addresses`     | `email.toAddresses`     | string | `""`                            | Recipients (comma-separated)   |
| `email.subject_template` | `email.subjectTemplate` | string | `Doom Scroll Digest — {{DATE}}` | Subject line template          |
| `email.template`         | `email.template`        | string | `email`                         | Output template for body       |

#### email.smtp

| YAML Key              | JSON Key              | Type    | Default          | Description                    |
|-----------------------|-----------------------|---------|------------------|--------------------------------|
| `email.smtp.host`     | `email.smtp.host`     | string  | `smtp.gmail.com` | SMTP server hostname           |
| `email.smtp.port`     | `email.smtp.port`     | int     | `587`            | SMTP port                      |
| `email.smtp.use_ssl`  | `email.smtp.useSsl`   | bool    | `true`           | Use SSL/TLS                    |
| `email.smtp.username` | `email.smtp.username` | string? | `""`             | SMTP username                  |
| `email.smtp.password` | `email.smtp.password` | string? | `""`             | SMTP password (prefer env var) |

### storage

Database and retention settings.

| YAML Key                 | JSON Key                | Type   | Default                     | Description                       |
|--------------------------|-------------------------|--------|-----------------------------|-----------------------------------|
| `storage.db_path`        | `storage.dbPath`        | string | `~/.doomsummarizer/doom.db` | SQLite database path (`~` = home) |
| `storage.retention_days` | `storage.retentionDays` | int    | `30`                        | Days to keep articles             |

### plugins

Plugin management.

| YAML Key                         | JSON Key                       | Type     | Default | Description                                  |
|----------------------------------|--------------------------------|----------|---------|----------------------------------------------|
| `plugins.enable_runtime_plugins` | `plugins.enableRuntimePlugins` | bool     | `true`  | Load plugins from ~/.doomsummarizer/plugins/ |
| `plugins.auto_install`           | `plugins.autoInstall`          | string[] | `[]`    | Auto-install on first run                    |
| `plugins.disabled`               | `plugins.disabled`             | string[] | `[]`    | Plugin keys to disable                       |
| `plugins.settings`               | `plugins.settings`             | dict     | `{}`    | Per-plugin overrides                         |

**Per-plugin settings (plugins.settings.\<key\>):**

| Key         | Type | Default | Description                       |
|-------------|------|---------|-----------------------------------|
| `enabled`   | bool | `true`  | Override enabled state            |
| `max_items` | int? | `null`  | Override --limit for this source  |
| `options`   | dict | `{}`    | Plugin-specific key-value options |

### api_budget

Global budget controls across all paid APIs.

| YAML Key                                 | JSON Key                            | Type   | Default | Description                                             |
|------------------------------------------|-------------------------------------|--------|---------|---------------------------------------------------------|
| `api_budget.global_max_requests_per_day` | `apiBudget.globalMaxRequestsPerDay` | int    | `500`   | Total requests/day across all paid APIs (0 = unlimited) |
| `api_budget.global_daily_budget_usd`     | `apiBudget.globalDailyBudgetUsd`    | double | `2.0`   | Total daily spend cap in USD (0 = unlimited)            |

### vibes

Synthesis personality presets. Key = vibe name, value = prompt describing tone.

| Name       | Description                                                 |
|------------|-------------------------------------------------------------|
| `doom`     | Pessimistic, concerning. Threats, vulnerabilities, layoffs. |
| `hopeful`  | Optimistic. Innovations, tools, opportunities.              |
| `snarky`   | Sharp dry wit. Mock hype, deadpan humor.                    |
| `funny`    | Puns, absurd analogies, playful exaggeration.               |
| `upbeat`   | High energy, exclamation points, celebrate wins.            |
| `friendly` | Warm, conversational. Smart friend over coffee.             |
| `toon`     | Comic-strip energy. Sound effects, dramatic reveals.        |
| `neutral`  | Objective, balanced. Just the facts.                        |

You can add custom vibes or override built-in ones in your config.json:

```json
{
  "vibes": {
    "pirate": "Arr, matey! Every story told like a sea shanty.",
    "doom": "Even MORE pessimistic than the default doom."
  }
}
```

---

## API Keys (keys[])

Each entry in the `keys` array defines an API service with its credentials, budget, and rate limits.

### Service Definitions

| Service              | Env Var                                 | User Secret Key         | Description                |
|----------------------|-----------------------------------------|-------------------------|----------------------------|
| `google_search`      | `DOOM_GOOGLE_SEARCH`                    | `keys:0:apiKey`         | Google Custom Search API   |
| `google_search` (CX) | `DOOM_GOOGLE_SEARCH_CX`                 | `keys:0:searchEngineId` | Google Search Engine ID    |
| `google_places`      | `DOOM_GOOGLE_PLACES`                    | `keys:1:apiKey`         | Google Places API          |
| `openai`             | `DOOM_OPENAI` or `OPENAI_API_KEY`       | `keys:2:apiKey`         | OpenAI API (GPT-4o)        |
| `anthropic`          | `DOOM_ANTHROPIC` or `ANTHROPIC_API_KEY` | `keys:3:apiKey`         | Anthropic API (Claude)     |
| `brave_search`       | `DOOM_BRAVE_SEARCH`                     | `keys:4:apiKey`         | Brave Search API           |
| `serper`             | `DOOM_SERPER`                           | `keys:5:apiKey`         | Serper.dev search API      |
| `newsapi`            | `DOOM_NEWSAPI`                          | `keys:6:apiKey`         | NewsAPI.org                |
| `newsdata`           | `DOOM_NEWSDATA`                         | `keys:7:apiKey`         | NewsData.io                |
| `tavily`             | `DOOM_TAVILY`                           | `keys:8:apiKey`         | Tavily search API          |
| `jina`               | `DOOM_JINA`                             | `keys:9:apiKey`         | Jina AI reader API         |
| `duckduckgo`         | —                                       | —                       | DuckDuckGo (no key needed) |
| `currents`           | `DOOM_CURRENTS`                         | `keys:11:apiKey`        | Currents API               |

### Per-Service Budget Fields

Each key entry supports these budget/rate-limit fields:

| YAML Key                        | JSON Key                     | Type    | Default | Description                                        |
|---------------------------------|------------------------------|---------|---------|----------------------------------------------------|
| `api_key`                       | `apiKey`                     | string  | `""`    | API key (prefer env var/secrets)                   |
| `search_engine_id`              | `searchEngineId`             | string? | `""`    | Service-specific ID (e.g., Google CX, model names) |
| `enabled`                       | `enabled`                    | bool    | `true`  | Enable this service                                |
| `max_requests_per_day`          | `maxRequestsPerDay`          | int     | varies  | Daily request limit                                |
| `max_requests`                  | `maxRequests`                | int     | `0`     | Lifetime request cap (0 = unlimited)               |
| `daily_budget_usd`              | `dailyBudgetUsd`             | double  | varies  | Daily spend cap for this service                   |
| `cost_per_request`              | `costPerRequest`             | double  | varies  | Estimated cost per API call                        |
| `rate_limit_ms`                 | `rateLimitMs`                | int     | varies  | Min delay between requests (ms)                    |
| `max_retries`                   | `maxRetries`                 | int     | `2`     | Retry attempts on 429/5xx                          |
| `circuit_breaker_threshold`     | `circuitBreakerThreshold`    | int     | `3`     | Failures before circuit opens                      |
| `circuit_breaker_reset_seconds` | `circuitBreakerResetSeconds` | int     | varies  | Seconds before circuit resets                      |

### Email API Keys

| Service       | Env Var                               | User Secret    | Description                  |
|---------------|---------------------------------------|----------------|------------------------------|
| SendGrid      | `DOOM_SENDGRID` or `SENDGRID_API_KEY` | `SendGrid`     | SendGrid API key             |
| SMTP password | `DOOM_SMTP_PASSWORD`                  | `SmtpPassword` | SMTP authentication password |

---

## Environment Variables

All `DOOM_*` environment variables are resolved by `ApiKeyService` at startup:

| Variable                               | Maps To                              | Description                  |
|----------------------------------------|--------------------------------------|------------------------------|
| `DOOM_GOOGLE_SEARCH`                   | `keys[google_search].apiKey`         | Google Custom Search API key |
| `DOOM_GOOGLE_SEARCH_CX`                | `keys[google_search].searchEngineId` | Google Search Engine ID      |
| `DOOM_GOOGLE_PLACES`                   | `keys[google_places].apiKey`         | Google Places API key        |
| `DOOM_OPENAI` / `OPENAI_API_KEY`       | `keys[openai].apiKey`                | OpenAI API key               |
| `DOOM_ANTHROPIC` / `ANTHROPIC_API_KEY` | `keys[anthropic].apiKey`             | Anthropic API key            |
| `DOOM_BRAVE_SEARCH`                    | `keys[brave_search].apiKey`          | Brave Search API key         |
| `DOOM_SERPER`                          | `keys[serper].apiKey`                | Serper.dev API key           |
| `DOOM_NEWSAPI`                         | `keys[newsapi].apiKey`               | NewsAPI.org API key          |
| `DOOM_NEWSDATA`                        | `keys[newsdata].apiKey`              | NewsData.io API key          |
| `DOOM_TAVILY`                          | `keys[tavily].apiKey`                | Tavily API key               |
| `DOOM_JINA`                            | `keys[jina].apiKey`                  | Jina AI API key              |
| `DOOM_CURRENTS`                        | `keys[currents].apiKey`              | Currents API key             |
| `DOOM_SENDGRID` / `SENDGRID_API_KEY`   | `email.sendGridApiKey`               | SendGrid API key             |
| `DOOM_SMTP_PASSWORD`                   | `email.smtp.password`                | SMTP password                |

---

## Examples

### Minimal override (just change the LLM model)

```json
{
  "ollama": {
    "model": "llama3.2:3b"
  }
}
```

### Override sources and add a custom website

```json
{
  "sources": {
    "hackerNews": {
      "minScore": 100
    },
    "websites": [
      {
        "url": "https://blog.example.com/feed",
        "selector": "article"
      }
    ]
  }
}
```

### Disable Reddit entirely

```json
{
  "sources": {
    "reddit": {
      "enabled": false
    }
  }
}
```

### Use Anthropic Claude instead of local Ollama

Set your API key via environment variable:

```bash
export DOOM_ANTHROPIC="sk-ant-..."
```

Then enable the Anthropic key in config:

```json
{
  "keys": [
    { "name": "anthropic", "enabled": true }
  ]
}
```
