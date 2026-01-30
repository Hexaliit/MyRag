using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DoomSummarizer.Models;
using LucidRAG.Decomposer.Models;
using LucidRAG.Decomposer.Orchestration;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Content;
using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Services;

/// <summary>
/// Bridges the decomposer's ISubQueryExecutor to the existing DoomSummarizer
/// retrieval pipeline. Routes sub-queries through RetrievalPipeline.SearchAsync
/// and tool actions through the appropriate service.
/// </summary>
public sealed class RetrievalSubQueryExecutor : ISubQueryExecutor
{
    private readonly RetrievalPipeline _retrieval;
    private readonly IEmbeddingService _embedding;
    private readonly StorageService _storage;
    private readonly HttpClient _httpClient;
    private readonly ILogger<RetrievalSubQueryExecutor>? _logger;

    /// <summary>Default retrieval options applied to each sub-query.</summary>
    private readonly RetrievalOptions _baseOptions;

    public RetrievalSubQueryExecutor(
        RetrievalPipeline retrieval,
        IEmbeddingService embedding,
        StorageService storage,
        HttpClient? httpClient = null,
        RetrievalOptions? baseOptions = null,
        ILogger<RetrievalSubQueryExecutor>? logger = null)
    {
        _retrieval = retrieval;
        _embedding = embedding;
        _storage = storage;
        _httpClient = httpClient ?? new HttpClient();
        _baseOptions = baseOptions ?? new RetrievalOptions();
        _logger = logger;
    }

    /// <summary>
    /// Execute a sub-query through the retrieval pipeline (Lucene FTS + embedding HNSW + RRF).
    /// </summary>
    public async Task<SubQueryResult> ExecuteAsync(QueryNode node, CancellationToken ct = default)
    {
        try
        {
            _logger?.LogDebug("Executing sub-query [{NodeId}]: {Query}", node.Id, node.Query);

            var options = _baseOptions with
            {
                TopK = _baseOptions.TopK > 0 ? _baseOptions.TopK : 10,
                UseEmbeddingDedup = true
            };

            var result = await _retrieval.SearchAsync(node.Query, options, ct);

            _logger?.LogDebug("Sub-query [{NodeId}] returned {Count} items", node.Id, result.Items.Count);

            return new SubQueryResult
            {
                NodeId = node.Id,
                Success = true,
                Items = result.Items,
                ItemCount = result.Items.Count
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Sub-query [{NodeId}] failed: {Query}", node.Id, node.Query);
            return new SubQueryResult
            {
                NodeId = node.Id,
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Fetch content from a direct reference (URL, file path, DOI).
    /// URLs are fetched via ContentExtractor; file paths are read directly.
    /// </summary>
    public async Task<SubQueryResult> FetchReferenceAsync(ContentReference reference, CancellationToken ct = default)
    {
        try
        {
            _logger?.LogDebug("Fetching reference: {Kind} {Uri}", reference.Kind, reference.Uri);

            switch (reference.Kind)
            {
                case ContentReferenceKind.Url:
                case ContentReferenceKind.Doi:
                case ContentReferenceKind.GitHubReference:
                    // Use ContentExtractor (SmartReader) to fetch and parse the URL
                    var extractor = new ContentExtractor(_httpClient);
                    var extracted = await extractor.ExtractAsync(reference.Uri, ct);
                    if (extracted == null || string.IsNullOrWhiteSpace(extracted.Content))
                    {
                        return new SubQueryResult
                        {
                            NodeId = reference.Uri,
                            Success = false,
                            Error = $"No content extracted from {reference.Uri}"
                        };
                    }

                    var item = new ContentItem
                    {
                        Id = $"ref:{reference.Uri.GetHashCode():X8}",
                        Source = $"reference:{reference.Kind.ToString().ToLowerInvariant()}",
                        Title = extracted.Title ?? reference.Uri,
                        Url = reference.Uri,
                        Content = extracted.Content,
                        FetchedAt = DateTimeOffset.UtcNow
                    };

                    return new SubQueryResult
                    {
                        NodeId = reference.Uri,
                        Success = true,
                        Items = new List<ContentItem> { item },
                        ItemCount = 1
                    };

                case ContentReferenceKind.FilePath:
                    if (!File.Exists(reference.Uri))
                    {
                        return new SubQueryResult
                        {
                            NodeId = reference.Uri,
                            Success = false,
                            Error = $"File not found: {reference.Uri}"
                        };
                    }

                    var fileContent = await File.ReadAllTextAsync(reference.Uri, ct);
                    var fileName = Path.GetFileName(reference.Uri);

                    var fileItem = new ContentItem
                    {
                        Id = $"file:{reference.Uri.GetHashCode():X8}",
                        Source = "reference:filepath",
                        Title = fileName,
                        Url = reference.Uri,
                        Content = fileContent,
                        FetchedAt = DateTimeOffset.UtcNow
                    };

                    return new SubQueryResult
                    {
                        NodeId = reference.Uri,
                        Success = true,
                        Items = new List<ContentItem> { fileItem },
                        ItemCount = 1
                    };

                default:
                    return new SubQueryResult
                    {
                        NodeId = reference.Uri,
                        Success = false,
                        Error = $"Unsupported reference kind: {reference.Kind}"
                    };
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch reference: {Uri}", reference.Uri);
            return new SubQueryResult
            {
                NodeId = reference.Uri,
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Execute a tool action. Routes to the appropriate handler by ToolKind.
    /// </summary>
    public async Task<SubQueryResult> ExecuteToolAsync(QueryNode node, ToolAction action, CancellationToken ct = default)
    {
        try
        {
            _logger?.LogDebug("Executing tool [{Tool}] for [{NodeId}]: {Intent}",
                action.Tool, node.Id, action.Intent);

            return action.Tool switch
            {
                ToolKind.Search => await ExecuteSearchToolAsync(node, action, ct),
                ToolKind.KbQuery => await ExecuteKbQueryToolAsync(node, action, ct),
                ToolKind.Fetch => await ExecuteFetchToolAsync(node, action, ct),
                ToolKind.FileSystem => await ExecuteFileSystemToolAsync(node, action, ct),
                ToolKind.Index => await ExecuteIndexToolAsync(node, action, ct),
                ToolKind.Analyze => await ExecuteAnalyzeToolAsync(node, action, ct),
                _ => new SubQueryResult
                {
                    NodeId = node.Id,
                    Success = false,
                    Error = $"Tool '{action.Tool}' is not yet implemented. " +
                            $"Intent: {action.Intent}. " +
                            $"Parameters: {string.Join(", ", action.Parameters.Select(p => $"{p.Key}={p.Value}"))}"
                }
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Tool [{Tool}] failed for [{NodeId}]", action.Tool, node.Id);
            return new SubQueryResult
            {
                NodeId = node.Id,
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <inheritdoc />
    public Task<bool> SupportsToolAsync(ToolKind tool, CancellationToken ct = default)
    {
        var supported = tool is ToolKind.Search or ToolKind.KbQuery or ToolKind.Fetch
            or ToolKind.FileSystem or ToolKind.Index or ToolKind.Analyze;
        return Task.FromResult(supported);
    }

    private async Task<SubQueryResult> ExecuteSearchToolAsync(QueryNode node, ToolAction action, CancellationToken ct)
    {
        var searchQuery = action.Parameters.GetValueOrDefault("query", node.Query);
        var options = _baseOptions with
        {
            TopK = int.TryParse(action.Parameters.GetValueOrDefault("max_items"), out var maxItems)
                ? maxItems
                : _baseOptions.TopK
        };

        var result = await _retrieval.SearchAsync(searchQuery, options, ct);
        return new SubQueryResult
        {
            NodeId = node.Id,
            Success = true,
            Items = result.Items,
            ItemCount = result.Items.Count
        };
    }

    private async Task<SubQueryResult> ExecuteKbQueryToolAsync(QueryNode node, ToolAction action, CancellationToken ct)
    {
        var collection = action.Parameters.GetValueOrDefault("collection", "default");
        var topK = int.TryParse(action.Parameters.GetValueOrDefault("top_k"), out var k) ? k : 10;

        var options = _baseOptions with
        {
            CollectionName = collection,
            TopK = topK,
            IsKnowledgeBase = true
        };

        var result = await _retrieval.SearchAsync(node.Query, options, ct);
        return new SubQueryResult
        {
            NodeId = node.Id,
            Success = true,
            Items = result.Items,
            ItemCount = result.Items.Count
        };
    }

    private async Task<SubQueryResult> ExecuteFetchToolAsync(QueryNode node, ToolAction action, CancellationToken ct)
    {
        var url = action.Parameters.GetValueOrDefault("url", node.Query);
        var reference = new ContentReference
        {
            Uri = url,
            Kind = ContentReferenceKind.Url,
            OriginalText = url
        };
        return await FetchReferenceAsync(reference, ct);
    }

    /// <summary>
    /// FileSystem tool: find and read files matching a glob pattern.
    /// Extracts full metadata (size, type, dates, author, line count, structure).
    /// </summary>
    private async Task<SubQueryResult> ExecuteFileSystemToolAsync(QueryNode node, ToolAction action, CancellationToken ct)
    {
        var path = action.Parameters.GetValueOrDefault("path", ".");
        var pattern = action.Parameters.GetValueOrDefault("pattern", "*.*");
        var recursive = action.Parameters.GetValueOrDefault("recursive", "true")
            .Equals("true", StringComparison.OrdinalIgnoreCase);

        if (!Directory.Exists(path))
        {
            // Maybe it's a single file path
            if (File.Exists(path))
                return await ReadSingleFileAsync(node.Id, path, ct);

            return new SubQueryResult
            {
                NodeId = node.Id,
                Success = false,
                Error = $"Path not found: {path}"
            };
        }

        _logger?.LogDebug("FileSystem tool: scanning {Path} with pattern {Pattern} (recursive: {Recursive})",
            path, pattern, recursive);

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(path, pattern, searchOption)
            .Take(500) // Safety limit
            .ToList();

        if (files.Count == 0)
        {
            return new SubQueryResult
            {
                NodeId = node.Id,
                Success = true,
                Items = new List<ContentItem>(),
                ItemCount = 0
            };
        }

        _logger?.LogDebug("FileSystem tool: found {Count} files", files.Count);

        // Read files in parallel with bounded concurrency to avoid I/O contention
        var semaphore = new SemaphoreSlim(8);
        var tasks = files.Select(async filePath =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                return await BuildContentItemFromFileAsync(filePath, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug("Skipping file {File}: {Error}", filePath, ex.Message);
                return null;
            }
            finally
            {
                semaphore.Release();
            }
        });
        var results = await Task.WhenAll(tasks);
        var items = results.Where(item => item != null).ToList()!;

        return new SubQueryResult
        {
            NodeId = node.Id,
            Success = true,
            Items = items,
            ItemCount = items.Count
        };
    }

    /// <summary>
    /// Index tool: embed and store content items into a named KB collection.
    /// </summary>
    private async Task<SubQueryResult> ExecuteIndexToolAsync(QueryNode node, ToolAction action, CancellationToken ct)
    {
        var collection = action.Parameters.GetValueOrDefault("collection", "default");

        // Items to index come from the node's query against existing results,
        // or from a prior FileSystem tool in the same chain.
        // First, try to get items from the retrieval pipeline using the node query.
        var searchQuery = action.Parameters.GetValueOrDefault("query", node.Query);
        var topK = int.TryParse(action.Parameters.GetValueOrDefault("max_items"), out var mi) ? mi : 50;

        var result = await _retrieval.SearchAsync(searchQuery, _baseOptions with
        {
            CollectionName = collection,
            TopK = topK,
            IsKnowledgeBase = true
        }, ct);

        if (result.Items.Count == 0)
        {
            return new SubQueryResult
            {
                NodeId = node.Id,
                Success = true,
                Items = new List<ContentItem>(),
                ItemCount = 0
            };
        }

        _logger?.LogDebug("Index tool: indexing {Count} items into collection '{Collection}'",
            result.Items.Count, collection);

        // Batch-embed items that need embeddings (instead of one-at-a-time)
        var itemsNeedingEmbedding = result.Items
            .Where(i => i.Embedding == null || i.Embedding.Length == 0)
            .ToList();

        if (itemsNeedingEmbedding.Count > 0)
        {
            var texts = itemsNeedingEmbedding
                .Select(i => i.Content ?? i.Summary ?? i.Title)
                .ToList();
            var embeddings = await _embedding.EmbedBatchAsync(texts, ct);
            for (var i = 0; i < itemsNeedingEmbedding.Count; i++)
                itemsNeedingEmbedding[i].Embedding = embeddings[i];
        }

        var indexed = 0;
        foreach (var item in result.Items)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Store the item
                await _storage.SaveItemAsync(item);

                // Build a keyword profile and index in FTS
                var contentPreview = (item.Content ?? item.Summary ?? "");
                if (contentPreview.Length > 2000) contentPreview = contentPreview[..2000];
                await _storage.IndexDocumentFtsAsync(item.Id, item.Title, item.Keywords ?? "", contentPreview);

                indexed++;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug("Failed to index item {Id}: {Error}", item.Id, ex.Message);
            }
        }

        return new SubQueryResult
        {
            NodeId = node.Id,
            Success = true,
            Items = result.Items,
            ItemCount = indexed
        };
    }

    /// <summary>
    /// Analyze tool: compute metrics (length, count, statistics) on retrieved content.
    /// </summary>
    private async Task<SubQueryResult> ExecuteAnalyzeToolAsync(QueryNode node, ToolAction action, CancellationToken ct)
    {
        var metric = action.Parameters.GetValueOrDefault("metric", "summary");
        var searchQuery = action.Parameters.GetValueOrDefault("query", node.Query);

        // Get items to analyze from the retrieval pipeline
        var result = await _retrieval.SearchAsync(searchQuery, _baseOptions with
        {
            TopK = 50,
            UseEmbeddingDedup = true
        }, ct);

        if (result.Items.Count == 0)
        {
            return new SubQueryResult
            {
                NodeId = node.Id,
                Success = true,
                Items = new List<ContentItem>
                {
                    new()
                    {
                        Id = $"analysis:{node.Id}",
                        Source = "tool:analyze",
                        Title = $"Analysis: {metric}",
                        Content = "No items found to analyze.",
                        FetchedAt = DateTimeOffset.UtcNow
                    }
                },
                ItemCount = 1
            };
        }

        var sb = new StringBuilder();
        sb.AppendLine($"## Analysis: {metric}");
        sb.AppendLine($"**Items analyzed**: {result.Items.Count}");
        sb.AppendLine();

        switch (metric.ToLowerInvariant())
        {
            case "length" or "size":
                var lengths = result.Items
                    .Select(i => (i.Title, Length: (i.Content ?? "").Length))
                    .OrderByDescending(x => x.Length)
                    .ToList();
                var totalChars = lengths.Sum(l => l.Length);
                var avgChars = totalChars / Math.Max(1, lengths.Count);

                sb.AppendLine($"**Total characters**: {totalChars:N0}");
                sb.AppendLine($"**Average length**: {avgChars:N0} chars");
                sb.AppendLine($"**Shortest**: {lengths.Last().Title} ({lengths.Last().Length:N0} chars)");
                sb.AppendLine($"**Longest**: {lengths.First().Title} ({lengths.First().Length:N0} chars)");
                sb.AppendLine();
                sb.AppendLine("| # | Title | Length |");
                sb.AppendLine("|---|-------|--------|");
                foreach (var (title, length) in lengths.Take(20))
                {
                    var t = title.Length > 50 ? title[..47] + "..." : title;
                    sb.AppendLine($"| - | {t} | {length:N0} |");
                }
                break;

            case "count":
                var bySrc = result.Items
                    .GroupBy(i => i.Source)
                    .Select(g => (Source: g.Key, Count: g.Count()))
                    .OrderByDescending(x => x.Count)
                    .ToList();

                sb.AppendLine("| Source | Count |");
                sb.AppendLine("|--------|-------|");
                foreach (var (source, count) in bySrc)
                    sb.AppendLine($"| {source} | {count} |");
                break;

            case "freshness" or "age":
                var now = DateTimeOffset.UtcNow;
                var byAge = result.Items
                    .Select(i => (i.Title, Age: now - i.FetchedAt))
                    .OrderBy(x => x.Age)
                    .ToList();

                var newest = byAge.First();
                var oldest = byAge.Last();
                sb.AppendLine($"**Newest**: {newest.Title} ({FormatAge(newest.Age)} ago)");
                sb.AppendLine($"**Oldest**: {oldest.Title} ({FormatAge(oldest.Age)} ago)");
                sb.AppendLine($"**Median age**: {FormatAge(byAge[byAge.Count / 2].Age)}");
                break;

            case "topics":
                var topics = result.Items
                    .Where(i => !string.IsNullOrEmpty(i.DetectedTopic))
                    .GroupBy(i => i.DetectedTopic!)
                    .Select(g => (Topic: g.Key, Count: g.Count()))
                    .OrderByDescending(x => x.Count)
                    .ToList();

                sb.AppendLine("| Topic | Count |");
                sb.AppendLine("|-------|-------|");
                foreach (var (topic, count) in topics.Take(20))
                    sb.AppendLine($"| {topic} | {count} |");
                break;

            default: // "summary" or unknown metric
                var total = result.Items.Count;
                var withContent = result.Items.Count(i => !string.IsNullOrEmpty(i.Content));
                var sources = result.Items.Select(i => i.Source).Distinct().Count();
                var totalLen = result.Items.Sum(i => (i.Content ?? "").Length);

                sb.AppendLine($"**Total items**: {total}");
                sb.AppendLine($"**With content**: {withContent}");
                sb.AppendLine($"**Distinct sources**: {sources}");
                sb.AppendLine($"**Total content length**: {totalLen:N0} chars");
                sb.AppendLine($"**Average relevance**: {result.Items.Average(i => i.RelevanceScore):F3}");

                if (result.Items.Any(i => i.FetchedAt != default))
                {
                    var newestDate = result.Items.Max(i => i.FetchedAt);
                    var oldestDate = result.Items.Where(i => i.FetchedAt != default).Min(i => i.FetchedAt);
                    sb.AppendLine($"**Date range**: {oldestDate:yyyy-MM-dd} to {newestDate:yyyy-MM-dd}");
                }
                break;
        }

        var analysisItem = new ContentItem
        {
            Id = $"analysis:{node.Id}",
            Source = "tool:analyze",
            Title = $"Analysis: {metric} ({result.Items.Count} items)",
            Content = sb.ToString(),
            FetchedAt = DateTimeOffset.UtcNow
        };

        return new SubQueryResult
        {
            NodeId = node.Id,
            Success = true,
            Items = new List<ContentItem> { analysisItem },
            ItemCount = 1
        };
    }

    // ── File helpers ──────────────────────────────────────────────────────────

    private async Task<SubQueryResult> ReadSingleFileAsync(string nodeId, string filePath, CancellationToken ct)
    {
        var item = await BuildContentItemFromFileAsync(filePath, ct);
        return new SubQueryResult
        {
            NodeId = nodeId,
            Success = true,
            Items = new List<ContentItem> { item },
            ItemCount = 1
        };
    }

    /// <summary>Maximum text file size to read fully (10 MB). Larger files are truncated.</summary>
    private const long MaxTextFileBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Build a fully-populated ContentItem from a file path with all available metadata.
    /// </summary>
    internal static async Task<ContentItem> BuildContentItemFromFileAsync(string filePath, CancellationToken ct)
    {
        var fi = new FileInfo(filePath);
        var ext = fi.Extension.ToLowerInvariant();

        // Read content for text-readable formats
        string? content = null;
        ContentStructure? structure = null;
        var frontMatter = new Dictionary<string, string>();

        if (IsTextFile(ext))
        {
            if (fi.Length <= MaxTextFileBytes)
            {
                content = await File.ReadAllTextAsync(filePath, ct);
            }
            else
            {
                // Cap content to avoid OOM on very large files
                var buffer = new char[MaxTextFileBytes / 2]; // chars are ~2 bytes in UTF-8 avg
                using var reader = new StreamReader(filePath);
                var charsRead = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
                content = new string(buffer, 0, charsRead);
            }

            // Extract YAML frontmatter from Markdown files
            if (ext is ".md" or ".markdown" or ".mdx")
            {
                frontMatter = ExtractYamlFrontMatter(content);

                // Analyze Markdown structure
                structure = MarkdownContentAnalyzer.Analyze(content);
            }
        }

        // Build metadata dictionary
        var metadata = new Dictionary<string, string>
        {
            ["file_size"] = fi.Length.ToString(CultureInfo.InvariantCulture),
            ["file_size_human"] = FormatFileSize(fi.Length),
            ["extension"] = ext,
            ["file_name"] = fi.Name,
            ["directory"] = fi.DirectoryName ?? "",
            ["full_path"] = fi.FullName,
            ["last_modified"] = fi.LastWriteTimeUtc.ToString("O"),
            ["created"] = fi.CreationTimeUtc.ToString("O"),
            ["last_accessed"] = fi.LastAccessTimeUtc.ToString("O"),
            ["is_readonly"] = fi.IsReadOnly.ToString()
        };

        // Content-derived metadata
        if (content != null)
        {
            var lineCount = content.Count(c => c == '\n') + 1;
            var wordCount = CountWords(content.AsSpan());
            metadata["line_count"] = lineCount.ToString(CultureInfo.InvariantCulture);
            metadata["word_count"] = wordCount.ToString(CultureInfo.InvariantCulture);
            metadata["char_count"] = content.Length.ToString(CultureInfo.InvariantCulture);
        }

        // Structure-derived metadata
        if (structure != null)
        {
            metadata["headings"] = structure.Headings.ToString(CultureInfo.InvariantCulture);
            metadata["paragraphs"] = structure.Paragraphs.ToString(CultureInfo.InvariantCulture);
            metadata["code_blocks"] = structure.CodeBlocks.ToString(CultureInfo.InvariantCulture);
            metadata["tables"] = structure.Tables.ToString(CultureInfo.InvariantCulture);
            metadata["links"] = structure.Links.ToString(CultureInfo.InvariantCulture);
            metadata["images"] = structure.Images.ToString(CultureInfo.InvariantCulture);
            metadata["list_items"] = structure.ListItems.ToString(CultureInfo.InvariantCulture);
        }

        // Frontmatter metadata
        foreach (var (key, value) in frontMatter)
            metadata[$"fm_{key}"] = value;

        // Derive title: frontmatter title > first H1 > filename
        var title = frontMatter.GetValueOrDefault("title")
                    ?? structure?.HeadingTexts.FirstOrDefault()
                    ?? Path.GetFileNameWithoutExtension(filePath);

        var author = frontMatter.GetValueOrDefault("author");
        var dateStr = frontMatter.GetValueOrDefault("date")
                      ?? frontMatter.GetValueOrDefault("publisheddate");
        var createdAt = DateTimeOffset.TryParse(dateStr, out var parsed)
            ? parsed
            : new DateTimeOffset(fi.CreationTimeUtc, TimeSpan.Zero);

        // Tags from frontmatter
        var tags = new List<string>();
        if (frontMatter.TryGetValue("tags", out var tagsStr))
        {
            tags = tagsStr
                .Trim('[', ']')
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim().Trim('"', '\''))
                .Where(t => !string.IsNullOrEmpty(t))
                .ToList();
        }
        if (frontMatter.TryGetValue("categories", out var catsStr))
        {
            var cats = catsStr
                .Trim('[', ']')
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim().Trim('"', '\''))
                .Where(t => !string.IsNullOrEmpty(t));
            tags.AddRange(cats);
        }

        return new ContentItem
        {
            Id = $"file:{filePath.GetHashCode():X8}",
            Source = $"filesystem:{ext.TrimStart('.')}",
            Title = title,
            Url = filePath,
            Content = content,
            Author = author,
            CreatedAt = createdAt,
            FetchedAt = DateTimeOffset.UtcNow,
            Tags = tags,
            ContentStructure = structure,
            Metadata = metadata
        };
    }

    /// <summary>
    /// Extract key-value pairs from YAML frontmatter (between --- delimiters).
    /// </summary>
    internal static Dictionary<string, string> ExtractYamlFrontMatter(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!content.StartsWith("---"))
            return result;

        var endIdx = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endIdx < 0)
            return result;

        var yamlBlock = content[3..endIdx];
        var lines = yamlBlock.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var colonIdx = line.IndexOf(':');
            if (colonIdx <= 0) continue;

            var key = line[..colonIdx].Trim().ToLowerInvariant();
            var value = line[(colonIdx + 1)..].Trim();

            // Strip surrounding quotes
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                result[key] = value;
        }

        return result;
    }

    /// <summary>
    /// Zero-allocation word count using ReadOnlySpan.
    /// Walks chars tracking in-word state instead of allocating a Split() array.
    /// </summary>
    internal static int CountWords(ReadOnlySpan<char> text)
    {
        var count = 0;
        var inWord = false;

        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                count++;
            }
        }

        return count;
    }

    private static bool IsTextFile(string extension) =>
        extension is ".md" or ".markdown" or ".mdx"
            or ".txt" or ".text"
            or ".html" or ".htm"
            or ".xml" or ".json" or ".yaml" or ".yml" or ".toml"
            or ".csv" or ".tsv"
            or ".cs" or ".py" or ".js" or ".ts" or ".jsx" or ".tsx"
            or ".java" or ".go" or ".rs" or ".rb" or ".php"
            or ".sh" or ".bash" or ".ps1" or ".bat" or ".cmd"
            or ".sql" or ".css" or ".scss" or ".less"
            or ".r" or ".R" or ".ipynb"
            or ".cfg" or ".ini" or ".conf" or ".env"
            or ".log" or ".diff" or ".patch";

    private static string FormatFileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
    };

    private static string FormatAge(TimeSpan age) => age.TotalDays switch
    {
        > 365 => $"{age.TotalDays / 365:F0}y",
        > 30 => $"{age.TotalDays / 30:F0}mo",
        > 1 => $"{age.TotalDays:F0}d",
        _ when age.TotalHours > 1 => $"{age.TotalHours:F0}h",
        _ => $"{age.TotalMinutes:F0}m"
    };
}
