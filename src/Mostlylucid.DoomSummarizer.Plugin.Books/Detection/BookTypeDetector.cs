using System.Text.RegularExpressions;
using DoomSummarizer.Helpers;

namespace Mostlylucid.DoomSummarizer.Plugin.Books.Detection;

/// <summary>
///     Detected book type with confidence scores from multiple signals.
/// </summary>
public record BookTypeResult
{
    /// <summary>Primary detected type.</summary>
    public required string Type { get; init; }

    /// <summary>Confidence (0-1) for the primary type.</summary>
    public required double Confidence { get; init; }

    /// <summary>All signal votes with their weights and contributions.</summary>
    public required IReadOnlyList<DetectionSignal> Signals { get; init; }

    /// <summary>All type scores (for debugging / "range of opinions").</summary>
    public required IReadOnlyDictionary<string, double> TypeScores { get; init; }
}

/// <summary>
///     A single detection signal and its contribution to the score.
/// </summary>
public record DetectionSignal
{
    public required string Name { get; init; }
    public required string Category { get; init; } // "structural", "filename", "metadata", "content"
    public required double Weight { get; init; }
    public required string VotedType { get; init; }
    public required string Reason { get; init; }
}

/// <summary>
///     Lightweight, fast book type detector using weighted signals.
///     No LLM calls — uses filename, file size, word count, structural markers,
///     and content-based heuristics to produce a range of "opinions" about the document type.
/// </summary>
public static partial class BookTypeDetector
{
    // Known document types
    public const string Fiction = "fiction";
    public const string NonFiction = "nonfiction";
    public const string Academic = "academic";
    public const string Technical = "technical";
    public const string Anthology = "anthology";
    public const string Play = "play";
    public const string Collection = "collection"; // Poetry collections, short story anthologies, essay compilations
    public const string Unknown = "unknown";

    /// <summary>
    ///     Detect the document type using fast, structural signals.
    ///     Returns a result with the primary type, confidence, and all signal votes.
    /// </summary>
    public static BookTypeResult Detect(
        string content,
        string? fileName = null,
        long? fileSize = null,
        int? wordCount = null,
        Dictionary<string, string>? metadata = null)
    {
        var signals = new List<DetectionSignal>();
        var effectiveWordCount = wordCount ?? WordCounter.Count(content);

        // --- FILENAME SIGNALS (very fast, high confidence when they match) ---
        if (fileName != null)
            CollectFilenameSignals(fileName, signals);

        // --- SIZE / LENGTH SIGNALS ---
        CollectSizeSignals(effectiveWordCount, fileSize, signals);

        // --- METADATA SIGNALS (from reader extraction) ---
        if (metadata != null)
            CollectMetadataSignals(metadata, signals);

        // --- STRUCTURAL SIGNALS (scan first ~5000 chars) ---
        var earlyContent = content.Length > 5000 ? content[..5000] : content;
        CollectStructuralSignals(earlyContent, signals);

        // --- CONTENT SIGNALS (deeper scan of early text) ---
        CollectContentSignals(earlyContent, signals);

        // Tally weighted votes
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            [Fiction] = 0, [NonFiction] = 0, [Academic] = 0,
            [Technical] = 0, [Anthology] = 0, [Play] = 0, [Collection] = 0
        };

        foreach (var signal in signals)
            if (scores.ContainsKey(signal.VotedType))
                scores[signal.VotedType] += signal.Weight;

        var totalWeight = scores.Values.Sum();
        var best = scores.OrderByDescending(kv => kv.Value).First();
        var confidence = totalWeight > 0 ? best.Value / totalWeight : 0;

        return new BookTypeResult
        {
            Type = best.Value > 0 ? best.Key : Unknown,
            Confidence = confidence,
            Signals = signals,
            TypeScores = scores
        };
    }

    private static void CollectFilenameSignals(string fileName, List<DetectionSignal> signals)
    {
        var lower = fileName.ToLowerInvariant();
        var name = Path.GetFileNameWithoutExtension(lower);

        // Extension signals
        var ext = Path.GetExtension(lower);
        if (ext is ".epub" or ".mobi")
            signals.Add(new DetectionSignal
            {
                Name = "ebook_extension", Category = "filename", Weight = 3.0, VotedType = Fiction,
                Reason = $"eBook format ({ext})"
            });

        // Filename content signals
        if (name.Contains("novel") || name.Contains("story") || name.Contains("tales"))
            signals.Add(new DetectionSignal
            {
                Name = "fiction_filename", Category = "filename", Weight = 5.0, VotedType = Fiction,
                Reason = "Filename suggests fiction"
            });

        if (name.Contains("paper") || name.Contains("thesis") || name.Contains("dissertation"))
            signals.Add(new DetectionSignal
            {
                Name = "academic_filename", Category = "filename", Weight = 5.0, VotedType = Academic,
                Reason = "Filename suggests academic"
            });

        if (name.Contains("manual") || name.Contains("guide") || name.Contains("handbook") || name.Contains("docs"))
            signals.Add(new DetectionSignal
            {
                Name = "technical_filename", Category = "filename", Weight = 5.0, VotedType = Technical,
                Reason = "Filename suggests technical"
            });

        if (name.Contains("complete-works") || name.Contains("collected") || name.Contains("anthology"))
            signals.Add(new DetectionSignal
            {
                Name = "anthology_filename", Category = "filename", Weight = 8.0, VotedType = Anthology,
                Reason = "Filename suggests anthology"
            });

        if (name.Contains("poems") || name.Contains("poetry") || name.Contains("verses") || name.Contains("sonnets"))
            signals.Add(new DetectionSignal
            {
                Name = "poetry_filename", Category = "filename", Weight = 8.0, VotedType = Collection,
                Reason = "Filename suggests poetry collection"
            });
        if (name.Contains("stories") || name.Contains("tales") || name.Contains("collection") ||
            name.Contains("essays"))
            signals.Add(new DetectionSignal
            {
                Name = "collection_filename", Category = "filename", Weight = 6.0, VotedType = Collection,
                Reason = "Filename suggests collection"
            });

        if (name.Contains("play") || name.Contains("drama") || name.Contains("tragedy") || name.Contains("comedy"))
            signals.Add(new DetectionSignal
            {
                Name = "play_filename", Category = "filename", Weight = 5.0, VotedType = Play,
                Reason = "Filename suggests dramatic work"
            });

        // Gutenberg-style naming: pg12345
        if (GutenbergIdRegex().IsMatch(name))
            signals.Add(new DetectionSignal
            {
                Name = "gutenberg_id", Category = "filename", Weight = 2.0, VotedType = Fiction,
                Reason = "Gutenberg eBook ID pattern"
            });
    }

    private static void CollectSizeSignals(int wordCount, long? fileSize, List<DetectionSignal> signals)
    {
        // Very long documents are more likely anthologies or novels
        if (wordCount > 200_000)
            signals.Add(new DetectionSignal
            {
                Name = "very_long", Category = "size", Weight = 3.0, VotedType = Anthology,
                Reason = $"{wordCount:N0} words — likely anthology or collected works"
            });
        else if (wordCount > 50_000)
            signals.Add(new DetectionSignal
            {
                Name = "book_length", Category = "size", Weight = 2.0, VotedType = Fiction,
                Reason = $"{wordCount:N0} words — novel length"
            });
        else if (wordCount is > 3_000 and < 15_000)
            signals.Add(new DetectionSignal
            {
                Name = "paper_length", Category = "size", Weight = 1.5, VotedType = Academic,
                Reason = $"{wordCount:N0} words — typical paper length"
            });
        else if (wordCount < 3_000)
            signals.Add(new DetectionSignal
            {
                Name = "short_doc", Category = "size", Weight = 1.0, VotedType = Technical,
                Reason = $"{wordCount:N0} words — short document"
            });

        // File size signals (PDFs with images tend to be larger)
        if (fileSize > 50 * 1024 * 1024) // > 50 MB
            signals.Add(new DetectionSignal
            {
                Name = "large_file", Category = "size", Weight = 1.5, VotedType = Technical,
                Reason = "Large file — likely image-heavy technical doc"
            });
    }

    private static void CollectMetadataSignals(Dictionary<string, string> metadata, List<DetectionSignal> signals)
    {
        if (metadata.TryGetValue("author", out var author) && !string.IsNullOrWhiteSpace(author))
            // Having an author is a weak signal for fiction/nonfiction
            signals.Add(new DetectionSignal
            {
                Name = "has_author", Category = "metadata", Weight = 1.0, VotedType = Fiction,
                Reason = $"Author: {author}"
            });

        if (metadata.TryGetValue("language", out var lang) &&
            lang.Contains("english", StringComparison.OrdinalIgnoreCase))
            // Gutenberg metadata presence signals fiction
            if (metadata.ContainsKey("ebook_number"))
                signals.Add(new DetectionSignal
                {
                    Name = "gutenberg_meta", Category = "metadata", Weight = 3.0, VotedType = Fiction,
                    Reason = "Gutenberg eBook metadata present"
                });
    }

    private static void CollectStructuralSignals(string earlyContent, List<DetectionSignal> signals)
    {
        var lower = earlyContent.ToLowerInvariant();

        // Chapter markers → fiction or nonfiction
        if (ChapterMarkerRegex().IsMatch(earlyContent))
            signals.Add(new DetectionSignal
            {
                Name = "chapter_markers", Category = "structural", Weight = 5.0, VotedType = Fiction,
                Reason = "Chapter markers found"
            });

        // Act/Scene markers → play
        if (ActSceneRegex().IsMatch(earlyContent))
            signals.Add(new DetectionSignal
            {
                Name = "act_scene_markers", Category = "structural", Weight = 8.0, VotedType = Play,
                Reason = "Act/Scene markers found"
            });

        // Part/Book markers → anthology or long novel
        if (PartBookRegex().IsMatch(earlyContent))
            signals.Add(new DetectionSignal
            {
                Name = "part_markers", Category = "structural", Weight = 3.0, VotedType = Anthology,
                Reason = "Part/Book division markers found"
            });

        // Academic structure
        if (lower.Contains("abstract") && (lower.Contains("introduction") || lower.Contains("methodology")))
            signals.Add(new DetectionSignal
            {
                Name = "academic_structure", Category = "structural", Weight = 8.0, VotedType = Academic,
                Reason = "Abstract + Introduction/Methodology structure"
            });

        // Citation markers [1], [2]
        if (CitationRegex().IsMatch(earlyContent))
            signals.Add(new DetectionSignal
            {
                Name = "citations", Category = "structural", Weight = 4.0, VotedType = Academic,
                Reason = "Bracketed citation references found"
            });

        // Code blocks
        if (lower.Contains("```"))
            signals.Add(new DetectionSignal
            {
                Name = "code_blocks", Category = "structural", Weight = 5.0, VotedType = Technical,
                Reason = "Code blocks found"
            });

        // Page markers (PDF origin)
        if (lower.Contains("<!-- page:"))
            signals.Add(new DetectionSignal
            {
                Name = "page_markers", Category = "structural", Weight = 1.0, VotedType = Fiction,
                Reason = "PDF page markers present"
            });
    }

    private static void CollectContentSignals(string earlyContent, List<DetectionSignal> signals)
    {
        var lower = earlyContent.ToLowerInvariant();

        // Dialogue patterns → fiction
        if (DialogueRegex().IsMatch(earlyContent))
            signals.Add(new DetectionSignal
            {
                Name = "dialogue", Category = "content", Weight = 5.0, VotedType = Fiction,
                Reason = "Dialogue patterns detected"
            });

        // Narrative pronouns with action verbs → fiction
        if (NarrativeRegex().IsMatch(lower))
            signals.Add(new DetectionSignal
            {
                Name = "narrative_voice", Category = "content", Weight = 3.0, VotedType = Fiction,
                Reason = "Narrative voice (he/she + action verbs)"
            });

        // Character honorifics → fiction
        if (HonorificsRegex().IsMatch(lower))
            signals.Add(new DetectionSignal
            {
                Name = "honorifics", Category = "content", Weight = 2.0, VotedType = Fiction,
                Reason = "Character honorifics (Mr./Mrs./Lord/etc.)"
            });

        // Stage directions → play
        if (StageDirectionRegex().IsMatch(earlyContent))
            signals.Add(new DetectionSignal
            {
                Name = "stage_directions", Category = "content", Weight = 6.0, VotedType = Play,
                Reason = "Stage directions found ([Enter...], [Exit...])"
            });

        // Speaker labels → play
        if (SpeakerLabelRegex().IsMatch(earlyContent))
            signals.Add(new DetectionSignal
            {
                Name = "speaker_labels", Category = "content", Weight = 5.0, VotedType = Play,
                Reason = "Speaker labels (CHARACTER_NAME. or CHARACTER_NAME:)"
            });

        // Academic keywords
        if (lower.Contains("methodology") || lower.Contains("literature review") || lower.Contains("hypothesis"))
            signals.Add(new DetectionSignal
            {
                Name = "academic_keywords", Category = "content", Weight = 3.0, VotedType = Academic,
                Reason = "Academic methodology keywords"
            });

        // Technical keywords
        if (lower.Contains("installation") || lower.Contains("getting started") || lower.Contains("configuration"))
            signals.Add(new DetectionSignal
            {
                Name = "technical_keywords", Category = "content", Weight = 3.0, VotedType = Technical,
                Reason = "Technical documentation keywords"
            });

        // ISBN / publisher → nonfiction
        if (lower.Contains("isbn") || lower.Contains("published by") || lower.Contains("copyright"))
            signals.Add(new DetectionSignal
            {
                Name = "publishing_meta", Category = "content", Weight = 3.0, VotedType = NonFiction,
                Reason = "Publishing metadata (ISBN, publisher)"
            });

        // Foreword/Preface → nonfiction
        if (lower.Contains("foreword") || lower.Contains("preface") || lower.Contains("acknowledgments"))
            signals.Add(new DetectionSignal
            {
                Name = "nonfiction_front", Category = "content", Weight = 3.0, VotedType = NonFiction,
                Reason = "Non-fiction front matter (foreword/preface)"
            });

        // Collection signals: poetry, short stories, essays
        // Poetry: short line lengths, many line breaks, rhyme-like patterns
        var lines = earlyContent.Split('\n');
        var shortLines = lines.Count(l => l.Trim().Length is > 3 and < 50);
        var totalNonEmpty = lines.Count(l => l.Trim().Length > 3);
        if (totalNonEmpty > 10 && (double)shortLines / totalNonEmpty > 0.6)
            signals.Add(new DetectionSignal
            {
                Name = "short_line_ratio", Category = "content", Weight = 4.0, VotedType = Collection,
                Reason = $"High ratio of short lines ({shortLines}/{totalNonEmpty}) — possible poetry"
            });

        // "Table of Contents" with many titled entries suggests collection
        if (lower.Contains("table of contents") || lower.Contains("contents"))
        {
            // Count potential title-like lines (short, capitalized) near the beginning
            var titleLikeCount = lines.Take(100)
                .Count(l => l.Trim().Length is > 3 and < 60
                            && l.Trim().Length > 0
                            && char.IsUpper(l.Trim()[0])
                            && !l.TrimStart().StartsWith('#'));
            if (titleLikeCount > 10)
                signals.Add(new DetectionSignal
                {
                    Name = "toc_many_entries", Category = "content", Weight = 4.0, VotedType = Collection,
                    Reason = $"Table of Contents with {titleLikeCount} entries"
                });
        }

        // "Sonnet" / "Poem" / "Stanza" keywords
        if (lower.Contains("sonnet") || lower.Contains("stanza") || lower.Contains("verse"))
            signals.Add(new DetectionSignal
            {
                Name = "poetry_keywords", Category = "content", Weight = 5.0, VotedType = Collection,
                Reason = "Poetry-specific keywords (sonnet/stanza/verse)"
            });
    }

    [GeneratedRegex(@"^Chapter\s+(\d+|[IVXLC]+|One|Two|Three|Four|Five|Six|Seven|Eight|Nine|Ten)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ChapterMarkerRegex();

    [GeneratedRegex(@"^ACT\s+[IVXLC\d]+", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ActSceneRegex();

    [GeneratedRegex(@"^(Part|Book|Volume)\s+(\d+|[IVXLC]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex PartBookRegex();

    [GeneratedRegex(@"\[\d+\]")]
    private static partial Regex CitationRegex();

    [GeneratedRegex(@"""[^""]{5,80}[,.]""\s*\w+\s+(said|asked|replied|whispered|exclaimed|shouted)")]
    private static partial Regex DialogueRegex();

    [GeneratedRegex(@"\b(he|she)\s+(walked|looked|said|thought|felt|turned|whispered|smiled|sighed|stood|sat)")]
    private static partial Regex NarrativeRegex();

    [GeneratedRegex(@"\b(mr\.|mrs\.|miss|sir|lady|lord|captain|colonel|professor|doctor)\s+[a-z]")]
    private static partial Regex HonorificsRegex();

    [GeneratedRegex(@"\[(Enter|Exit|Exeunt|Aside|Stage direction)[^\]]*\]", RegexOptions.IgnoreCase)]
    private static partial Regex StageDirectionRegex();

    [GeneratedRegex(@"^[A-Z]{2,}[\.\:]\s", RegexOptions.Multiline)]
    private static partial Regex SpeakerLabelRegex();

    [GeneratedRegex(@"^pg\d+")]
    private static partial Regex GutenbergIdRegex();
}