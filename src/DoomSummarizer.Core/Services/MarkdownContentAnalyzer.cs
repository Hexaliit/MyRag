using System.Text.RegularExpressions;
using ReverseMarkdown;

namespace DoomSummarizer.Services;

/// <summary>
/// Converts HTML to Markdown and analyzes structural signals for content quality scoring.
/// Uses Markdown structure (headings, lists, code blocks, tables, paragraphs) as proxy
/// signals for content type and quality — cheaper than ML, more precise than raw text length.
/// </summary>
public static partial class MarkdownContentAnalyzer
{
    private static readonly Converter MdConverter = new(new Config
    {
        GithubFlavored = true,
        RemoveComments = true,
        SmartHrefHandling = true,
        UnknownTags = Config.UnknownTagsOption.Bypass,
        TableWithoutHeaderRowHandling = Config.TableWithoutHeaderRowHandlingOption.Default,
    });

    /// <summary>
    /// Convert HTML to clean Markdown, preserving structural elements.
    /// </summary>
    public static string HtmlToMarkdown(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        try
        {
            var md = MdConverter.Convert(html);
            // Collapse excessive blank lines (3+ → 2)
            md = ExcessiveNewlines().Replace(md, "\n\n");
            return md.Trim();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Analyze Markdown structure and return content quality signals.
    /// </summary>
    public static ContentStructure Analyze(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return new ContentStructure();

        var lines = markdown.Split('\n');
        var structure = new ContentStructure();

        var currentParagraph = new List<string>();
        var inCodeBlock = false;
        var codeBlockLines = 0;
        var inTable = false;
        var tableRows = 0;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            // Code blocks (fenced)
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                if (inCodeBlock)
                {
                    // End code block
                    structure.CodeBlocks++;
                    structure.CodeBlockLines += codeBlockLines;
                    codeBlockLines = 0;
                    inCodeBlock = false;
                }
                else
                {
                    FlushParagraph(currentParagraph, structure);
                    inCodeBlock = true;
                }
                continue;
            }

            if (inCodeBlock)
            {
                codeBlockLines++;
                continue;
            }

            // Tables (GFM: lines with |)
            if (trimmed.StartsWith('|') && trimmed.EndsWith('|'))
            {
                if (!inTable)
                {
                    FlushParagraph(currentParagraph, structure);
                    inTable = true;
                    tableRows = 0;
                }
                // Skip separator rows (|---|---|)
                if (!TableSeparator().IsMatch(trimmed))
                    tableRows++;
                continue;
            }
            if (inTable)
            {
                structure.Tables++;
                structure.TableRows += tableRows;
                inTable = false;
                tableRows = 0;
            }

            // Headings
            var headingMatch = HeadingPattern().Match(trimmed);
            if (headingMatch.Success)
            {
                FlushParagraph(currentParagraph, structure);
                var level = headingMatch.Groups[1].Value.Length;
                structure.Headings++;
                structure.HeadingLevels.Add(level);
                structure.HeadingTexts.Add(headingMatch.Groups[2].Value.Trim());
                continue;
            }

            // Blockquotes
            if (trimmed.StartsWith('>'))
            {
                FlushParagraph(currentParagraph, structure);
                structure.Blockquotes++;
                structure.BlockquoteChars += trimmed.TrimStart('>').Trim().Length;
                continue;
            }

            // Unordered list items
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ "))
            {
                FlushParagraph(currentParagraph, structure);
                structure.ListItems++;
                structure.ListItemChars += trimmed[2..].Trim().Length;
                continue;
            }

            // Ordered list items
            if (OrderedListItem().IsMatch(trimmed))
            {
                FlushParagraph(currentParagraph, structure);
                structure.ListItems++;
                var textStart = trimmed.IndexOf(' ') + 1;
                structure.ListItemChars += trimmed[textStart..].Trim().Length;
                continue;
            }

            // Horizontal rules
            if (trimmed is "---" or "***" or "___")
            {
                FlushParagraph(currentParagraph, structure);
                continue;
            }

            // Links (count inline)
            var linkMatches = InlineLink().Matches(trimmed);
            structure.Links += linkMatches.Count;

            // Images
            var imageMatches = InlineImage().Matches(trimmed);
            structure.Images += imageMatches.Count;

            // Empty line = paragraph break
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                FlushParagraph(currentParagraph, structure);
                continue;
            }

            // Accumulate paragraph text
            currentParagraph.Add(trimmed);
        }

        // Flush final state
        FlushParagraph(currentParagraph, structure);
        if (inCodeBlock)
        {
            structure.CodeBlocks++;
            structure.CodeBlockLines += codeBlockLines;
        }
        if (inTable)
        {
            structure.Tables++;
            structure.TableRows += tableRows;
        }

        // Compute derived metrics
        structure.TotalChars = markdown.Length;
        structure.TotalLines = lines.Length;

        // Research-backed text quality signals
        ComputeTextQualitySignals(markdown, structure);

        structure.ComputeQualityScore();

        return structure;
    }

    /// <summary>
    /// Quick extraction: convert HTML to Markdown, analyze structure, return
    /// the structured Markdown content (preserving headings, lists, etc.)
    /// plus quality signals.
    /// </summary>
    public static (string markdown, ContentStructure structure) ExtractStructured(string html)
    {
        var md = HtmlToMarkdown(html);
        var structure = Analyze(md);
        return (md, structure);
    }

    private static void FlushParagraph(List<string> lines, ContentStructure structure)
    {
        if (lines.Count == 0) return;

        var text = string.Join(" ", lines);
        structure.Paragraphs++;
        structure.ParagraphChars += text.Length;
        structure.ParagraphLengths.Add(text.Length);

        // Count sentences ending with terminal punctuation
        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length > 0 && trimmed[^1] is '.' or '!' or '?')
                structure.CompleteSentences++;
            structure.TotalSentenceLines++;
        }

        lines.Clear();
    }

    // Common English stop words for density calculation
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "but", "in", "on", "at", "to", "for",
        "of", "with", "by", "from", "is", "are", "was", "were", "be", "been",
        "being", "have", "has", "had", "do", "does", "did", "will", "would",
        "could", "should", "may", "might", "can", "shall", "not", "no",
        "it", "its", "this", "that", "these", "those", "he", "she", "they",
        "we", "you", "i", "me", "my", "your", "his", "her", "our", "their",
        "as", "if", "so", "than", "then", "also", "more", "most", "some",
        "any", "all", "each", "every", "both", "few", "many", "much",
        "about", "up", "out", "into", "over", "after", "before", "between",
        "under", "such", "very", "just", "only", "when", "where", "how",
        "what", "which", "who", "whom", "why", "because", "while"
    };

    /// <summary>
    /// Compute text quality signals: stop word density, sentence completeness,
    /// paragraph CV, and duplicate line ratio.
    /// Called after structural analysis is complete.
    /// </summary>
    internal static void ComputeTextQualitySignals(string markdown, ContentStructure structure)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return;

        var lines = markdown.Split('\n');

        // Stop word density: split all prose text into words, count stop words
        var words = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            // Skip markdown-specific lines (headings, code fences, tables, list markers)
            if (trimmed.StartsWith('#') || trimmed.StartsWith("```") || trimmed.StartsWith("~~~")
                || (trimmed.StartsWith('|') && trimmed.EndsWith('|'))
                || trimmed.StartsWith('>'))
                continue;

            var lineWords = WordSplitter().Matches(trimmed);
            foreach (Match w in lineWords)
                words.Add(w.Value);
        }

        if (words.Count > 0)
        {
            var stopCount = words.Count(w => StopWords.Contains(w));
            structure.StopWordDensity = (float)stopCount / words.Count;
            structure.WordCount = words.Count;
        }

        // Paragraph length coefficient of variation
        if (structure.ParagraphLengths.Count >= 3)
        {
            var avg = structure.ParagraphLengths.Average();
            if (avg > 0)
            {
                var stdDev = Math.Sqrt(structure.ParagraphLengths.Average(l => Math.Pow(l - avg, 2)));
                structure.ParagraphLengthCV = (float)(stdDev / avg);
            }
        }

        // Duplicate line ratio
        var nonEmptyLines = lines
            .Select(l => l.Trim())
            .Where(l => l.Length > 10) // Skip very short lines
            .ToList();

        if (nonEmptyLines.Count > 0)
        {
            var uniqueLines = new HashSet<string>(nonEmptyLines, StringComparer.OrdinalIgnoreCase);
            structure.DuplicateLineRatio = 1f - (float)uniqueLines.Count / nonEmptyLines.Count;
        }

        // Sentence completeness ratio
        if (structure.TotalSentenceLines > 0)
        {
            structure.SentenceCompleteness = (float)structure.CompleteSentences / structure.TotalSentenceLines;
        }
    }

    [GeneratedRegex(@"\b[\w'-]+\b")]
    private static partial Regex WordSplitter();

    // Compiled regex patterns for performance
    [GeneratedRegex(@"\n{3,}", RegexOptions.Compiled)]
    private static partial Regex ExcessiveNewlines();

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^\|[\s\-:|]+\|$")]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"^\d+\.\s")]
    private static partial Regex OrderedListItem();

    [GeneratedRegex(@"\[([^\]]*)\]\(([^)]+)\)")]
    private static partial Regex InlineLink();

    [GeneratedRegex(@"!\[([^\]]*)\]\(([^)]+)\)")]
    private static partial Regex InlineImage();
}

/// <summary>
/// Structural analysis of Markdown content.
/// Captures document structure signals for content quality assessment.
/// </summary>
public class ContentStructure
{
    // Raw counts
    public int TotalChars { get; set; }
    public int TotalLines { get; set; }
    public int Headings { get; set; }
    public List<int> HeadingLevels { get; set; } = [];
    public List<string> HeadingTexts { get; set; } = [];
    public int Paragraphs { get; set; }
    public int ParagraphChars { get; set; }
    public List<int> ParagraphLengths { get; set; } = [];
    public int ListItems { get; set; }
    public int ListItemChars { get; set; }
    public int CodeBlocks { get; set; }
    public int CodeBlockLines { get; set; }
    public int Tables { get; set; }
    public int TableRows { get; set; }
    public int Blockquotes { get; set; }
    public int BlockquoteChars { get; set; }
    public int Links { get; set; }
    public int Images { get; set; }

    // Text quality signals (research-backed)
    public float StopWordDensity { get; set; }    // 0.35-0.50 = natural prose
    public float SentenceCompleteness { get; set; } // Ratio of lines ending with .!?
    public float ParagraphLengthCV { get; set; }   // Coefficient of variation (0 = uniform, >1 = varied)
    public float DuplicateLineRatio { get; set; }  // 0 = all unique, 1 = all duplicates
    public int CompleteSentences { get; set; }     // Internal counter
    public int TotalSentenceLines { get; set; }    // Internal counter
    public int WordCount { get; set; }

    // Derived quality metrics
    public double QualityScore { get; set; }
    public string ContentType { get; set; } = "unknown";

    /// <summary>
    /// Compute an overall content quality score (0-1) and classify content type.
    /// </summary>
    public void ComputeQualityScore()
    {
        if (TotalChars == 0)
        {
            QualityScore = 0;
            ContentType = "empty";
            return;
        }

        var score = 0.0;

        // Paragraph quality: long paragraphs = prose/article content
        if (Paragraphs > 0)
        {
            var avgParagraphLen = ParagraphChars / (double)Paragraphs;
            // Ideal article paragraph: 200-800 chars
            score += avgParagraphLen switch
            {
                > 800 => 0.15, // Wall of text (okay but not structured)
                > 200 => 0.25, // Well-formed paragraphs
                > 80 => 0.15,  // Short paragraphs (news style)
                _ => 0.05      // Very short (likely navigation/captions)
            };

            // Multiple paragraphs = article structure
            score += Math.Min(Paragraphs / 10.0, 0.15);
        }

        // Heading structure: organized content
        if (Headings > 0)
        {
            score += 0.10; // Has headings
            if (HeadingLevels.Distinct().Count() > 1)
                score += 0.05; // Heading hierarchy
        }

        // List items: structured information (good for facts, key points)
        if (ListItems > 0)
        {
            score += Math.Min(ListItems / 20.0, 0.10);
        }

        // Code blocks: technical content
        if (CodeBlocks > 0)
        {
            score += 0.05;
        }

        // Tables: data-rich content
        if (Tables > 0)
        {
            score += 0.10;
        }

        // Blockquotes: citations/quotes (evidence)
        if (Blockquotes > 0)
        {
            score += Math.Min(Blockquotes / 5.0, 0.05);
        }

        // Link density penalty: high link-to-text ratio = navigation
        if (TotalChars > 0 && Links > 0)
        {
            var linkDensity = Links / (TotalChars / 100.0);
            if (linkDensity > 2.0)
                score -= 0.15; // Very link-heavy = likely navigation
        }

        // Content volume bonus
        if (ParagraphChars > 1000)
            score += 0.10;

        // Research-backed text quality signals

        // Stop word density: natural English prose is ~35-50% stop words.
        // Very low (<20%) = technical jargon/lists. Very high (>60%) = filler/repetitive.
        // (Kohlschütter et al. 2010, jusText, RefinedWeb)
        if (StopWordDensity is > 0.25f and < 0.55f)
            score += 0.05; // Natural prose range
        else if (StopWordDensity is > 0.10f and < 0.65f)
            score += 0.02; // Acceptable range
        // else: no bonus for extreme densities

        // Sentence completeness: well-formed text ends sentences with punctuation.
        // High completeness = prose/articles. Low = fragments/lists/navigation.
        if (SentenceCompleteness > 0.6f)
            score += 0.05;
        else if (SentenceCompleteness > 0.3f)
            score += 0.02;

        // Paragraph length CV: natural writing varies paragraph length (CV 0.3-1.5).
        // Very uniform (CV < 0.1) suggests machine-generated or template content.
        // Very high (CV > 2.0) suggests mixed content types.
        if (ParagraphLengthCV is > 0.2f and < 1.5f)
            score += 0.05; // Natural variation
        else if (ParagraphLengthCV is > 0f and < 0.2f)
            score -= 0.05; // Suspiciously uniform

        // Duplicate line penalty: repeated lines indicate boilerplate/template content.
        if (DuplicateLineRatio > 0.3f)
            score -= 0.10; // Significant duplication
        else if (DuplicateLineRatio > 0.1f)
            score -= 0.03; // Some duplication

        QualityScore = Math.Clamp(score, 0, 1);

        // Classify content type
        ContentType = ClassifyContentType();
    }

    private string ClassifyContentType()
    {
        var proseRatio = ParagraphChars / (double)Math.Max(TotalChars, 1);
        var listRatio = ListItemChars / (double)Math.Max(TotalChars, 1);
        var codeRatio = CodeBlockLines / (double)Math.Max(TotalLines, 1);

        if (codeRatio > 0.3)
            return "technical";
        if (Tables > 0 && TableRows > 5)
            return "data";
        if (listRatio > 0.4)
            return "structured";
        if (proseRatio > 0.6 && Paragraphs >= 3)
            return "article";
        if (proseRatio > 0.4)
            return "content";
        if (Links > 10 && proseRatio < 0.3)
            return "navigation";

        return "mixed";
    }

    /// <summary>
    /// Short structural summary for debug/display.
    /// </summary>
    public string ToSummary()
    {
        var parts = new List<string>();
        if (Paragraphs > 0) parts.Add($"{Paragraphs}p");
        if (Headings > 0) parts.Add($"{Headings}h");
        if (ListItems > 0) parts.Add($"{ListItems}li");
        if (CodeBlocks > 0) parts.Add($"{CodeBlocks}code");
        if (Tables > 0) parts.Add($"{Tables}tbl");
        if (Blockquotes > 0) parts.Add($"{Blockquotes}bq");
        if (Links > 0) parts.Add($"{Links}links");

        var quality = $"q={QualityScore:F2}";
        if (StopWordDensity > 0)
            quality += $" sw={StopWordDensity:P0}";
        if (SentenceCompleteness > 0)
            quality += $" sc={SentenceCompleteness:P0}";

        return $"[{ContentType}] {string.Join(" ", parts)} {quality}";
    }
}
