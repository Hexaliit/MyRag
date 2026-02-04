# DoomSummarizer Future Enhancements

Nice-to-have features for future iterations:

## Time Filtering

- `--since 24h` or `--since yesterday` - Filter content by time
- `--since 2025-01-20` - Specific date cutoff

## Output Options

- `--group-by topic` - Organize output by detected topics
- `--markdown` - Blog/newsletter-ready markdown output
- `--rss` - Generate RSS feed from digest

## Trend Analysis

- `--trend` - Compare last run vs current (delta view)
- `--compare <date>` - Compare with specific historical snapshot

## Additional Sources (Future)

- YouTube channel summaries
- Podcast RSS feeds
- Substack newsletters
- Mastodon feeds

## Quality Improvements

- Better query interpretation for special chars (C#, .NET)
- Topic clustering with embeddings
- Keyword extraction alongside NER
- Source credibility scoring

---

## Currently Implemented

### Sources

- **hn** - Hacker News (top, best, new)
- **reddit** - Reddit programming subreddits
- **reddit:subreddit** - Specific subreddit (e.g., reddit:dotnet)
- **so** - StackOverflow hot questions
- **so:tag** - StackOverflow by tag (e.g., so:csharp, so:python)
- **so:search:query** - StackOverflow search
- **bbc, guardian, ars, verge, wired, techcrunch** - News sources
- **bbc:query** - News filtered by topic (e.g., bbc:AI)
- **lobsters, devto, hackernoon, slashdot** - Tech blogs
- **search:query** - DuckDuckGo search
- **http://url** - Any RSS feed or website

### Features

- 4 vibes: doom, hopeful, snarky, neutral
- NER entity extraction (--entities)
- JSON output for LLM tools (--json)
- Raw content display (--raw)
- Inline image display (--images)
- Natural language prompt interpretation
- Parallel fetching from multiple sources
- Embedding-based deduplication
- SQLite trend tracking
- Self-setup with ONNX model download

### Commands

- `doomsummarizer setup [--ner] [--playwright]` - Initialize models
- `doomsummarizer scroll [prompt]` - Main scroll command
- `doomsummarizer trends` - View historical trends
- `doomsummarizer config --show` - View configuration

### Example Prompts

- `scroll "doom scroll hacker news"` - Pessimistic HN summary
- `scroll "see what bbc says about AI"` - BBC filtered by AI
- `scroll -s so:csharp -s reddit:dotnet` - C# from SO and Reddit
- `scroll --vibe snarky -s lobsters -s hn` - Snarky tech news
- `scroll -s bbc -s guardian --images` - News with inline images
