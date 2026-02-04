using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
using DoomSummarizer.Models;
using DoomSummarizer.Services;
using Mostlylucid.DocSummarizer.Services;

namespace DoomWriter.Services;

/// <summary>
///     Manages a searchable knowledge base from markdown directories.
///     Ingests, indexes, and watches corpus folders using DoomSummarizer.Core pipeline.
/// </summary>
public class CorpusService : IDisposable
{
    private readonly IEmbeddingService _embedding;
    private readonly IEntityGraphStore _entityGraph;
    private readonly EntityProfileService _entityProfiles;
    private readonly ConcurrentDictionary<string, string> _fileHashes = new();
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly NerService _ner;
    private readonly WriterSettingsService _settings;
    private readonly StorageService _storage;
    private readonly DuckDbVectorStore _vectorStore;

    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new();
    private CancellationTokenSource? _watcherDebounceCts;

    public CorpusService(
        IEmbeddingService embedding,
        StorageService storage,
        NerService ner,
        EntityProfileService entityProfiles,
        DuckDbVectorStore vectorStore,
        IEntityGraphStore entityGraph,
        WriterSettingsService settings)
    {
        _embedding = embedding;
        _storage = storage;
        _ner = ner;
        _entityProfiles = entityProfiles;
        _vectorStore = vectorStore;
        _entityGraph = entityGraph;
        _settings = settings;
    }

    public bool IsInitialized { get; private set; }
    public int TotalDocuments { get; private set; }
    public int TotalSegments { get; private set; }
    public LuceneSearchService? Lucene { get; private set; }

    public void Dispose()
    {
        Lucene?.Dispose();
        foreach (var (_, watcher) in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
        _watcherDebounceCts?.Dispose();
        _indexLock.Dispose();
    }

    public event EventHandler<CorpusIndexProgress>? IndexProgress;
    public event EventHandler? IndexCompleted;

    /// <summary>
    ///     Initialize storage backends (SQLite + DuckDB + Lucene FTS).
    /// </summary>
    public async Task InitializeAsync()
    {
        await _storage.InitializeAsync();
        await _vectorStore.InitializeAsync();

        // Initialize Lucene FTS index for fast keyword search and autocomplete
        var lucenePath = Path.Combine(_storage.DataPath, "lucene", "corpus");
        Directory.CreateDirectory(lucenePath);
        Lucene = new LuceneSearchService(lucenePath);
        Lucene.Open();

        IsInitialized = true;
    }

    /// <summary>
    ///     Ingest all markdown files from a directory.
    /// </summary>
    public async Task IngestDirectoryAsync(string directoryPath, CancellationToken ct = default)
    {
        if (!Directory.Exists(directoryPath)) return;

        var files = Directory.GetFiles(directoryPath, "*.md", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(directoryPath, "*.markdown", SearchOption.AllDirectories))
            .Concat(Directory.GetFiles(directoryPath, "*.mdx", SearchOption.AllDirectories))
            .Concat(Directory.GetFiles(directoryPath, "*.txt", SearchOption.AllDirectories))
            .Distinct()
            .ToList();

        var total = files.Count;
        var processed = 0;

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await IngestFileAsync(file, ct);
                processed++;
                IndexProgress?.Invoke(this, new CorpusIndexProgress
                {
                    CurrentFile = Path.GetFileName(file),
                    ProcessedFiles = processed,
                    TotalFiles = total,
                    ProgressPercent = (float)processed / total * 100
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to ingest {file}: {ex.Message}");
            }
        }

        // Commit Lucene index after batch ingestion
        Lucene?.Commit();

        TotalDocuments = processed;
        IndexCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    ///     Ingest a single markdown file into the corpus.
    /// </summary>
    public async Task IngestFileAsync(string filePath, CancellationToken ct = default)
    {
        var content = await File.ReadAllTextAsync(filePath, ct);
        var contentHash = ComputeHash(content);

        // Skip if content hasn't changed
        if (_fileHashes.TryGetValue(filePath, out var existingHash) && existingHash == contentHash)
            return;

        _fileHashes[filePath] = contentHash;

        // Parse frontmatter metadata
        var (metadata, body) = ParseFrontmatter(content);
        var title = metadata.GetValueOrDefault("title") ?? Path.GetFileNameWithoutExtension(filePath);
        var slug = Path.GetFileNameWithoutExtension(filePath);

        // Create content item for storage
        var itemId = $"corpus:{slug}";
        var item = new ContentItem
        {
            Id = itemId,
            Source = "corpus",
            Title = title,
            Url = filePath,
            Content = body,
            CreatedAt = File.GetCreationTimeUtc(filePath),
            FetchedAt = DateTimeOffset.UtcNow
        };

        // Extract segments (paragraphs)
        var segments = ExtractSegments(body);

        // Embed segments and compute document centroid
        foreach (var segment in segments)
        {
            var embedding = await _embedding.EmbedAsync(segment.Text, ct);
            segment.Embedding = embedding;
        }

        // Compute document embedding (mean of segment embeddings)
        var validEmbeddings = segments.Where(s => s.Embedding != null).Select(s => s.Embedding!).ToList();
        if (validEmbeddings.Count > 0)
        {
            var centroid = new float[384];
            foreach (var emb in validEmbeddings)
                for (var i = 0; i < centroid.Length && i < emb.Length; i++)
                    centroid[i] += emb[i];
            for (var i = 0; i < centroid.Length; i++)
                centroid[i] /= validEmbeddings.Count;
            VectorMath.L2Normalize(centroid);
            item.Embedding = centroid;
        }

        // Store document-level embedding in vector store (DuckDB HNSW)
        if (item.Embedding != null)
            await _vectorStore.UpsertItemEmbeddingAsync(
                itemId, title, "corpus", filePath, item.Embedding);

        // Store item in SQLite
        await _storage.SaveItemAsync(item);

        // Index in Lucene FTS for keyword search and autocomplete
        item.Keywords = metadata.GetValueOrDefault("tags") ?? metadata.GetValueOrDefault("categories");
        Lucene?.IndexItem(item);

        // Extract entities and persist into knowledge graph
        await PersistEntitiesForItemAsync(itemId, title, body, ct);

        TotalSegments += segments.Count;
    }

    private async Task PersistEntitiesForItemAsync(string itemId, string title, string body, CancellationToken ct)
    {
        try
        {
            if (!_ner.IsAvailable) return;

            var entities = await _ner.ExtractEntitiesAsync(body, ct);
            if (entities.Count == 0) return;

            // Deduplicate by name, keep highest confidence
            var deduped = entities
                .GroupBy(e => e.Text.ToLowerInvariant())
                .Select(g => g.MaxBy(e => e.Confidence)!)
                .ToList();

            var entityIds = new List<string>();

            foreach (var entity in deduped)
            {
                var entityId = KnowledgeGraphService.GenerateEntityId(entity.Text, entity.Type);
                entityIds.Add(entityId);

                await _entityGraph.UpsertEntityAsync(
                    entityId, entity.Text, entity.Type, (float)entity.Confidence);
                await _entityGraph.UpsertEntityMentionAsync(
                    entityId, itemId, (float)entity.Confidence, title);
            }

            // Build co-occurrence edges (all pairs)
            for (var i = 0; i < entityIds.Count; i++)
            for (var j = i + 1; j < entityIds.Count; j++)
                await _entityGraph.UpsertRelationshipAsync(
                    entityIds[i], entityIds[j]);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Entity persistence failed for {itemId}: {ex.Message}");
        }
    }

    /// <summary>
    ///     Search the corpus using semantic similarity.
    ///     Returns matching segments ranked by relevance.
    /// </summary>
    public async Task<List<CorpusMatch>> SearchAsync(string query, int topK = 10)
    {
        var results = new List<CorpusMatch>();

        var queryEmbedding = await _embedding.EmbedAsync(query);
        var matches = await _vectorStore.FindSimilarItemsAsync(queryEmbedding, topK, 0.3f);

        foreach (var (itemId, title, url, similarity) in matches)
            results.Add(new CorpusMatch
            {
                Id = itemId,
                Score = similarity,
                Title = title,
                Text = "", // Full text retrieved from StorageService if needed
                Source = url ?? itemId
            });

        return results;
    }

    /// <summary>
    ///     Fast keyword-based suggestions using Lucene prefix matching.
    ///     Ideal for autocomplete as it avoids embedding computation.
    /// </summary>
    public List<CorpusMatch> Suggest(string prefix, int limit = 8)
    {
        if (Lucene == null || string.IsNullOrWhiteSpace(prefix))
            return [];

        return Lucene.Suggest(prefix, limit: limit)
            .Select(r => new CorpusMatch
            {
                Id = r.Id,
                Score = r.Score,
                Title = r.Title ?? r.Id,
                Text = "",
                Source = r.Source ?? "corpus"
            })
            .ToList();
    }

    /// <summary>
    ///     Full-text keyword search using Lucene FTS.
    ///     Complements the embedding-based SeachAsync with exact keyword matching.
    /// </summary>
    public List<CorpusMatch> KeywordSearch(string query, int limit = 10)
    {
        if (Lucene == null || string.IsNullOrWhiteSpace(query))
            return [];

        return Lucene.Search(query, limit: limit)
            .Select(r => new CorpusMatch
            {
                Id = r.Id,
                Score = r.Score,
                Title = r.Title ?? r.Id,
                Text = "",
                Source = r.Source ?? "corpus"
            })
            .ToList();
    }

    /// <summary>
    ///     Search corpus by entity name.
    /// </summary>
    public async Task<List<CorpusMatch>> SearchByEntityAsync(string entityName, int topK = 5)
    {
        // Embed the entity name and search
        return await SearchAsync(entityName, topK);
    }

    // --- FileSystemWatcher ---

    /// <summary>
    ///     Start watching a directory for markdown file changes.
    /// </summary>
    public void StartWatching(string directoryPath)
    {
        if (_watchers.ContainsKey(directoryPath)) return;
        if (!Directory.Exists(directoryPath)) return;

        var watcher = new FileSystemWatcher(directoryPath)
        {
            Filter = "*.md",
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        watcher.Changed += OnFileChanged;
        watcher.Created += OnFileChanged;
        watcher.Renamed += (_, e) => OnFileChanged(null,
            new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(e.FullPath) ?? "", e.Name ?? ""));

        _watchers[directoryPath] = watcher;
    }

    /// <summary>
    ///     Stop watching a directory.
    /// </summary>
    public void StopWatching(string directoryPath)
    {
        if (_watchers.TryRemove(directoryPath, out var watcher))
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
    }

    private async void OnFileChanged(object? sender, FileSystemEventArgs e)
    {
        // Debounce: wait 2 seconds before re-indexing
        _watcherDebounceCts?.Cancel();
        _watcherDebounceCts = new CancellationTokenSource();
        var ct = _watcherDebounceCts.Token;

        try
        {
            await Task.Delay(2000, ct);
            await _indexLock.WaitAsync(ct);
            try
            {
                await IngestFileAsync(e.FullPath, ct);
                Lucene?.Commit();
            }
            finally
            {
                _indexLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    ///     Start watching all configured corpus directories.
    /// </summary>
    public void StartWatchingAll()
    {
        if (!_settings.Config.AutoIndexOnChange) return;

        foreach (var dir in _settings.Config.CorpusDirectories)
            if (Directory.Exists(dir))
                StartWatching(dir);
    }

    // --- Helpers ---

    private static List<CorpusSegment> ExtractSegments(string markdown)
    {
        var segments = new List<CorpusSegment>();
        var paragraphs = markdown.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        foreach (var para in paragraphs)
        {
            var trimmed = para.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                continue;

            // Strip markdown formatting for clean text
            var cleanText = trimmed
                .Replace("**", "").Replace("__", "")
                .Replace("*", "").Replace("_", "")
                .Replace("`", "");

            if (cleanText.Length < 20) continue; // Skip very short segments

            segments.Add(new CorpusSegment
            {
                Text = cleanText,
                OriginalMarkdown = trimmed
            });
        }

        return segments;
    }

    private static (Dictionary<string, string> metadata, string body) ParseFrontmatter(string content)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!content.StartsWith("---")) return (metadata, content);

        var endIndex = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endIndex < 0) return (metadata, content);

        var frontmatter = content[3..endIndex].Trim();
        var body = content[(endIndex + 4)..].Trim();

        foreach (var line in frontmatter.Split('\n'))
        {
            var colonIndex = line.IndexOf(':');
            if (colonIndex > 0)
            {
                var key = line[..colonIndex].Trim();
                var value = line[(colonIndex + 1)..].Trim().Trim('"', '\'');
                metadata[key] = value;
            }
        }

        return (metadata, body);
    }

    private static string ComputeHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = XxHash64.Hash(bytes);
        return Convert.ToHexString(hash);
    }
}

// --- Supporting types ---

public class CorpusSegment
{
    public required string Text { get; set; }
    public required string OriginalMarkdown { get; set; }
    public float[]? Embedding { get; set; }
}

public record CorpusMatch
{
    public required string Id { get; init; }
    public required float Score { get; init; }
    public required string Title { get; init; }
    public required string Text { get; init; }
    public required string Source { get; init; }
}

public record CorpusIndexProgress
{
    public required string CurrentFile { get; init; }
    public required int ProcessedFiles { get; init; }
    public required int TotalFiles { get; init; }
    public required float ProgressPercent { get; init; }
}