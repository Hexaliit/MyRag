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
///     Document complexity profile computed in a single O(N) pass over the full text.
///     Drives adaptive chunk sizing and dedup thresholds — no ML, pure heuristics.
/// </summary>
public record DocumentComplexityProfile
{
    /// <summary>Type-Token Ratio with length correction (Guiraud's index / sqrt(N)). 0-1.</summary>
    public float VocabularyRichness { get; init; }

    /// <summary>Avg words/sentence normalized to 0-1 (8 words=0, 35+ words=1).</summary>
    public float SentenceLengthScore { get; init; }

    /// <summary>Fraction of characters inside quoted speech. 0-1.</summary>
    public float DialogueDensity { get; init; }

    /// <summary>Capitalized non-sentence-starters / total words. 0-1.</summary>
    public float EntityDensity { get; init; }

    /// <summary>Fraction of characters inside code fences. 0-1.</summary>
    public float CodeDensity { get; init; }

    /// <summary>Headings per 1000 chars, clamped to 0-1.</summary>
    public float StructuralDensity { get; init; }

    /// <summary>Weighted composite score for the detected document type. 0-1.</summary>
    public float CompositeComplexity { get; init; }

    /// <summary>Total character count of the profiled text.</summary>
    public int TotalChars { get; init; }
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

    /// <summary>
    ///     Heuristic: does the input look like a file/directory path rather than a search query?
    ///     Used to show a "file not found" error instead of treating paths as search text.
    /// </summary>
    internal static bool LooksLikeFilePath(string input)
    {
        // URLs are not file paths
        if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;

        // Windows drive letter (C:\...) or UNC path (\\server\...)
        if (input.Length >= 3 && char.IsLetter(input[0]) && input[1] == ':' &&
            (input[2] == '\\' || input[2] == '/'))
            return true;

        if (input.StartsWith(@"\\", StringComparison.Ordinal))
            return true;

        // Has path separators + a file extension
        var hasSeparator = input.Contains('\\') || input.Contains('/');
        var ext = Path.GetExtension(input);
        return hasSeparator && ext.Length > 1;
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
        }.MaxBy(x => x.Item2);

        return best.Item2 >= 3 ? best.Item1 : IngestDocumentType.Unknown;
    }

    /// <summary>
    ///     Compute a complexity profile for the full document text in a single O(N) pass.
    ///     Counts words, sentences, dialogue chars, code-fence chars, headings, and
    ///     capitalized non-sentence-starters to produce six signals plus a composite score.
    /// </summary>
    internal static DocumentComplexityProfile ComputeComplexityProfile(string text, IngestDocumentType docType)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new DocumentComplexityProfile();

        var totalChars = text.Length;
        var wordSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalWords = 0;
        var totalSentences = 0;
        var wordsInCurrentSentence = 0;
        var totalWordsForAvg = 0; // sum of words across sentences
        var dialogueChars = 0;
        var codeChars = 0;
        var entityWords = 0; // capitalized non-sentence-starters
        var headingCount = 0;
        var insideQuote = false;
        var insideCode = false;
        var lineStart = true;
        var sentenceStart = true;
        var wordStart = -1;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            // Track code fences: ``` at line start
            if (lineStart && i + 2 < text.Length && c == '`' && text[i + 1] == '`' && text[i + 2] == '`')
            {
                insideCode = !insideCode;
                i += 2;
                lineStart = false;
                continue;
            }

            if (insideCode)
            {
                codeChars++;
                if (c == '\n') lineStart = true;
                else lineStart = false;
                continue;
            }

            // Track headings: # at line start
            if (lineStart && c == '#')
            {
                // Count consecutive # chars
                var hLevel = 0;
                var j = i;
                while (j < text.Length && text[j] == '#') { hLevel++; j++; }
                if (j < text.Length && text[j] == ' ' && hLevel <= 6)
                    headingCount++;
            }

            // Track dialogue: content between paired double-quotes
            if (c == '"' || c == '\u201C' || c == '\u201D')
            {
                insideQuote = !insideQuote;
                lineStart = false;
                continue;
            }

            if (insideQuote)
                dialogueChars++;

            // Newline handling
            if (c == '\n')
            {
                lineStart = true;
                // Flush current word if any
                if (wordStart >= 0)
                {
                    FlushWord(text, wordStart, i, wordSet, ref totalWords,
                        ref wordsInCurrentSentence, sentenceStart, ref entityWords);
                    sentenceStart = false;
                    wordStart = -1;
                }
                continue;
            }

            lineStart = c == '\r'; // \r before \n resets lineStart

            // Word boundaries
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c))
            {
                if (wordStart >= 0)
                {
                    FlushWord(text, wordStart, i, wordSet, ref totalWords,
                        ref wordsInCurrentSentence, sentenceStart, ref entityWords);
                    sentenceStart = false;
                    wordStart = -1;
                }

                // Sentence boundaries
                if (c is '.' or '!' or '?')
                {
                    if (wordsInCurrentSentence > 0)
                    {
                        totalSentences++;
                        totalWordsForAvg += wordsInCurrentSentence;
                        wordsInCurrentSentence = 0;
                    }
                    sentenceStart = true;
                }
            }
            else
            {
                if (wordStart < 0)
                    wordStart = i;
            }
        }

        // Flush trailing word/sentence
        if (wordStart >= 0)
        {
            FlushWord(text, wordStart, text.Length, wordSet, ref totalWords,
                ref wordsInCurrentSentence, sentenceStart, ref entityWords);
        }
        if (wordsInCurrentSentence > 0)
        {
            totalSentences++;
            totalWordsForAvg += wordsInCurrentSentence;
        }

        // Compute signals
        var vocabRichness = totalWords > 0
            ? Math.Clamp((float)(wordSet.Count / Math.Sqrt(totalWords)) / 15f, 0f, 1f)
            : 0f;

        var avgWordsPerSentence = totalSentences > 0 ? (float)totalWordsForAvg / totalSentences : 15f;
        var sentenceLengthScore = Math.Clamp((avgWordsPerSentence - 8f) / 27f, 0f, 1f); // 8→0, 35→1

        var dialogueDensity = totalChars > 0 ? Math.Clamp((float)dialogueChars / totalChars, 0f, 1f) : 0f;
        var entityDensity = totalWords > 0 ? Math.Clamp((float)entityWords / totalWords, 0f, 1f) : 0f;
        var codeDensity = totalChars > 0 ? Math.Clamp((float)codeChars / totalChars, 0f, 1f) : 0f;
        var structuralDensity = totalChars > 0
            ? Math.Clamp(headingCount / (totalChars / 1000f), 0f, 1f)
            : 0f;

        // Composite weighted by doc type
        var composite = docType switch
        {
            IngestDocumentType.Fiction =>
                0.35f * vocabRichness + 0.30f * sentenceLengthScore +
                0.20f * entityDensity + 0.15f * dialogueDensity,
            IngestDocumentType.Technical =>
                0.25f * structuralDensity + 0.25f * codeDensity +
                0.25f * vocabRichness + 0.25f * sentenceLengthScore,
            IngestDocumentType.Academic =>
                0.25f * vocabRichness + 0.25f * sentenceLengthScore +
                0.25f * entityDensity + 0.25f * structuralDensity,
            _ => // Unknown / NonFiction: balanced
                0.20f * vocabRichness + 0.20f * sentenceLengthScore +
                0.20f * entityDensity + 0.20f * codeDensity + 0.20f * structuralDensity
        };

        return new DocumentComplexityProfile
        {
            VocabularyRichness = vocabRichness,
            SentenceLengthScore = sentenceLengthScore,
            DialogueDensity = dialogueDensity,
            EntityDensity = entityDensity,
            CodeDensity = codeDensity,
            StructuralDensity = structuralDensity,
            CompositeComplexity = Math.Clamp(composite, 0f, 1f),
            TotalChars = totalChars
        };

        static void FlushWord(string text, int start, int end, HashSet<string> wordSet,
            ref int totalWords, ref int wordsInSentence, bool sentenceStart, ref int entityWords)
        {
            var word = text[start..end];
            if (word.Length < 2) return;
            wordSet.Add(word.ToLowerInvariant());
            totalWords++;
            wordsInSentence++;
            // Entity detection: capitalized word that isn't at sentence start
            if (!sentenceStart && char.IsUpper(word[0]))
                entityWords++;
        }
    }

    /// <summary>
    ///     Compute adaptive chunk target tokens based on document length, type, and complexity.
    ///     Replaces the static 1250/400 with length-band tables and complexity adjustment.
    /// </summary>
    internal static (int targetTokens, int minTokens) ComputeChunkTargets(
        int documentChars, IngestDocumentType docType, DocumentComplexityProfile? profile)
    {
        int baseTarget, minTokens;

        if (docType is IngestDocumentType.Fiction or IngestDocumentType.NonFiction)
        {
            (baseTarget, minTokens) = documentChars switch
            {
                < 15_000 => (300, 75),
                < 50_000 => (450, 100),
                < 150_000 => (600, 150),
                < 400_000 => (800, 200),
                _ => (1000, 200)
            };
        }
        else // Technical, Academic, Unknown
        {
            (baseTarget, minTokens) = documentChars switch
            {
                < 20_000 => (250, 50),
                < 80_000 => (350, 50),
                < 250_000 => (450, 50),
                _ => (550, 50)
            };
        }

        // Complexity adjustment: higher complexity → slightly smaller chunks for more precise retrieval
        if (profile != null)
        {
            var multiplier = 1.0f - 0.3f * profile.CompositeComplexity;
            baseTarget = Math.Max(minTokens, (int)(baseTarget * multiplier));
        }

        return (baseTarget, minTokens);
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
        DocumentComplexityProfile? lastProfile = null; // Track for adaptive dedup limits

        // Embedding rate: percentage of chunks to embed (100 = all, 50 = top half by salience)
        var embeddingRate = Math.Clamp(boot.Config.Ingestion.EmbeddingRate, 1, 100);

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
                    FetchedAt = DateTimeOffset.UtcNow,
                    SalienceScore = 1.0f, // Images always high salience
                    IsEmbedded = true
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

                // Compute complexity profile for adaptive chunking
                var complexityProfile = ComputeComplexityProfile(content.Markdown, detectedDocType);
                lastProfile = complexityProfile;

                // Chunk the content with adaptive size based on document type and complexity
                var chunks = ChunkDocument(content.Markdown, content.Title ?? "", filePath, detectedDocType,
                    complexityProfile);

                foreach (var chunk in chunks)
                {
                    // Compute heuristic salience score for this chunk
                    var salience = EstimateChunkSalience(chunk.text, chunk.index, chunks.Count, detectedDocType,
                        complexityProfile);

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
                        FetchedAt = DateTimeOffset.UtcNow,
                        SalienceScore = salience,
                        IsEmbedded = true
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

        // === Pre-embedding cheap dedup (eliminates obvious duplicates before GPU work) ===
        var preDedup = boot.Config.Ingestion.PreDedup;
        var preDisposed = 0;
        if (!preDedup.IsDisabled && pendingItems.Count > 1)
        {
            var beforeCount = pendingItems.Count;
            pendingItems = PreDeduplicateChunks(pendingItems, preDedup);
            preDisposed = beforeCount - pendingItems.Count;
            if (preDisposed > 0)
                progressTask.Description =
                    $"[cyan]Pre-dedup: {beforeCount} → {pendingItems.Count} chunks ({preDisposed} eliminated by text similarity)[/]";
        }

        // Apply embedding rate: sort by salience and embed top N%
        // embedding_rate=100 → embed all, 50 → top half by salience
        var sortedBySalience = pendingItems
            .OrderByDescending(p => p.item.SalienceScore ?? 0f)
            .ToList();

        var embedCount = embeddingRate >= 100
            ? sortedBySalience.Count
            : Math.Max(1, (int)Math.Ceiling(sortedBySalience.Count * embeddingRate / 100.0));

        var toEmbed = sortedBySalience.Take(embedCount).ToList();
        var deferred = sortedBySalience.Skip(embedCount).ToList();

        // Mark deferred items
        foreach (var (item, _) in deferred)
            item.IsEmbedded = false;

        // Batch embed selected chunks
        if (toEmbed.Count > 0)
        {
            var embeddedLabel = deferred.Count > 0
                ? $"Embedding {toEmbed.Count}/{sortedBySalience.Count} chunks ({embeddingRate}% rate, {deferred.Count} deferred)"
                : $"Computing embeddings for {toEmbed.Count} chunks";
            progressTask.Description = $"[cyan]{embeddedLabel}[/]";

            var texts = toEmbed.Select(p => p.embedText).ToList();
            var embeddings = await boot.Embedding.EmbedBatchAsync(texts, ct);

            for (var i = 0; i < toEmbed.Count; i++)
            {
                var (item, _) = toEmbed[i];
                if (i < embeddings.Length)
                    item.Embedding = embeddings[i];
            }
        }

        // === Per-document semantic deduplication ===
        // Group embedded chunks by source file, dedup within each group, then merge survivors
        var ingestionConfig = boot.Config.Ingestion;
        var dedupedItems = new List<ContentItem>();
        var totalAbsorbed = 0;

        if (ingestionConfig.DeduplicationEnabled && toEmbed.Count > 1)
        {
            // Group by source file (URL is file://path)
            var bySource = toEmbed
                .GroupBy(p => p.item.Url ?? p.item.Source)
                .ToList();

            foreach (var group in bySource)
            {
                var groupItems = group.Select(p => p.item).ToList();
                var (minSurv, maxSurv, dedupThreshold) =
                    GetAdaptiveLimits(groupItems.Count, detectedDocType, ingestionConfig, lastProfile);

                var survivors = DeduplicateChunks(
                    groupItems, dedupThreshold, minSurv, maxSurv,
                    ingestionConfig.SalienceBoostEnabled);

                var absorbed = groupItems.Count - survivors.Count;
                totalAbsorbed += absorbed;
                dedupedItems.AddRange(survivors);
            }

            if (totalAbsorbed > 0)
            {
                var pct = toEmbed.Count > 0 ? (int)(100.0 * totalAbsorbed / toEmbed.Count) : 0;
                progressTask.Description =
                    $"[cyan]Dedup: {toEmbed.Count} → {dedupedItems.Count} chunks ({pct}% reduction, {detectedDocType})[/]";
            }
        }
        else
        {
            dedupedItems = toEmbed.Select(p => p.item).ToList();
        }

        // Score sentiment/topic on surviving embedded items
        foreach (var item in dedupedItems)
            processor.ScoreSentimentAndTopic(item);

        // Deferred items get sentiment/topic but no embedding
        foreach (var (item, _) in deferred)
            processor.ScoreSentimentAndTopic(item);

        // Merge survivors + deferred for indexing
        // Survivors: SQLite + FTS5 + Lucene + HNSW
        // Deferred: SQLite + FTS5 + Lucene only (no HNSW — null embedding)
        var allItems = new List<ContentItem>(dedupedItems.Count + deferred.Count);
        allItems.AddRange(dedupedItems);
        allItems.AddRange(deferred.Select(p => p.item));

        if (allItems.Count > 0)
        {
            progressTask.Description = $"[cyan]Indexing {allItems.Count} chunks[/]";
            await processor.IndexBatchAsync(allItems);
            totalIngested = allItems.Count;
        }

        processor.CommitLucene();

        // NER entity extraction on surviving + deferred chunks (not absorbed)
        var entityCount = 0;
        if (boot.EntityStore != null && allItems.Count > 0)
            try
            {
                using var nerService = new NerService();
                if (nerService.IsAvailable)
                {
                    await nerService.InitializeAsync(ct);
                    var nerTotal = allItems.Count;
                    var nerProcessed = 0;
                    progressTask.Description = $"[cyan]Extracting entities: 0/{nerTotal} chunks[/]";

                    foreach (var item in allItems)
                    {
                        ct.ThrowIfCancellationRequested();
                        nerProcessed++;
                        if (!string.IsNullOrEmpty(item.ImageUrl)) continue;
                        var textForNer = ItemProcessor.PrepareNerText(item.Title, item.Content);
                        var entities = await nerService.ExtractEntitiesAsync(textForNer, ct);
                        if (entities.Count > 0)
                        {
                            await processor.PersistEntitiesAsync(item, entities);
                            entityCount += entities.Count;
                        }

                        progressTask.Description =
                            $"[cyan]Extracting entities: {nerProcessed}/{nerTotal} chunks ({entityCount} found)[/]";
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
        var rateLabel = embeddingRate < 100 ? $", {embeddingRate}% embedded" : "";
        var dedupLabel = totalAbsorbed > 0 ? $", {totalAbsorbed} deduped" : "";
        var preDedupLabel = preDisposed > 0 ? $", {preDisposed} pre-disposed" : "";
        progressTask.Description =
            $"[green]Ingested {totalIngested} segments from {files.Count} file(s){typeLabel}{entityLabel}{preDedupLabel}{dedupLabel}{rateLabel}[/]";

        return (sourceTag, totalIngested, detectedDocType);
    }

    /// <summary>
    ///     Split a document into retrieval-sized chunks.
    ///     Delegates to the shared DocumentChunker from DocSummarizer.Core which handles
    ///     page markers (PDFs), heading-based splitting, paragraph fallback, and section merging.
    ///     Chunk size adapts to document type, length, and complexity profile.
    /// </summary>
    private static List<(string title, string text, int index)> ChunkDocument(
        string markdown, string docTitle, string filePath,
        IngestDocumentType docType = IngestDocumentType.Unknown,
        DocumentComplexityProfile? profile = null)
    {
        // Adaptive chunk targets from document length + complexity
        var (targetTokens, minTokens) = profile != null
            ? ComputeChunkTargets(profile.TotalChars, docType, profile)
            : ComputeChunkTargets(markdown.Length, docType, null);

        // Build boundary hints from document type
        var hints = docType switch
        {
            IngestDocumentType.Fiction => new ChunkBoundaryHints
            {
                RespectSceneBreaks = true,
                KeepDialogueTogether = true,
                RespectChapterBoundaries = true,
                OverlapFraction = 0.12f
            },
            IngestDocumentType.NonFiction => new ChunkBoundaryHints
            {
                RespectSceneBreaks = true,
                RespectChapterBoundaries = true,
                OverlapFraction = 0.10f
            },
            IngestDocumentType.Technical => new ChunkBoundaryHints
            {
                KeepCodeBlocksTogether = true,
                OverlapFraction = 0.08f
            },
            _ => new ChunkBoundaryHints
            {
                OverlapFraction = 0.08f
            }
        };

        var chunker = new DocumentChunker(
            targetChunkTokens: targetTokens, minChunkTokens: minTokens, hints: hints);
        var docChunks = chunker.ChunkByStructure(markdown);

        // Optionally prepend parent heading as contextual prefix for better embeddings
        var parentHeading = docTitle;

        return docChunks
            .Select(c =>
            {
                var heading = !string.IsNullOrEmpty(c.Heading) ? c.Heading : docTitle;
                var text = c.Content;

                // Contextual header prefix (TreeRAG): prepend parent heading if different from chunk heading
                if (!string.IsNullOrEmpty(parentHeading) &&
                    !string.Equals(heading, parentHeading, StringComparison.OrdinalIgnoreCase) &&
                    docType is IngestDocumentType.Technical or IngestDocumentType.Academic)
                {
                    text = $"[{parentHeading}] {text}";
                }

                return (title: heading, text, index: c.Order);
            })
            .ToList();
    }

    /// <summary>
    ///     Estimate a chunk's salience using cheap heuristic signals (no embedding needed).
    ///     Used to decide which chunks to embed immediately vs defer at ingestion time.
    ///     Fiction/NonFiction gets enhanced signals: dialogue density, character introduction,
    ///     chapter boundary, plot pivot (local TTR), and description passage detection.
    /// </summary>
    internal static float EstimateChunkSalience(string text, int chunkIndex, int totalChunks,
        IngestDocumentType docType, DocumentComplexityProfile? profile = null)
    {
        var score = 0.3f; // baseline

        // Heading presence: chunks with markdown headings are more structurally important
        if (Regex.IsMatch(text, @"^#{1,3}\s", RegexOptions.Multiline))
            score += 0.2f;

        // Length sweet spot (200-500 tokens ~ 800-2000 chars): substantial content
        if (text.Length is >= 800 and <= 2000)
            score += 0.1f;
        else if (text.Length < 200)
            score -= 0.15f;

        // Position bonus: first and last chunks of a document score higher
        if (chunkIndex == 0 || chunkIndex == totalChunks - 1)
            score += 0.1f;

        // Doc-type specific signals
        if (docType == IngestDocumentType.Technical && text.Contains("```"))
            score += 0.15f;

        // Academic: chunks with citations or references
        if (docType == IngestDocumentType.Academic && Regex.IsMatch(text, @"\[\d+\]"))
            score += 0.1f;

        // ── Fiction / NonFiction enhanced signals ──
        if (docType is IngestDocumentType.Fiction or IngestDocumentType.NonFiction)
        {
            // Count quoted speech segments
            var quoteCount = Regex.Matches(text, "\"[^\"]{5,}\"").Count;
            if (quoteCount >= 3)
                score += 0.15f; // Heavy dialogue: character interaction scene
            else if (quoteCount >= 1)
                score += 0.08f; // Light dialogue: some character presence

            // Character introduction patterns: "Mr./Mrs./Dr./Captain + Name"
            if (Regex.IsMatch(text,
                    @"\b(?:Mr\.|Mrs\.|Ms\.|Dr\.|Sir|Lady|Lord|Captain|Inspector|Colonel|Major|Miss)\s+[A-Z][a-z]+",
                    RegexOptions.None))
                score += 0.10f;

            // Chapter boundary text present
            if (Regex.IsMatch(text,
                    @"(?:^|\n)\s*(?:chapter|part)\s+(?:\d+|[ivxlcdm]+|one|two|three|four|five|six|seven|eight|nine|ten)",
                    RegexOptions.IgnoreCase))
                score += 0.12f;

            // Plot pivot detection: high local Type-Token Ratio vs document average
            // New vocabulary = new setting, character, or revelation
            if (profile != null && text.Length >= 200)
            {
                var words = Regex.Split(text.ToLowerInvariant(), @"[\s\p{P}]+")
                    .Where(w => w.Length >= 3).ToArray();
                if (words.Length > 10)
                {
                    var localTtr = (float)new HashSet<string>(words).Count / words.Length;
                    // If local TTR is notably higher than doc average, it's likely a pivot point
                    if (localTtr > profile.VocabularyRichness + 0.15f)
                        score += 0.08f;
                }
            }

            // Description passage: long text with no dialogue, likely setting/atmosphere
            if (quoteCount == 0 && text.Length > 400)
                score += 0.05f;
        }

        return Math.Clamp(score, 0f, 1f);
    }

    // ── Pre-Embedding Cheap Dedup ──────────────────────────────────────────

    /// <summary>
    ///     Eliminate obvious duplicates BEFORE embedding using fast text signals.
    ///     Each pair is scored by a weighted combination of cheap signals (word Jaccard,
    ///     trigrams, length, heading). Pairs above the threshold → lower-salience item removed.
    ///     O(N^2) pairwise but each comparison is ~1µs vs embedding's ~1-5ms.
    /// </summary>
    /// <returns>Items surviving the pre-dedup filter (removed items are discarded).</returns>
    internal static List<(ContentItem item, string embedText)> PreDeduplicateChunks(
        List<(ContentItem item, string embedText)> items,
        Models.PreDedupWeights weights)
    {
        if (items.Count <= 1 || weights.IsDisabled)
            return items;

        // Pre-compute fingerprints for each item
        var fingerprints = items.Select(p => new ChunkFingerprint(p.item, p.embedText)).ToArray();

        // Exact content hash dedup first (O(N) — free)
        var seenHashes = new HashSet<ulong>();
        var hashSurvivors = new List<int>();
        for (var i = 0; i < fingerprints.Length; i++)
        {
            if (seenHashes.Add(fingerprints[i].ContentHash))
                hashSurvivors.Add(i);
            // Exact dupe — silently drop (lower index = seen first)
        }

        if (hashSurvivors.Count == items.Count && weights.Threshold >= 1.0f)
            return items; // No exact dupes and threshold unreachable

        // Sort by salience descending — higher-salience items survive ties
        var sortedIndices = hashSurvivors
            .OrderByDescending(i => items[i].item.SalienceScore ?? 0f)
            .ToList();

        var absorbed = new HashSet<int>();
        var threshold = weights.Threshold;

        foreach (var i in sortedIndices)
        {
            if (absorbed.Contains(i)) continue;

            foreach (var j in sortedIndices)
            {
                if (j == i || absorbed.Contains(j)) continue;

                var score = CheapSimilarityScore(fingerprints[i], fingerprints[j], weights);
                if (score >= threshold)
                    absorbed.Add(j); // Lower-salience item (j comes after i in sorted order)
            }
        }

        return hashSurvivors
            .Where(i => !absorbed.Contains(i))
            .Select(i => items[i])
            .ToList();
    }

    /// <summary>
    ///     Compute a weighted similarity score between two chunks using cheap text signals.
    ///     All operations are O(N) per chunk text — microseconds, not milliseconds.
    /// </summary>
    internal static float CheapSimilarityScore(ChunkFingerprint a, ChunkFingerprint b, Models.PreDedupWeights w)
    {
        var score = 0f;
        var totalWeight = w.WordJaccard + w.Trigram + w.Length + w.Heading;
        if (totalWeight <= 0) return 0f;

        // 1. Word-set Jaccard similarity (bag-of-words overlap)
        if (w.WordJaccard > 0 && a.WordSet.Count > 0 && b.WordSet.Count > 0)
        {
            var intersection = 0;
            foreach (var word in a.WordSet)
                if (b.WordSet.Contains(word))
                    intersection++;
            var union = a.WordSet.Count + b.WordSet.Count - intersection;
            score += w.WordJaccard * ((float)intersection / Math.Max(union, 1));
        }

        // 2. Character trigram Jaccard (catches minor edits/paraphrases)
        if (w.Trigram > 0 && a.Trigrams.Count > 0 && b.Trigrams.Count > 0)
        {
            var intersection = 0;
            foreach (var tri in a.Trigrams)
                if (b.Trigrams.Contains(tri))
                    intersection++;
            var union = a.Trigrams.Count + b.Trigrams.Count - intersection;
            score += w.Trigram * ((float)intersection / Math.Max(union, 1));
        }

        // 3. Length similarity (1.0 when identical length, decays as lengths diverge)
        if (w.Length > 0)
        {
            var longer = Math.Max(a.CharCount, b.CharCount);
            var shorter = Math.Min(a.CharCount, b.CharCount);
            var lengthSim = longer > 0 ? (float)shorter / longer : 1f;
            score += w.Length * lengthSim;
        }

        // 4. Title/heading overlap (same heading = more likely duplicate)
        if (w.Heading > 0 && !string.IsNullOrEmpty(a.NormalizedTitle) && !string.IsNullOrEmpty(b.NormalizedTitle))
        {
            var headingSim = a.NormalizedTitle == b.NormalizedTitle ? 1f : 0f;
            score += w.Heading * headingSim;
        }

        return score / totalWeight; // Normalize by total weight
    }

    /// <summary>
    ///     Pre-computed fingerprint for cheap similarity comparison.
    ///     Computed once per chunk, reused for all pairwise comparisons.
    /// </summary>
    internal sealed partial class ChunkFingerprint
    {
        public readonly ulong ContentHash;
        public readonly HashSet<string> WordSet;
        public readonly HashSet<long> Trigrams;
        public readonly int CharCount;
        public readonly string? NormalizedTitle;

        public ChunkFingerprint(ContentItem item, string embedText)
        {
            var text = item.Content ?? embedText;
            CharCount = text.Length;
            ContentHash = System.IO.Hashing.XxHash64.HashToUInt64(
                Encoding.UTF8.GetBytes(text));

            // Word set: lowercase, split on whitespace/punctuation, deduplicated
            WordSet = new HashSet<string>(
                WordSplitRegex().Split(text.ToLowerInvariant())
                    .Where(w => w.Length >= 3),
                StringComparer.Ordinal);

            // Character trigrams: rolling hash of 3-char windows
            Trigrams = new HashSet<long>();
            var lower = text.ToLowerInvariant();
            for (var i = 0; i <= lower.Length - 3; i++)
            {
                long tri = ((long)lower[i] << 16) | ((long)lower[i + 1] << 8) | lower[i + 2];
                Trigrams.Add(tri);
            }

            // Normalized title for heading comparison
            NormalizedTitle = string.IsNullOrWhiteSpace(item.Title)
                ? null
                : item.Title.Trim().ToLowerInvariant();
        }

        [GeneratedRegex(@"[\s\p{P}]+", RegexOptions.Compiled)]
        private static partial Regex WordSplitRegex();
    }

    // ── Post-Embedding Semantic Dedup ───────────────────────────────────

    /// <summary>
    ///     Get adaptive chunk limits based on document type, raw chunk count, and complexity profile.
    ///     When a profile is available, uses formula-based limits that scale with chunk count and complexity.
    ///     Without a profile, falls back to the legacy static lookup table.
    ///     Returns (minSurvivors, maxSurvivors, dedupThreshold) tuned per category.
    /// </summary>
    internal static (int min, int max, float threshold) GetAdaptiveLimits(
        int rawChunkCount, IngestDocumentType docType, Models.IngestionConfig config,
        DocumentComplexityProfile? profile = null)
    {
        // Config overrides take precedence
        var minOverride = config.MinChunksOverride;
        var maxOverride = config.MaxChunksOverride;

        int min, max;
        float threshold;

        if (profile != null)
        {
            // Formula-based: scales with chunk count and complexity
            var complexity = profile.CompositeComplexity;
            min = Math.Max(10, (int)(rawChunkCount * 0.55));
            var maxFraction = 0.80f + 0.10f * complexity;
            max = Math.Max(min + 5, (int)(rawChunkCount * maxFraction));
            threshold = 0.88f + 0.08f * complexity;
        }
        else
        {
            // Legacy static lookup table (backward compatibility when profile is null)
            (min, max, threshold) = docType switch
            {
                IngestDocumentType.Fiction when rawChunkCount > 200 =>
                    (50, 200, 0.85f),
                IngestDocumentType.Fiction when rawChunkCount > 40 =>
                    (30, 120, 0.88f),
                IngestDocumentType.Fiction =>
                    (15, 40, 0.92f),

                IngestDocumentType.Technical when rawChunkCount > 100 =>
                    (40, 150, 0.88f),
                IngestDocumentType.Technical =>
                    (15, 80, 0.92f),

                IngestDocumentType.Academic =>
                    (15, 80, 0.90f),

                IngestDocumentType.NonFiction when rawChunkCount > 40 =>
                    (20, 120, 0.88f),

                // Default / Unknown
                _ => (15, Math.Max(rawChunkCount, 80), 0.90f)
            };
        }

        // Apply user overrides
        if (minOverride > 0) min = minOverride;
        if (maxOverride > 0) max = maxOverride;

        // Use config threshold if it differs from the model default (0.90)
        if (Math.Abs(config.DeduplicationThreshold - 0.90f) > 0.001f)
            threshold = config.DeduplicationThreshold;

        return (min, max, threshold);
    }

    /// <summary>
    ///     Per-document semantic deduplication. Finds near-duplicate chunks by cosine similarity
    ///     and merges them, boosting the surviving chunk's salience score. O(N^2) pairwise
    ///     comparison where N = chunks per document — fast for typical document sizes.
    /// </summary>
    /// <returns>List of surviving chunks after dedup, sorted by original order.</returns>
    internal static List<ContentItem> DeduplicateChunks(
        List<ContentItem> items,
        float threshold,
        int minSurvivors,
        int maxSurvivors,
        bool salienceBoost)
    {
        if (items.Count <= minSurvivors)
            return items; // Already under minimum — keep all

        // Sort by salience (highest first = canonical chunks)
        var sorted = items
            .OrderByDescending(i => i.SalienceScore ?? 0f)
            .ToList();

        var absorbed = new HashSet<string>();
        var survivors = new List<ContentItem>();

        foreach (var item in sorted)
        {
            if (absorbed.Contains(item.Id)) continue;

            var mergeCount = 0;

            // Check remaining items for near-duplicates
            foreach (var candidate in sorted)
            {
                if (candidate.Id == item.Id || absorbed.Contains(candidate.Id))
                    continue;

                if (item.Embedding == null || candidate.Embedding == null)
                    continue;

                var sim = VectorMath.CosineSimilarity(item.Embedding, candidate.Embedding);
                if (sim >= threshold)
                {
                    absorbed.Add(candidate.Id);
                    mergeCount++;
                }
            }

            // Boost salience by absorbed count (logarithmic decay)
            if (mergeCount > 0 && salienceBoost)
            {
                var boost = 0.15f * (float)Math.Log2(1 + mergeCount);
                item.SalienceScore = Math.Clamp(
                    (item.SalienceScore ?? 0.3f) + boost, 0f, 1f);
            }

            survivors.Add(item);
        }

        // Apply max limit: if still over max, keep top-N by salience
        if (survivors.Count > maxSurvivors)
            survivors = survivors
                .OrderByDescending(i => i.SalienceScore ?? 0f)
                .Take(maxSurvivors)
                .ToList();

        return survivors;
    }

    private static string GenerateChunkId(string filePath, int chunkIndex)
    {
        var input = $"{filePath}:{chunkIndex}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}