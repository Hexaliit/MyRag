using System.Text;
using System.Text.RegularExpressions;
using Mostlylucid.DocSummarizer.Models;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
///     Hints for narrative/structure-aware chunk boundary detection.
///     Passed to DocumentChunker to respect document-type-specific boundaries.
/// </summary>
public record ChunkBoundaryHints
{
    /// <summary>Split at scene break markers (*** / * * * / --- / ___).</summary>
    public bool RespectSceneBreaks { get; init; }

    /// <summary>Extend chunk target by 1.5× to keep dialogue exchanges together.</summary>
    public bool KeepDialogueTogether { get; init; }

    /// <summary>Treat "Chapter N" text as level-1 heading for splitting.</summary>
    public bool RespectChapterBoundaries { get; init; }

    /// <summary>Extend chunk target up to 2× to keep code fences intact.</summary>
    public bool KeepCodeBlocksTogether { get; init; }

    /// <summary>Overlap fraction (0-0.20). Repeat last N tokens as prefix of next chunk.</summary>
    public float OverlapFraction { get; init; }

    /// <summary>If set, prepend this heading as context prefix to each chunk's content.</summary>
    public string? ContextualHeaderPrefix { get; init; }
}

public class DocumentChunker
{
    // Rough estimate: 1 token ≈ 4 characters for English text
    private const int CharsPerToken = 4;

    // Regex to extract page markers: <!-- PAGE:1-5 --> or <!-- PAGE:1 -->
    private static readonly Regex PageMarkerRegex = new(@"<!--\s*PAGE:(\d+)(?:-(\d+))?\s*-->", RegexOptions.Compiled);

    // Scene break patterns: ***, * * *, ---, ___, ===, or similar
    private static readonly Regex SceneBreakRegex = new(
        @"^\s*(?:\*\s*\*\s*\*|\*{3,}|-{3,}|_{3,}|={3,})\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // Chapter heading patterns: "Chapter 1", "CHAPTER ONE", "Chapter I", etc.
    private static readonly Regex ChapterRegex = new(
        @"^\s*(?:chapter|part)\s+(?:\d+|[ivxlcdm]+|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ChunkBoundaryHints? _hints;
    private readonly int _maxHeadingLevel;
    private readonly int _minChunkTokens;
    private readonly int _targetChunkTokens;

    /// <summary>
    ///     Creates a new document chunker.
    /// </summary>
    /// <param name="maxHeadingLevel">Maximum heading level to split on (1-6). Default is 4.</param>
    /// <param name="targetChunkTokens">
    ///     Target chunk size in tokens. Default is 400 (~1.6KB).
    ///     Optimized for RAG retrieval - smaller chunks improve precision.
    /// </param>
    /// <param name="minChunkTokens">Minimum chunk size before merging. Default is 50 (~200 bytes).</param>
    /// <param name="hints">Optional boundary hints for narrative/structure-aware chunking.</param>
    public DocumentChunker(int maxHeadingLevel = 4, int targetChunkTokens = 400, int minChunkTokens = 50,
        ChunkBoundaryHints? hints = null)
    {
        _maxHeadingLevel = Math.Clamp(maxHeadingLevel, 1, 6);
        _targetChunkTokens = targetChunkTokens;
        _minChunkTokens = minChunkTokens;
        _hints = hints;
    }

    /// <summary>
    ///     Check if a line is a scene break marker (*** / * * * / --- / ___ / ===).
    /// </summary>
    public static bool IsSceneBreak(string line) =>
        SceneBreakRegex.IsMatch(line);

    /// <summary>
    ///     Check if a text block is dialogue-heavy (>50% of non-empty lines start with " or contain speech attribution).
    /// </summary>
    public static bool IsDialogueSection(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return false;

        var dialogueLines = 0;
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0) continue;
            // Starts with quote or contains speech attribution
            if (trimmed[0] == '"' || trimmed[0] == '\u201C' ||
                Regex.IsMatch(trimmed, @"""[^""]*""\s*\w+\s+(said|asked|replied|whispered|exclaimed|cried|murmured|shouted|answered|called|declared)",
                    RegexOptions.IgnoreCase))
                dialogueLines++;
        }

        return lines.Length > 0 && (float)dialogueLines / lines.Length > 0.5f;
    }

    public List<DocumentChunk> ChunkByStructure(string markdown)
    {
        // Extract page markers before processing
        var pageMap = ExtractPageMarkers(markdown);

        // Determine if document has markdown headings (ignore markers for detection)
        var hasHeadings = HasMarkdownHeadings(PageMarkerRegex.Replace(markdown, ""));

        // First pass: split by structure (headings) or paragraphs for plain text
        var rawSections = hasHeadings
            ? SplitByHeadings(markdown)
            : SplitByParagraphs(markdown);

        // Second pass: merge small sections to approach target size
        var mergedSections = MergeSections(rawSections);


        // Convert to chunks with page info
        var chunks = new List<DocumentChunk>();
        var index = 0;
        var totalSections = mergedSections.Count;

        foreach (var section in mergedSections)
        {
            if (string.IsNullOrWhiteSpace(section.Content)) continue;

            // Try to find page info for this section
            var (pageStart, pageEnd) = GetPageInfoForSection(section, pageMap, index, totalSections);

            chunks.Add(new DocumentChunk(
                index++,
                section.Heading,
                section.Level,
                section.Content,
                HashHelper.ComputeHash(section.Content),
                pageStart,
                pageEnd));
        }

        return chunks;
    }

    /// <summary>
    ///     Extract page markers from markdown into a position-to-page map
    /// </summary>
    private static Dictionary<int, (int start, int end)> ExtractPageMarkers(string markdown)
    {
        var map = new Dictionary<int, (int start, int end)>();
        var matches = PageMarkerRegex.Matches(markdown);

        foreach (Match match in matches)
        {
            var position = match.Index;
            var startPage = int.Parse(match.Groups[1].Value);
            var endPage = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : startPage;
            map[position] = (startPage, endPage);
        }

        return map;
    }

    /// <summary>
    ///     Get page info for a section - uses section's page info if available,
    ///     falls back to pageMap estimation, or returns null for estimation by caller
    /// </summary>
    private static (int? pageStart, int? pageEnd) GetPageInfoForSection(
        RawSection section,
        Dictionary<int, (int start, int end)> pageMap,
        int sectionIndex,
        int totalSections)
    {
        // If section already has page info (from marker parsing), use it
        if (section.PageStart.HasValue)
            return (section.PageStart, section.PageEnd ?? section.PageStart);

        // If we have page markers, estimate based on section index distribution
        if (pageMap.Count > 0)
        {
            var sortedMarkers = pageMap.OrderBy(kv => kv.Key).ToList();
            var totalPages = sortedMarkers.Max(m => m.Value.end);

            // Estimate page based on section's position in document
            var estimatedPage = totalSections > 0
                ? (int)Math.Ceiling((double)(sectionIndex + 1) / totalSections * totalPages)
                : 1;

            return (Math.Max(1, estimatedPage), Math.Max(1, estimatedPage));
        }

        // No page markers - return null (caller will estimate or use section number)
        return (null, null);
    }

    /// <summary>
    ///     Check if document contains markdown headings
    /// </summary>
    private bool HasMarkdownHeadings(string text)
    {
        var lines = text.Split('\n');
        foreach (var line in lines)
        {
            var level = GetHeadingLevel(line);
            if (level > 0 && level <= _maxHeadingLevel)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Split plain text by paragraphs (double newlines).
    ///     When RespectSceneBreaks is set, scene break markers (*** / --- / ___) act as hard boundaries.
    ///     When RespectChapterBoundaries is set, "Chapter N" lines act as level-1 headings.
    /// </summary>
    private List<RawSection> SplitByParagraphs(string text)
    {
        var sections = new List<RawSection>();

        // Split on double newlines (blank lines) - handles \n\n and \r\n\r\n
        var paragraphs = Regex
            .Split(text, @"\r?\n\s*\r?\n")
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        if (paragraphs.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(text))
                sections.Add(new RawSection("Document", 1, text.Trim()));
            return sections;
        }

        int? currentPageStart = null;
        int? currentPageEnd = null;
        var paragraphIndex = 0;

        foreach (var rawPara in paragraphs)
        {
            var para = rawPara;
            var match = PageMarkerRegex.Match(para);
            if (match.Success)
            {
                currentPageStart = int.Parse(match.Groups[1].Value);
                currentPageEnd = match.Groups[2].Success
                    ? int.Parse(match.Groups[2].Value)
                    : currentPageStart;

                para = PageMarkerRegex.Replace(para, "").Trim();
                if (string.IsNullOrWhiteSpace(para))
                    continue;
            }

            // Scene break detection: treat as section boundary marker (empty content, forces flush)
            if (_hints?.RespectSceneBreaks == true && IsSceneBreak(para))
            {
                paragraphIndex++;
                sections.Add(new RawSection($"Scene {paragraphIndex}", 1, "",
                    currentPageStart, currentPageEnd));
                continue;
            }

            // Chapter boundary in plain text: promote to level-1 heading
            if (_hints?.RespectChapterBoundaries == true && ChapterRegex.IsMatch(para))
            {
                paragraphIndex++;
                // Extract chapter title from the line
                var chapterTitle = para.Split('\n', 2)[0].Trim();
                var chapterContent = para.Contains('\n') ? para[(para.IndexOf('\n') + 1)..].Trim() : "";
                sections.Add(new RawSection(chapterTitle, 1, chapterContent,
                    currentPageStart, currentPageEnd));
                continue;
            }

            paragraphIndex++;
            var heading = $"Paragraph {paragraphIndex}";

            var firstSentenceEnd = para.IndexOfAny(['.', '!', '?']);
            if (firstSentenceEnd > 0 && firstSentenceEnd < 100)
            {
                heading = para[..(firstSentenceEnd + 1)];
            }
            else if (para.Length < 100)
            {
                heading = para;
            }
            else
            {
                var cutoff = para.LastIndexOf(' ', Math.Min(80, para.Length - 1));
                if (cutoff < 20) cutoff = 80;
                heading = para[..Math.Min(cutoff, para.Length)] + "...";
            }

            sections.Add(new RawSection(heading, 1, para, currentPageStart, currentPageEnd));
        }

        return sections;
    }

    private List<RawSection> SplitByHeadings(string markdown)
    {
        var sections = new List<RawSection>();
        var lines = markdown.Split('\n');

        var content = new StringBuilder();
        string? heading = null;
        var level = 0;
        int? currentPageStart = null;
        int? currentPageEnd = null;

        foreach (var line in lines)
        {
            // Check for page marker
            var pageMatch = PageMarkerRegex.Match(line);
            if (pageMatch.Success)
            {
                currentPageStart = int.Parse(pageMatch.Groups[1].Value);
                currentPageEnd = pageMatch.Groups[2].Success
                    ? int.Parse(pageMatch.Groups[2].Value)
                    : currentPageStart;
                continue; // Don't include marker in content
            }

            var headingLevel = GetHeadingLevel(line);

            // Chapter boundary detection: treat "Chapter N" lines as level-1 headings
            if (headingLevel == 0 && _hints?.RespectChapterBoundaries == true && ChapterRegex.IsMatch(line))
            {
                headingLevel = 1;
            }

            // Only split on headings up to the configured max level
            if (headingLevel > 0 && headingLevel <= _maxHeadingLevel)
            {
                // Flush previous section with current page info
                if (content.Length > 0 || heading != null)
                {
                    sections.Add(new RawSection(heading ?? "", level, content.ToString().Trim(), currentPageStart,
                        currentPageEnd));
                    content.Clear();
                }

                heading = line.TrimStart('#', ' ');
                level = headingLevel;
            }
            else
            {
                content.AppendLine(line);
            }
        }

        // Flush final section
        if (content.Length > 0 || heading != null)
            sections.Add(new RawSection(heading ?? "", level, content.ToString().Trim(), currentPageStart,
                currentPageEnd));

        return sections;
    }

    private List<RawSection> MergeSections(List<RawSection> sections)
    {
        if (sections.Count <= 1)
            return sections;

        var merged = new List<RawSection>();
        var currentHeading = "";
        var currentLevel = 0;
        var currentContent = new StringBuilder();
        var currentTokens = 0;
        int? currentPageStart = null;
        int? currentPageEnd = null;
        string? lastFlushedTail = null; // For overlap support

        void FlushCurrent()
        {
            if (currentContent.Length == 0) return;
            var content = currentContent.ToString().Trim();

            // Capture tail for overlap before flushing
            if (_hints?.OverlapFraction > 0 && content.Length > 0)
            {
                var overlapChars = Math.Max(20, (int)(_targetChunkTokens * CharsPerToken * _hints.OverlapFraction));
                overlapChars = Math.Min(overlapChars, content.Length);
                lastFlushedTail = content[^overlapChars..];
            }

            merged.Add(new RawSection(
                currentHeading,
                currentLevel,
                content,
                currentPageStart,
                currentPageEnd));
            currentContent.Clear();
            currentTokens = 0;
            currentHeading = "";
            currentLevel = 0;
            currentPageStart = null;
            currentPageEnd = null;
        }

        void StartNewChunk(RawSection section, int sectionTokens)
        {
            currentHeading = section.Heading;
            currentLevel = section.Level;

            // Prepend overlap from previous chunk
            if (lastFlushedTail != null)
            {
                currentContent.AppendLine(lastFlushedTail);
                currentTokens = EstimateTokens(lastFlushedTail);
                lastFlushedTail = null;
            }

            currentContent.AppendLine(section.Content);
            currentTokens += sectionTokens;
            currentPageStart = section.PageStart;
            currentPageEnd = section.PageEnd ?? section.PageStart;
        }

        void AppendSection(RawSection section, int tokens)
        {
            if (currentContent.Length > 0) currentContent.AppendLine();
            if (!string.IsNullOrEmpty(section.Heading))
            {
                currentContent.AppendLine($"## {section.Heading}");
                currentContent.AppendLine();
            }

            currentContent.AppendLine(section.Content);
            currentTokens += tokens;

            if (section.PageStart.HasValue)
            {
                currentPageStart = currentPageStart.HasValue
                    ? Math.Min(currentPageStart.Value, section.PageStart.Value)
                    : section.PageStart;
                var sectionEnd = (section.PageEnd ?? section.PageStart)!.Value;
                currentPageEnd = currentPageEnd.HasValue
                    ? Math.Max(currentPageEnd.Value, sectionEnd)
                    : sectionEnd;
            }
        }

        for (var idx = 0; idx < sections.Count; idx++)
        {
            var section = sections[idx];
            var sectionTokens = EstimateTokens(section.Content);
            var sectionWithHeading = string.IsNullOrEmpty(section.Heading)
                ? section.Content
                : $"## {section.Heading}\n\n{section.Content}";
            var fullSectionTokens = EstimateTokens(sectionWithHeading);

            // Scene break (empty content) → hard flush boundary
            if (_hints?.RespectSceneBreaks == true &&
                string.IsNullOrWhiteSpace(section.Content) &&
                section.Heading.StartsWith("Scene", StringComparison.Ordinal))
            {
                FlushCurrent();
                continue;
            }

            // Compute effective target: may be extended for dialogue or code blocks
            var effectiveTarget = _targetChunkTokens;
            if (_hints?.KeepDialogueTogether == true &&
                IsDialogueSection(currentContent.ToString()))
            {
                // If next section is also dialogue, extend target by 1.5×
                if (IsDialogueSection(section.Content))
                    effectiveTarget = (int)(_targetChunkTokens * 1.5);
                // If next is NOT dialogue but current IS, flush cleanly at normal target
            }

            if (_hints?.KeepCodeBlocksTogether == true &&
                section.Content.Contains("```"))
            {
                // Extend target up to 2× to keep code block intact
                effectiveTarget = Math.Max(effectiveTarget, _targetChunkTokens * 2);
            }

            // If current buffer is empty, start a new chunk
            if (currentContent.Length == 0)
            {
                StartNewChunk(section, sectionTokens);
                continue;
            }

            // Would adding this section exceed the effective target?
            if (currentTokens + fullSectionTokens > effectiveTarget)
            {
                // Always flush if current buffer has meaningful content
                // Only merge tiny fragments (< minChunkTokens) with next section
                if (currentTokens >= _minChunkTokens)
                {
                    FlushCurrent();
                    StartNewChunk(section, sectionTokens);
                }
                else
                {
                    // Current is tiny - merge this section then flush
                    AppendSection(section, fullSectionTokens);
                    FlushCurrent();
                }
            }
            else
            {
                // Under target - keep merging
                AppendSection(section, fullSectionTokens);
            }
        }

        FlushCurrent();
        return merged;
    }

    private int EstimateTokens(string text)

    {
        if (string.IsNullOrEmpty(text)) return 0;
        return text.Length / CharsPerToken;
    }

    private static int GetHeadingLevel(string line)
    {
        if (string.IsNullOrEmpty(line) || !line.StartsWith('#'))
            return 0;

        var level = 0;
        foreach (var c in line)
            if (c == '#') level++;
            else break;

        // Must have space after # marks to be a valid heading
        return line.Length > level && line[level] == ' ' ? level : 0;
    }

    private record RawSection(string Heading, int Level, string Content, int? PageStart = null, int? PageEnd = null);
}