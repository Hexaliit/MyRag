using System.Diagnostics;
using System.Text.RegularExpressions;
using DoomWriter.Models;
using WeCantSpell.Hunspell;

namespace DoomWriter.Services;

/// <summary>
///     Hunspell-based spell checking with custom dictionary support.
///     Uses WeCantSpell.Hunspell (pure managed .NET).
///     Dictionary files stored at %APPDATA%/DoomWriter/dictionaries/
/// </summary>
public partial class SpellCheckService : IDisposable
{
    private readonly string _customDictPath;
    private readonly HashSet<string> _customWords = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _dictDir;
    private readonly WriterSettingsService _settings;
    private WordList? _dictionary;

    public SpellCheckService(WriterSettingsService settings)
    {
        _settings = settings;
        _dictDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DoomWriter", "dictionaries");
        _customDictPath = Path.Combine(_dictDir, "custom.txt");
        Directory.CreateDirectory(_dictDir);
    }

    public bool IsLoaded { get; private set; }

    public string ActiveLanguage { get; private set; } = "en_US";

    public void Dispose()
    {
        // WordList doesn't implement IDisposable but we clean up custom resources
        _customWords.Clear();
    }

    /// <summary>
    ///     Initialize the spell checker by loading the Hunspell dictionary.
    /// </summary>
    public async Task InitializeAsync(string language = "en_US")
    {
        ActiveLanguage = language;

        var affPath = Path.Combine(_dictDir, $"{language}.aff");
        var dicPath = Path.Combine(_dictDir, $"{language}.dic");

        if (!File.Exists(affPath) || !File.Exists(dicPath))
        {
            Debug.WriteLine(
                $"Dictionary files not found for {language}. " +
                $"Place {language}.aff and {language}.dic in {_dictDir}");
            return;
        }

        try
        {
            _dictionary = WordList.CreateFromFiles(dicPath, affPath);
            IsLoaded = true;

            // Load custom dictionary
            await LoadCustomDictionaryAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load dictionary: {ex.Message}");
        }
    }

    /// <summary>
    ///     Check if a word is spelled correctly.
    /// </summary>
    public bool CheckWord(string word)
    {
        if (!IsLoaded || _dictionary == null) return true; // Assume correct if no dictionary
        if (string.IsNullOrWhiteSpace(word)) return true;
        if (word.Length < 2) return true;

        // Skip words that look like code, URLs, numbers
        if (IsCodeOrSpecial(word)) return true;

        // Check custom dictionary first
        if (_customWords.Contains(word)) return true;

        return _dictionary.Check(word);
    }

    /// <summary>
    ///     Get spelling suggestions for a misspelled word.
    /// </summary>
    public List<string> Suggest(string word, int maxSuggestions = 5)
    {
        if (!IsLoaded || _dictionary == null) return [];
        if (string.IsNullOrWhiteSpace(word)) return [];

        return _dictionary.Suggest(word)
            .Take(maxSuggestions)
            .ToList();
    }

    /// <summary>
    ///     Add a word to the custom dictionary.
    /// </summary>
    public async Task AddToCustomDictionaryAsync(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return;

        _customWords.Add(word.Trim());
        await SaveCustomDictionaryAsync();
    }

    /// <summary>
    ///     Remove a word from the custom dictionary.
    /// </summary>
    public async Task RemoveFromCustomDictionaryAsync(string word)
    {
        if (_customWords.Remove(word.Trim()))
            await SaveCustomDictionaryAsync();
    }

    /// <summary>
    ///     Add entity names from document analysis to the custom word list
    ///     so they aren't flagged as misspelled.
    /// </summary>
    public void AddEntityNames(IEnumerable<string> entityNames)
    {
        foreach (var name in entityNames)
        {
            // Add both the full entity name and individual words
            _customWords.Add(name);
            foreach (var word in name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (word.Length >= 2)
                    _customWords.Add(word);
        }
    }

    /// <summary>
    ///     Check an entire paragraph for spelling errors.
    ///     Returns diagnostic items with positions for editor display.
    /// </summary>
    public List<DiagnosticItem> CheckParagraph(string text, int baseLine = 0)
    {
        if (!IsLoaded || _dictionary == null) return [];

        var diagnostics = new List<DiagnosticItem>();
        var lines = text.Split('\n');

        for (var lineIdx = 0; lineIdx < lines.Length; lineIdx++)
        {
            var line = lines[lineIdx];

            // Skip markdown code blocks
            if (line.TrimStart().StartsWith("```") || line.TrimStart().StartsWith("    "))
                continue;

            // Skip lines that are just headings (# marks)
            if (line.TrimStart().StartsWith('#'))
            {
                // Still check the heading text itself (after the # marks)
                var headingMatch = HeadingPrefixRegex().Match(line);
                if (headingMatch.Success)
                    line = line[headingMatch.Length..];
                else
                    continue;
            }

            foreach (Match match in WordRegex().Matches(line))
            {
                var word = match.Value;

                // Skip inline code
                var beforeMatch = line[..match.Index];
                if (CountChar(beforeMatch, '`') % 2 == 1)
                    continue; // Inside inline code

                // Skip URLs
                if (IsInsideMarkdownLink(line, match.Index))
                    continue;

                if (!CheckWord(word))
                {
                    var suggestions = Suggest(word);
                    diagnostics.Add(new DiagnosticItem
                    {
                        Type = DiagnosticType.Spelling,
                        Word = word,
                        Line = baseLine + lineIdx,
                        Column = match.Index,
                        Length = word.Length,
                        Message = $"Unknown word: \"{word}\"",
                        Suggestions = suggestions,
                        QuickFixText = suggestions.Count > 0 ? suggestions[0] : null
                    });
                }
            }
        }

        return diagnostics;
    }

    /// <summary>
    ///     Check entire markdown content for spelling errors.
    ///     Skips code blocks, inline code, URLs, and markdown syntax.
    /// </summary>
    public List<DiagnosticItem> CheckDocument(string markdown)
    {
        if (!IsLoaded || _dictionary == null) return [];

        var diagnostics = new List<DiagnosticItem>();
        var lines = markdown.Split('\n');
        var inCodeBlock = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // Track code block state
            if (trimmed.StartsWith("```"))
            {
                inCodeBlock = !inCodeBlock;
                continue;
            }

            if (inCodeBlock) continue;

            // Skip indented code blocks
            if (line.StartsWith("    ") || line.StartsWith('\t'))
                continue;

            // Check this line
            var lineDiagnostics = CheckLine(line, i);
            diagnostics.AddRange(lineDiagnostics);
        }

        return diagnostics;
    }

    private List<DiagnosticItem> CheckLine(string line, int lineNumber)
    {
        var diagnostics = new List<DiagnosticItem>();

        // Strip heading prefix but keep offset
        var textToCheck = line;
        var headingMatch = HeadingPrefixRegex().Match(line);
        var colOffset = 0;
        if (headingMatch.Success)
        {
            textToCheck = line[headingMatch.Length..];
            colOffset = headingMatch.Length;
        }

        foreach (Match match in WordRegex().Matches(textToCheck))
        {
            var word = match.Value;

            // Skip inline code (odd number of backticks before)
            var beforeMatch = textToCheck[..match.Index];
            if (CountChar(beforeMatch, '`') % 2 == 1)
                continue;

            // Skip markdown link URLs [text](url)
            if (IsInsideMarkdownLink(textToCheck, match.Index))
                continue;

            if (!CheckWord(word))
            {
                var suggestions = Suggest(word);
                diagnostics.Add(new DiagnosticItem
                {
                    Type = DiagnosticType.Spelling,
                    Word = word,
                    Line = lineNumber,
                    Column = match.Index + colOffset,
                    Length = word.Length,
                    Message = $"Unknown word: \"{word}\"",
                    Suggestions = suggestions,
                    QuickFixText = suggestions.Count > 0 ? suggestions[0] : null
                });
            }
        }

        return diagnostics;
    }

    // --- Private helpers ---

    private static bool IsCodeOrSpecial(string word)
    {
        // All caps (acronyms like API, HTTP)
        if (word.Length >= 2 && word.All(c => char.IsUpper(c) || char.IsDigit(c)))
            return true;

        // Contains digits (version numbers, hex, etc.)
        if (word.Any(char.IsDigit))
            return true;

        // CamelCase or PascalCase (code identifiers)
        if (word.Length >= 3 && word.Skip(1).Any(char.IsUpper) && word.Any(char.IsLower))
            return true;

        // Contains dots, underscores, dashes (file paths, code)
        if (word.Contains('.') || word.Contains('_') || word.Contains('-'))
            return true;

        return false;
    }

    private static bool IsInsideMarkdownLink(string line, int position)
    {
        // Check if position is inside the URL part of [text](url)
        var lastOpenParen = line.LastIndexOf("](", position, StringComparison.Ordinal);
        if (lastOpenParen < 0) return false;

        var closeParen = line.IndexOf(')', lastOpenParen);
        return closeParen < 0 || position < closeParen;
    }

    private static int CountChar(string text, char c)
    {
        var count = 0;
        foreach (var ch in text)
            if (ch == c)
                count++;
        return count;
    }

    private async Task LoadCustomDictionaryAsync()
    {
        if (!File.Exists(_customDictPath)) return;

        var lines = await File.ReadAllLinesAsync(_customDictPath);
        foreach (var line in lines)
        {
            var word = line.Trim();
            if (!string.IsNullOrEmpty(word))
                _customWords.Add(word);
        }
    }

    private async Task SaveCustomDictionaryAsync()
    {
        var lines = _customWords.OrderBy(w => w, StringComparer.OrdinalIgnoreCase).ToArray();
        await File.WriteAllLinesAsync(_customDictPath, lines);
    }

    [GeneratedRegex(@"^#{1,6}\s+")]
    private static partial Regex HeadingPrefixRegex();

    [GeneratedRegex(@"\b[a-zA-Z']+\b")]
    private static partial Regex WordRegex();
}