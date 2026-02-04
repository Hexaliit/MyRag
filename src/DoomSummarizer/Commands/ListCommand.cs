using System.ComponentModel;
using DoomSummarizer.Helpers;
using DoomSummarizer.Models;
using DoomSummarizer.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DoomSummarizer.Commands;

// ═══════════════════════════════════════════════════════════════════════
//  doomsummarizer list docs [query]
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
///     List stored documents. Optionally filter by semantic query, source, or entity.
///     doomsummarizer list docs                       → all documents grouped by collection
///     doomsummarizer list docs "transformer models"  → documents matching the query
///     doomsummarizer list docs --entity "OpenAI"     → documents mentioning an entity
///     doomsummarizer list docs --source crawl:docs   → documents from a specific source
/// </summary>
public sealed class ListDocsCommand : AsyncCommand<ListDocsCommand.Settings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        // ── Filesystem scan mode ──────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(settings.Path))
            return await ListFromFilesystemAsync(settings, ct);

        // ── Database mode (existing behavior) ─────────────────────────
        await using var boot = await CommandBootstrap.CreateAsync(ct: ct);

        // If entity filter requested, initialize entity store
        IEntityGraphStore? entityStore = null;
        if (!string.IsNullOrWhiteSpace(settings.Entity))
        {
            entityStore = await boot.TryInitializeEntityGraphStoreAsync();
            if (entityStore == null)
                AnsiConsole.MarkupLine("[yellow]Entity store not available. Ignoring --entity filter.[/]");
        }

        List<StoredItem> items;

        if (!string.IsNullOrWhiteSpace(settings.Query))
        {
            // Semantic search: embed query → find similar items
            var queryEmbedding = await boot.Embedding.EmbedAsync(settings.Query, ct);
            items = await boot.Storage.FindSimilarAsync(
                queryEmbedding, settings.Limit * 2, 0.20, settings.Source);

            // Also try FTS for keyword matches the embedding might miss
            var ftsIds = await boot.Storage.FtsPreFilterAsync(settings.Query, settings.Source, settings.Limit);
            if (ftsIds.Count > 0)
            {
                var existingIds = items.Select(i => i.Id).ToHashSet();
                var ftsItems = await boot.Storage.GetItemsByIdsAsync(
                    ftsIds.Where(id => !existingIds.Contains(id)).ToList());
                items.AddRange(ftsItems);
            }
        }
        else
        {
            // No query: list recent items
            items = await boot.Storage.GetRecentItemsAsync(settings.Days, settings.Source);
        }

        // Entity filter: narrow to documents mentioning the entity
        if (!string.IsNullOrWhiteSpace(settings.Entity) && entityStore != null)
        {
            var entityItems = await FilterByEntityNameAsync(entityStore, settings.Entity);
            if (entityItems != null)
            {
                var entityItemIds = entityItems.ToHashSet();
                items = items.Where(i => entityItemIds.Contains(i.Id)).ToList();
            }
        }

        if (items.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No documents found matching your criteria.[/]");
            return 0;
        }

        // Group by distinct URL/title (deduplicate segments from same document)
        var docs = items
            .GroupBy(i => i.Url ?? i.Title)
            .Select(g =>
            {
                var best = g.MaxBy(i => i.FetchedAt)!;
                return new
                {
                    best.Id,
                    best.Title,
                    best.Url,
                    best.Source,
                    best.FetchedAt,
                    best.DetectedTopic,
                    best.SentimentScore,
                    SegmentCount = g.Count(),
                    HasEmbedding = g.Any(i => i.Embedding != null)
                };
            })
            .OrderByDescending(d => d.FetchedAt)
            .Take(settings.Limit)
            .ToList();

        // Render table
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("#").RightAligned())
            .AddColumn("Title")
            .AddColumn("Source")
            .AddColumn(new TableColumn("Segs").RightAligned())
            .AddColumn("Topic")
            .AddColumn("Fetched");

        for (var i = 0; i < docs.Count; i++)
        {
            var d = docs[i];
            var title = d.Title.Length > 50 ? d.Title[..47] + "..." : d.Title;
            var topic = d.DetectedTopic ?? "";
            if (topic.Length > 20) topic = topic[..17] + "...";
            var age = FormattingHelpers.FormatAge(d.FetchedAt);

            table.AddRow(
                $"[grey]{i + 1}[/]",
                Markup.Escape(title),
                $"[cyan]{Markup.Escape(d.Source)}[/]",
                $"[grey]{d.SegmentCount}[/]",
                $"[grey]{Markup.Escape(topic)}[/]",
                $"[grey]{age}[/]");
        }

        AnsiConsole.Write(table);

        var totalSegs = docs.Sum(d => d.SegmentCount);
        AnsiConsole.MarkupLine(
            $"[grey]{docs.Count} documents ({totalSegs} segments) from {items.Select(i => i.Source).Distinct().Count()} sources[/]");

        return 0;
    }

    /// <summary>
    ///     Scan a local filesystem directory and display file metadata.
    /// </summary>
    private static async Task<int> ListFromFilesystemAsync(Settings settings, CancellationToken ct)
    {
        var path = settings.Path!;
        if (!Directory.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]Directory not found: {Markup.Escape(path)}[/]");
            return 1;
        }

        var files = Directory.EnumerateFiles(path, settings.Pattern, SearchOption.AllDirectories)
            .Take(settings.Limit)
            .ToList();

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]No files matching '{Markup.Escape(settings.Pattern)}' in {Markup.Escape(path)}[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"[grey]Scanning {files.Count} files in {Markup.Escape(path)}...[/]");

        var items = new List<ContentItem>();
        await AnsiConsole.Progress()
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("Reading files", maxValue: files.Count);
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var item = await RetrievalSubQueryExecutor
                            .BuildContentItemFromFileAsync(file, ct);
                        items.Add(item);
                    }
                    catch
                    {
                        // Skip unreadable files
                    }

                    task.Increment(1);
                }
            });

        if (items.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No readable files found.[/]");
            return 0;
        }

        // Render file metadata table
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("#").RightAligned())
            .AddColumn("Title")
            .AddColumn(new TableColumn("Size").RightAligned())
            .AddColumn(new TableColumn("Words").RightAligned())
            .AddColumn(new TableColumn("Lines").RightAligned())
            .AddColumn("Author")
            .AddColumn("Tags")
            .AddColumn("Modified");

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var meta = item.Metadata ?? new Dictionary<string, string>();

            var title = item.Title;
            if (title.Length > 45) title = title[..42] + "...";
            var size = meta.GetValueOrDefault("file_size_human", "?");
            var words = meta.GetValueOrDefault("word_count", "-");
            var lines = meta.GetValueOrDefault("line_count", "-");
            var author = item.Author ?? meta.GetValueOrDefault("fm_author", "");
            if (author.Length > 15) author = author[..12] + "...";
            var tags = item.Tags.Count > 0 ? string.Join(", ", item.Tags.Take(3)) : "";
            if (tags.Length > 20) tags = tags[..17] + "...";
            var modified = meta.TryGetValue("last_modified", out var modStr)
                           && DateTimeOffset.TryParse(modStr, out var modDate)
                ? FormattingHelpers.FormatAge(modDate)
                : "-";

            table.AddRow(
                $"[grey]{i + 1}[/]",
                Markup.Escape(title),
                $"[cyan]{size}[/]",
                $"[grey]{words}[/]",
                $"[grey]{lines}[/]",
                $"[grey]{Markup.Escape(author)}[/]",
                $"[dim]{Markup.Escape(tags)}[/]",
                $"[grey]{modified}[/]");
        }

        AnsiConsole.Write(table);

        // Summary statistics
        var totalSize = items.Sum(i =>
            long.TryParse(i.Metadata?.GetValueOrDefault("file_size"), out var s) ? s : 0);
        var totalWords = items.Sum(i =>
            int.TryParse(i.Metadata?.GetValueOrDefault("word_count"), out var w) ? w : 0);
        var extensions = items
            .Select(i => i.Metadata?.GetValueOrDefault("extension") ?? "?")
            .GroupBy(e => e)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}: {g.Count()}")
            .ToList();
        var withAuthor = items.Count(i => !string.IsNullOrEmpty(i.Author));
        var withTags = items.Count(i => i.Tags.Count > 0);

        // Structure stats for Markdown files
        var totalHeadings = items.Sum(i =>
            int.TryParse(i.Metadata?.GetValueOrDefault("headings"), out var h) ? h : 0);
        var totalCodeBlocks = items.Sum(i =>
            int.TryParse(i.Metadata?.GetValueOrDefault("code_blocks"), out var c) ? c : 0);
        var totalTables = items.Sum(i =>
            int.TryParse(i.Metadata?.GetValueOrDefault("tables"), out var t) ? t : 0);
        var totalLinks = items.Sum(i =>
            int.TryParse(i.Metadata?.GetValueOrDefault("links"), out var l) ? l : 0);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[bold]{items.Count} files[/] | " +
            $"[cyan]{FormatFileSize(totalSize)}[/] total | " +
            $"[grey]{totalWords:N0} words[/]");
        AnsiConsole.MarkupLine(
            $"[grey]Types: {string.Join(", ", extensions)}[/]");
        if (withAuthor > 0 || withTags > 0)
            AnsiConsole.MarkupLine(
                $"[grey]{withAuthor} with author | {withTags} with tags[/]");
        if (totalHeadings > 0)
            AnsiConsole.MarkupLine(
                $"[grey]Structure: {totalHeadings} headings, {totalCodeBlocks} code blocks, " +
                $"{totalTables} tables, {totalLinks} links[/]");

        return 0;
    }

    private static string FormatFileSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
        };
    }

    private static async Task<HashSet<string>?> FilterByEntityNameAsync(IEntityGraphStore entityStore,
        string entityName)
    {
        // Find the entity by name (case-insensitive search via top entities)
        var entities = await entityStore.GetTopEntitiesAsync(500);
        var match = entities.FirstOrDefault(e =>
            e.Name.Contains(entityName, StringComparison.OrdinalIgnoreCase));

        if (match == null)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Entity '{Markup.Escape(entityName)}' not found in the knowledge graph.[/]");
            return null;
        }

        AnsiConsole.MarkupLine(
            $"[grey]Filtering by entity: {Markup.Escape(match.Name)} ({match.Type}, {match.MentionCount} mentions)[/]");

        var articles = await entityStore.GetArticlesForEntityAsync(match.Id);
        return articles.Select(a => a.itemId).ToHashSet();
    }

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[query]")]
        [Description("Semantic query to filter documents (optional)")]
        public string? Query { get; init; }

        [CommandOption("-s|--source")]
        [Description("Filter to a source (e.g., crawl:docs, hn, reddit)")]
        public string? Source { get; init; }

        [CommandOption("-e|--entity")]
        [Description("Filter to documents mentioning a specific entity name")]
        public string? Entity { get; init; }

        [CommandOption("-p|--path")]
        [Description("Scan a local directory instead of the database (e.g., C:\\Blog\\Markdown)")]
        public string? Path { get; init; }

        [CommandOption("-g|--pattern")]
        [Description("File glob pattern when using --path (default: *.md)")]
        [DefaultValue("*.md")]
        public string Pattern { get; init; } = "*.md";

        [CommandOption("-l|--limit")]
        [Description("Maximum documents to show (default: 50)")]
        [DefaultValue(50)]
        public int Limit { get; init; } = 50;

        [CommandOption("-d|--days")]
        [Description("How far back to search (default: 365)")]
        [DefaultValue(365)]
        public int Days { get; init; } = 365;
    }
}

// ═══════════════════════════════════════════════════════════════════════
//  doomsummarizer list segments [query]
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
///     List stored content segments. Optionally filter by semantic query.
///     doomsummarizer list segments                        → recent segments
///     doomsummarizer list segments "attention mechanisms"  → segments matching query
///     doomsummarizer list segments --source crawl:docs     → segments from a source
///     doomsummarizer list segments --topic "AI Safety"     → segments by detected topic
/// </summary>
public sealed class ListSegmentsCommand : AsyncCommand<ListSegmentsCommand.Settings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        await using var boot = await CommandBootstrap.CreateAsync(ct: ct);

        List<StoredItem> items;

        if (!string.IsNullOrWhiteSpace(settings.Query))
        {
            // Hybrid search: embedding similarity + FTS
            var queryEmbedding = await boot.Embedding.EmbedAsync(settings.Query, ct);
            items = await boot.Storage.FindSimilarAsync(
                queryEmbedding, settings.Limit, 0.20, settings.Source);

            // Supplement with FTS results
            var ftsIds = await boot.Storage.FtsPreFilterAsync(settings.Query, settings.Source, settings.Limit);
            if (ftsIds.Count > 0)
            {
                var existingIds = items.Select(i => i.Id).ToHashSet();
                var ftsItems = await boot.Storage.GetItemsByIdsAsync(
                    ftsIds.Where(id => !existingIds.Contains(id)).Take(settings.Limit / 2).ToList());
                items.AddRange(ftsItems);
            }
        }
        else
        {
            items = await boot.Storage.GetRecentItemsAsync(settings.Days, settings.Source);
        }

        // Topic filter
        if (!string.IsNullOrWhiteSpace(settings.Topic))
            items = items
                .Where(i => i.DetectedTopic != null &&
                            i.DetectedTopic.Contains(settings.Topic, StringComparison.OrdinalIgnoreCase))
                .ToList();

        items = items.Take(settings.Limit).ToList();

        if (items.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No segments found matching your criteria.[/]");
            return 0;
        }

        // Render table
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("#").RightAligned())
            .AddColumn("Title")
            .AddColumn("Source")
            .AddColumn("Topic")
            .AddColumn(new TableColumn("Sent").RightAligned())
            .AddColumn(new TableColumn("Emb").Centered())
            .AddColumn("Fetched");

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var title = item.Title.Length > 40 ? item.Title[..37] + "..." : item.Title;
            var topic = item.DetectedTopic ?? "";
            if (topic.Length > 15) topic = topic[..12] + "...";
            var sentColor = item.SentimentScore >= 0 ? "green" : "red";
            var hasEmb = item.Embedding != null ? "[green]Y[/]" : "[grey]N[/]";
            var age = FormattingHelpers.FormatAge(item.FetchedAt);

            table.AddRow(
                $"[grey]{i + 1}[/]",
                Markup.Escape(title),
                $"[cyan]{Markup.Escape(item.Source)}[/]",
                $"[grey]{Markup.Escape(topic)}[/]",
                $"[{sentColor}]{item.SentimentScore:+0.00;-0.00}[/]",
                hasEmb,
                $"[grey]{age}[/]");

            if (settings.Full)
            {
                var preview =
                    (item.Summary ?? item.Content ?? "")[..Math.Min(200, (item.Summary ?? item.Content ?? "").Length)];
                if (preview.Length > 0)
                    table.AddRow("", $"[dim]{Markup.Escape(preview)}[/]", "", "", "", "", "");
            }
        }

        AnsiConsole.Write(table);

        var withEmb = items.Count(i => i.Embedding != null);
        AnsiConsole.MarkupLine($"[grey]{items.Count} segments shown ({withEmb} with embeddings)[/]");

        return 0;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[query]")]
        [Description("Semantic query to filter segments (optional)")]
        public string? Query { get; init; }

        [CommandOption("-s|--source")]
        [Description("Filter to a source (e.g., crawl:docs, hn, reddit)")]
        public string? Source { get; init; }

        [CommandOption("-t|--topic")]
        [Description("Filter by detected topic")]
        public string? Topic { get; init; }

        [CommandOption("-l|--limit")]
        [Description("Maximum segments to show (default: 30)")]
        [DefaultValue(30)]
        public int Limit { get; init; } = 30;

        [CommandOption("-d|--days")]
        [Description("How far back to search (default: 30)")]
        [DefaultValue(30)]
        public int Days { get; init; } = 30;

        [CommandOption("--full")]
        [Description("Show content preview for each segment")]
        public bool Full { get; init; }
    }
}

// ═══════════════════════════════════════════════════════════════════════
//  doomsummarizer list entities [query]
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
///     List extracted entities from the knowledge graph.
///     doomsummarizer list entities                    → top entities by mention count
///     doomsummarizer list entities "OpenAI"           → search entities by name/query
///     doomsummarizer list entities --type ORG          → filter by entity type
///     doomsummarizer list entities --type PER --days 7 → recent people
/// </summary>
public sealed class ListEntitiesCommand : AsyncCommand<ListEntitiesCommand.Settings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        await using var boot = await CommandBootstrap.CreateAsync(ct: ct);

        IEntityGraphStore entityStore;
        try
        {
            entityStore = await boot.InitializeEntityGraphStoreAsync();
        }
        catch
        {
            AnsiConsole.MarkupLine(
                "[red]Entity store not available. Run 'doomsummarizer crawl' with --entities first.[/]");
            return 1;
        }

        // Get graph stats
        var stats = await entityStore.GetStatsAsync();
        if (stats.entities == 0)
        {
            AnsiConsole.MarkupLine(
                "[yellow]No entities in the knowledge graph. Use 'crawl --entities' to extract them.[/]");
            return 0;
        }

        List<GraphEntity> entities;

        if (!string.IsNullOrWhiteSpace(settings.Query))
        {
            // Two-pass: name match first, then semantic search
            var allEntities = await entityStore.GetTopEntitiesAsync(1000, settings.Type);

            // Name matching (case-insensitive contains)
            entities = allEntities
                .Where(e => e.Name.Contains(settings.Query, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // If no name matches, try semantic search
            if (entities.Count == 0)
            {
                var queryEmbedding = await boot.Embedding.EmbedAsync(settings.Query, ct);
                var semanticResults = await entityStore.FindSimilarEntitiesAsync(queryEmbedding, settings.Limit);
                entities = semanticResults
                    .Where(r => settings.Type == null ||
                                r.entity.Type.Equals(settings.Type, StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.entity)
                    .ToList();

                if (entities.Count > 0)
                    AnsiConsole.MarkupLine(
                        $"[grey]No exact name match — showing {entities.Count} semantically similar entities[/]");
            }
        }
        else
        {
            entities = await entityStore.GetTopEntitiesAsync(
                settings.Limit,
                settings.Type,
                settings.Days > 0 ? settings.Days : null);
        }

        entities = entities.Take(settings.Limit).ToList();

        if (entities.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No entities found matching your criteria.[/]");
            return 0;
        }

        // Render table
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("#").RightAligned())
            .AddColumn("Entity")
            .AddColumn("Type")
            .AddColumn(new TableColumn("Mentions").RightAligned())
            .AddColumn(new TableColumn("Articles").RightAligned())
            .AddColumn("First Seen")
            .AddColumn("Last Seen");

        for (var i = 0; i < entities.Count; i++)
        {
            var e = entities[i];
            var typeColor = e.Type switch
            {
                "PER" => "blue",
                "ORG" => "green",
                "LOC" => "yellow",
                _ => "grey"
            };

            table.AddRow(
                $"[grey]{i + 1}[/]",
                Markup.Escape(e.Name),
                $"[{typeColor}]{e.Type}[/]",
                $"[cyan]{e.MentionCount}[/]",
                $"[grey]{e.ArticleCount}[/]",
                $"[grey]{FormattingHelpers.FormatAge(e.FirstSeen)}[/]",
                $"[grey]{FormattingHelpers.FormatAge(e.LastSeen)}[/]");

            // Show articles if requested
            if (settings.ShowArticles)
            {
                var articles = await entityStore.GetArticlesForEntityAsync(e.Id);
                foreach (var (itemId, title, url, confidence) in articles.Take(5))
                {
                    var artTitle = title.Length > 60 ? title[..57] + "..." : title;
                    table.AddRow("", $"  [dim]→ {Markup.Escape(artTitle)}[/]",
                        "", $"[dim]{confidence:F2}[/]", "", "", "");
                }
            }
        }

        AnsiConsole.Write(table);

        // Summary
        var typeBreakdown = entities
            .GroupBy(e => e.Type)
            .Select(g => $"{g.Key}: {g.Count()}")
            .ToList();

        AnsiConsole.MarkupLine(
            $"[grey]{entities.Count} entities shown (graph total: {stats.entities} entities, " +
            $"{stats.relationships} relationships, {stats.mentions} mentions)[/]");
        AnsiConsole.MarkupLine($"[grey]Types: {string.Join(", ", typeBreakdown)}[/]");

        return 0;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[query]")]
        [Description("Search entities by name or semantic query (optional)")]
        public string? Query { get; init; }

        [CommandOption("-t|--type")]
        [Description("Filter by entity type: PER (person), ORG (organization), LOC (location), MISC")]
        public string? Type { get; init; }

        [CommandOption("-l|--limit")]
        [Description("Maximum entities to show (default: 50)")]
        [DefaultValue(50)]
        public int Limit { get; init; } = 50;

        [CommandOption("-d|--days")]
        [Description("Only entities seen in the last N days")]
        [DefaultValue(0)]
        public int Days { get; init; }

        [CommandOption("--articles")]
        [Description("Show articles/documents for each entity")]
        public bool ShowArticles { get; init; }
    }
}