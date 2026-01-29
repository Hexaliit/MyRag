using System.Collections.Concurrent;
using DoomSummarizer.Services;
using DoomWriter.Models;

namespace DoomWriter.Services;

/// <summary>
/// Manages a searchable knowledge base from markdown directories.
/// Ingests, indexes, and watches corpus folders using DoomSummarizer.Core pipeline.
/// </summary>
public class CorpusService : IDisposable
{
    private readonly EmbeddingService _embedding;
    private readonly StorageService _storage;
    private readonly NerService _ner;
    private readonly EntityProfileService _entityProfiles;
    private readonly DuckDbVectorStore _vectorStore;
    private readonly WriterSettingsService _settings;

    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, string> _fileHashes = new();
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private CancellationTokenSource? _watcherDebounceCts;

    public event EventHandler<CorpusIndexProgress>? IndexProgress;
    public event EventHandler? IndexCompleted;

    public bool IsInitialized { get; private set; }
    public int TotalDocuments { get; private set; }
    public int TotalSegments { get; private set; }

    public CorpusService(
        EmbeddingService embedding,
        StorageService storage,
        NerService ner,
        EntityProfileService entityProfiles,
        DuckDbVectorStore vectorStore,
        WriterSettingsService settings)
    {
        _embedding = embedding;
        _storage = storage;
        _ner = ner;
        _entityProfiles = entityProfiles;
        _vectorStore = vectorStore;
        _settings = settings;
    }

    /// <summary>
    /// Initialize storage backends (SQLite + DuckDB).
    /// </summary>
    public async Task InitializeAsync()
    {
        await _storage.InitializeAsync();
        await _vectorStore.InitializeAsync();

        if (_embedding.IsSetup)
            _embedding.Initialize();

        IsInitialized = true;
    }

    /// <summary>
    /// Ingest all markdown files from a directory.
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
                System.Diagnostics.Debug.WriteLine($"Failed to ingest {file}: {ex.Message}");
            }
        }

        TotalDocuments = processed;
        IndexCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Ingest a single markdown file into the corpus.
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
        var item = new DoomSummarizer.Models.ContentItem
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
        if (_embedding.IsSetup)
        {
            foreach (var segment in segments)
            {
                var embedding = _embedding.Embed(segment.Text);
                segment.Embedding = embedding;
            }

            // Compute document embedding (mean of segment embeddings)
            var validEmbeddings = segments.Where(s => s.Embedding != null).Select(s => s.Embedding!).ToList();
            if (validEmbeddings.Count > 0)
            {
                var centroid = new float[384];
                foreach (var emb in validEmbeddings)
                    for (int i = 0; i < centroid.Length && i < emb.Length; i++)
                        centroid[i] += emb[i];
                for (int i = 0; i < centroid.Length; i++)
                    centroid[i] /= validEmbeddings.Count;
                VectorMath.L2Normalize(centroid);
                item.Embedding = centroid;
            }

            // Store document-level embedding in vector store (DuckDB HNSW)
            if (item.Embedding != null)
            {
                await _vectorStore.UpsertItemEmbeddingAsync(
                    itemId, title, "corpus", filePath, item.Embedding);
            }
        }

        // Store item in SQLite
        await _storage.SaveItemAsync(item);
        TotalSegments += segments.Count;
    }

    /// <summary>
    /// Search the corpus using semantic similarity.
    /// Returns matching segments ranked by relevance.
    /// </summary>
    public async Task<List<CorpusMatch>> SearchAsync(string query, int topK = 10)
    {
        var results = new List<CorpusMatch>();

        if (!_embedding.IsSetup) return results;

        var queryEmbedding = _embedding.Embed(query);
        var matches = await _vectorStore.FindSimilarItemsAsync(queryEmbedding, topK, minSimilarity: 0.3f);

        foreach (var (itemId, title, url, similarity) in matches)
        {
            results.Add(new CorpusMatch
            {
                Id = itemId,
                Score = similarity,
                Title = title,
                Text = "", // Full text retrieved from StorageService if needed
                Source = url ?? itemId
            });
        }

        return results;
    }

    /// <summary>
    /// Search corpus by entity name.
    /// </summary>
    public async Task<List<CorpusMatch>> SearchByEntityAsync(string entityName, int topK = 5)
    {
        // Embed the entity name and search
        return await SearchAsync(entityName, topK);
    }

    // --- FileSystemWatcher ---

    /// <summary>
    /// Start watching a directory for markdown file changes.
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
        watcher.Renamed += (_, e) => OnFileChanged(null, new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(e.FullPath) ?? "", e.Name ?? ""));

        _watchers[directoryPath] = watcher;
    }

    /// <summary>
    /// Stop watching a directory.
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
            }
            finally
            {
                _indexLock.Release();
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Start watching all configured corpus directories.
    /// </summary>
    public void StartWatchingAll()
    {
        if (!_settings.Config.AutoIndexOnChange) return;

        foreach (var dir in _settings.Config.CorpusDirectories)
        {
            if (Directory.Exists(dir))
                StartWatching(dir);
        }
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
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = System.IO.Hashing.XxHash64.Hash(bytes);
        return Convert.ToHexString(hash);
    }

    public void Dispose()
    {
        foreach (var (_, watcher) in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
        _watcherDebounceCts?.Dispose();
        _indexLock.Dispose();
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
