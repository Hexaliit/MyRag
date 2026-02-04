using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DoomSummarizer.Helpers;
using DoomSummarizer.Models;
using DoomSummarizer.Services;
using Mostlylucid.DocSummarizer.Services;
using Spectre.Console;

namespace DoomSummarizer.Commands;

/// <summary>
///     Document type classification for adaptive retrieval and template selection.
///     Mirrors FrontMatterDetector.DocumentProfileType but lightweight (no LLM dependency).
/// </summary>
public enum IngestDocumentType
{
    Unknown,
    Fiction,
    NonFiction,
    Academic,
    Technical
}

/// <summary>
///     Local file/folder ingestion for the scroll command.
///     When -s points to a file or folder, or the prompt is a file path,
///     we auto-ingest into a named collection then query against it.
/// </summary>
public sealed partial class ScrollCommand
{
    private static readonly string[] ImageExtensions = [".gif", ".jpg", ".jpeg", ".png", ".webp"];

    internal static bool IsImageFile(string path)
    {
        return ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
    }

    internal static string DescriptionFromFilename(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        name = name.Replace('_', ' ').Replace('-', ' ').Replace('.', ' ');
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w =>
            w.Length > 1 ? char.ToUpper(w[0]) + w[1..] : w.ToUpper()));
    }

    /// <summary>
    ///     Classify document type from early content using heuristic scoring.
    ///     Same approach as FrontMatterDetector.DetectDocumentType but standalone.
    /// </summary>
    internal static IngestDocumentType DetectDocumentType(string earlyContent)
    {
        if (string.IsNullOrWhiteSpace(earlyContent))
            return IngestDocumentType.Unknown;

        var lower = earlyContent.ToLowerInvariant();

        var fictionScore = 0;
        var academicScore = 0;
        var technicalScore = 0;
        var nonfictionScore = 0;

        // Fiction indicators
        if (Regex.IsMatch(lower, @"\bchapter\s+(one|two|three|four|five|1|2|3|4|5|i|ii|iii|iv|v)\b"))
            fictionScore += 5;
        if (Regex.IsMatch(lower, @"\b(he|she)\s+(walked|looked|said|thought|felt|turned|whispered|smiled|sighed)\b"))
            fictionScore += 3;
        if (Regex.IsMatch(lower, @"""[^""]{5,80}[,.]""\s*\w+\s+(said|asked|replied|whispered|exclaimed)"))
            fictionScore += 5;
        if (Regex.IsMatch(lower, @"\b(mr\.|mrs\.|miss|sir|lady|lord|captain)\s+[a-z]"))
            fictionScore += 2;

        // Academic indicators
        if (lower.Contains("abstract") && lower.Contains("keywords"))
            academicScore += 8;
        if (lower.Contains("in partial fulfillment") || lower.Contains("thesis committee"))
            academicScore += 10;
        if (Regex.IsMatch(lower, @"\[\d+\]") && lower.Contains("references"))
            academicScore += 5;
        if (lower.Contains("methodology") || lower.Contains("literature review"))
            academicScore += 3;

        // Technical indicators
        if (lower.Contains("installation") || lower.Contains("getting started"))
            technicalScore += 5;
        if (lower.Contains("```") && (lower.Contains("api") || lower.Contains("configuration")))
            technicalScore += 5;
        if (lower.Contains("docker") || lower.Contains("npm") || lower.Contains("nuget"))
            technicalScore += 3;

        // Non-fiction book indicators
        if (lower.Contains("isbn") || lower.Contains("published by"))
            nonfictionScore += 5;
        if (lower.Contains("foreword") || lower.Contains("preface") || lower.Contains("introduction"))
            nonfictionScore += 3;

        var best = new[]
        {
            (IngestDocumentType.Fiction, fictionScore),
            (IngestDocumentType.Academic, academicScore),
            (IngestDocumentType.Technical, technicalScore),
            (IngestDocumentType.NonFiction, nonfictionScore)
        }.OrderByDescending(x => x.Item2).First();

        return best.Item2 >= 3 ? best.Item1 : IngestDocumentType.Unknown;
    }

    /// <summary>
    ///     Detect whether any of the given paths are local files or directories.
    ///     Returns the list of resolved file paths, a suggested collection name,
    ///     and whether the source is predominantly images.
    /// </summary>
    internal static (List<string> files, string collectionName, bool isImageSource) ResolveLocalSources(
        string[] sources, string? explicitName, bool recurse = false)
    {
        var files = new List<string>();
        var searchOption = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        // Build supported extension set from registered handlers (single source of truth)
        var handlers = new DocumentHandlerRegistry();
        handlers.RegisterDefaultHandlers();
        var supportedExtensions = new HashSet<string>(
            handlers.GetSupportedExtensions(), StringComparer.OrdinalIgnoreCase);

#if FEATURE_COMPLETE
        // Complete build: also accept images and plugin formats
        foreach (var imgExt in ImageExtensions)
            supportedExtensions.Add(imgExt);
        foreach (var pluginExt in PluginDiscovery.DiscoverAllProcessorPlugins()
                     .SelectMany(p => p.Metadata.SupportedExtensions))
            supportedExtensions.Add(pluginExt.StartsWith('.') ? pluginExt : $".{pluginExt}");
#endif

        foreach (var source in sources)
            if (File.Exists(source))
            {
                var ext = Path.GetExtension(source);
                if (!supportedExtensions.Contains(ext))
                    continue;
                files.Add(Path.GetFullPath(source));
            }
            else if (Directory.Exists(source))
            {
                foreach (var ext in supportedExtensions)
                    files.AddRange(Directory.EnumerateFiles(source, $"*{ext}", searchOption));
            }

        if (files.Count == 0)
            return (files, "", false);

        // Derive collection name: explicit --name, or auto from path
        var name = explicitName
                   ?? CollectionNaming.Auto(sources.First(s => File.Exists(s) || Directory.Exists(s)));

        // Detect if this is primarily an image source (majority of files are images)
        var isImageSource = files.Count > 0 && files.Count(IsImageFile) > files.Count / 2;

        return (files, name, isImageSource);
    }

    /// <summary>
    ///     Ingest local files into a named collection.
    ///     Extracts text, chunks by page/section, computes embeddings, stores in SQLite + Lucene FTS.
    ///     Returns the source filter string, item count, and detected document type.
    /// </summary>
    internal static async Task<(string sourceFilter, int itemCount, IngestDocumentType docType)> IngestLocalFilesAsync(
        List<string> files,
        string collectionName,
        CommandBootstrap boot,
        ProgressTask progressTask,
        bool force,
        CancellationToken ct)
    {
        var sourceTag = $"file:{collectionName}";

        // Skip ingestion if collection already has items (use --force to re-ingest)
        var existing = await boot.Storage.GetRecentItemsAsync(36500, sourceTag);
        if (existing.Count > 0 && !force)
        {
            // Detect document type from cached content
            var sampleText = string.Join("\n", existing.Take(5)
                .Select(i => $"{i.Title} {i.Content ?? i.Summary ?? ""}"));
            if (sampleText.Length > 5000) sampleText = sampleText[..5000];
            var cachedDocType = DetectDocumentType(sampleText);

            progressTask.Value = 100;
            progressTask.Description =
                $"[green]Collection '{FormattingHelpers.Esc(collectionName)}' already has {existing.Count} segments (use --force to re-ingest)[/]";
            return (sourceTag, existing.Count, cachedDocType);
        }

        using var processor =
            await ItemProcessor.CreateAsync(boot.Embedding, boot.Storage, boot.EntityStore, collectionName, ct);

        // Set up document handlers
        var handlers = new DocumentHandlerRegistry();
        handlers.RegisterDefaultHandlers();

        var totalIngested = 0;
        var increment = files.Count > 0 ? 80.0 / files.Count : 80.0;
        var detectedDocType = IngestDocumentType.Unknown;
        var docTypeDetected = false;

        // Collect all items for batch embedding
        var pendingItems = new List<(ContentItem item, string embedText)>();

        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();

            // Image files: create a lightweight content item from filename (no document handler needed)
            if (IsImageFile(filePath))
            {
                var description = DescriptionFromFilename(filePath);
                var item = new ContentItem
                {
                    Id = $"file:{collectionName}:{GenerateChunkId(filePath, 0)}",
                    Source = sourceTag,
                    Title = description,
                    Url = $"file://{filePath}",
                    Content = description,
                    Summary = description,
                    ImageUrl = filePath,
                    IsEnriched = true,
                    CreatedAt = File.GetCreationTimeUtc(filePath),
                    FetchedAt = DateTimeOffset.UtcNow
                };
                pendingItems.Add((item, ItemProcessor.PrepareEmbeddingText(description, description)));
                progressTask.Increment(increment);
                continue;
            }

            var handler = handlers.GetHandlerForFile(filePath);
            if (handler == null)
            {
                progressTask.Increment(increment);
                continue;
            }

            progressTask.Description = $"[cyan]Ingesting {FormattingHelpers.Esc(Path.GetFileName(filePath))}[/]";

            try
            {
                var options = new DocumentHandlerOptions { CancellationToken = ct };
                var content = await handler.ProcessAsync(filePath, options);

                if (string.IsNullOrWhiteSpace(content.Markdown))
                {
                    progressTask.Increment(increment);
                    continue;
                }

                // Detect document type from the first file's content (once)
                if (!docTypeDetected)
                {
                    var sample = content.Markdown.Length > 5000 ? content.Markdown[..5000] : content.Markdown;
                    detectedDocType = DetectDocumentType(sample);
                    docTypeDetected = true;
                }

                // Chunk the content with adaptive size based on document type
                var chunks = ChunkDocument(content.Markdown, content.Title ?? "", filePath, detectedDocType);

                foreach (var chunk in chunks)
                {
                    var item = new ContentItem
                    {
                        Id = $"file:{collectionName}:{GenerateChunkId(filePath, chunk.index)}",
                        Source = sourceTag,
                        Title = chunk.title,
                        Url = $"file://{filePath}",
                        Content = chunk.text,
                        Summary = chunk.text.Length > 300 ? chunk.text[..300] + "..." : chunk.text,
                        IsEnriched = true,
                        CreatedAt = File.GetCreationTimeUtc(filePath),
                        FetchedAt = DateTimeOffset.UtcNow
                    };

                    var textToEmbed = ItemProcessor.PrepareEmbeddingText(chunk.title, chunk.text);

                    pendingItems.Add((item, textToEmbed));
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Warning: Failed to process {Markup.Escape(Path.GetFileName(filePath))}: {Markup.Escape(ex.Message)}[/]");
            }

            progressTask.Increment(increment);
        }

        // Batch embed all chunks (much faster than sequential single calls)
        if (pendingItems.Count > 0)
        {
            progressTask.Description = $"[cyan]Computing embeddings for {pendingItems.Count} chunks[/]";
            var texts = pendingItems.Select(p => p.embedText).ToList();
            var embeddings = await boot.Embedding.EmbedBatchAsync(texts, ct);

            var allItems = new List<ContentItem>(pendingItems.Count);
            for (var i = 0; i < pendingItems.Count; i++)
            {
                var (item, _) = pendingItems[i];
                if (i < embeddings.Length)
                    item.Embedding = embeddings[i];

                processor.ScoreSentimentAndTopic(item);
                allItems.Add(item);
            }

            // Batch index: single DB transaction for all items (instead of 3 ops × N items)
            progressTask.Description = $"[cyan]Indexing {allItems.Count} chunks[/]";
            await processor.IndexBatchAsync(allItems);
            totalIngested = allItems.Count;
        }

        processor.CommitLucene();

        // NER entity extraction: extract character names, locations, organizations from chunks.
        // For fiction, this populates the knowledge graph with character names so queries about
        // "characters" can leverage entity-aware retrieval.
        var entityCount = 0;
        if (boot.EntityStore != null && pendingItems.Count > 0)
            try
            {
                using var nerService = new NerService();
                if (nerService.IsAvailable)
                {
                    await nerService.InitializeAsync(ct);
                    progressTask.Description = $"[cyan]Extracting entities from {pendingItems.Count} chunks[/]";

                    foreach (var (item, _) in pendingItems)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!string.IsNullOrEmpty(item.ImageUrl)) continue;
                        var textForNer = ItemProcessor.PrepareNerText(item.Title, item.Content);
                        var entities = await nerService.ExtractEntitiesAsync(textForNer, ct);
                        if (entities.Count > 0)
                        {
                            await processor.PersistEntitiesAsync(item, entities);
                            entityCount += entities.Count;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]NER extraction skipped: {Markup.Escape(ex.Message)}[/]");
            }

        progressTask.Value = 100;
        var typeLabel = detectedDocType != IngestDocumentType.Unknown ? $" ({detectedDocType})" : "";
        var entityLabel = entityCount > 0 ? $", {entityCount} entities" : "";
        progressTask.Description =
            $"[green]Ingested {totalIngested} segments from {files.Count} file(s){typeLabel}{entityLabel}[/]";

        return (sourceTag, totalIngested, detectedDocType);
    }

    /// <summary>
    ///     Split a document into retrieval-sized chunks.
    ///     Delegates to the shared DocumentChunker from DocSummarizer.Core which handles
    ///     page markers (PDFs), heading-based splitting, paragraph fallback, and section merging.
    ///     Chunk size adapts to document type: books use larger chunks for narrative continuity.
    /// </summary>
    private static List<(string title, string text, int index)> ChunkDocument(
        string markdown, string docTitle, string filePath,
        IngestDocumentType docType = IngestDocumentType.Unknown)
    {
        // Books need larger chunks to preserve narrative context (~5000 chars = ~1250 tokens)
        // Technical/general content uses default (~400 tokens = ~1600 chars)
        var targetTokens = docType is IngestDocumentType.Fiction or IngestDocumentType.NonFiction
            ? 1250
            : 400;
        var minTokens = docType is IngestDocumentType.Fiction or IngestDocumentType.NonFiction
            ? 200
            : 50;

        var chunker = new DocumentChunker(
            targetChunkTokens: targetTokens, minChunkTokens: minTokens);
        var docChunks = chunker.ChunkByStructure(markdown);

        return docChunks
            .Select(c => (
                title: !string.IsNullOrEmpty(c.Heading) ? c.Heading : docTitle,
                text: c.Content,
                index: c.Order))
            .ToList();
    }

    private static string GenerateChunkId(string filePath, int chunkIndex)
    {
        var input = $"{filePath}:{chunkIndex}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}