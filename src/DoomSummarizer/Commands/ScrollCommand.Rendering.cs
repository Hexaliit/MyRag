using System.Text;
using System.Text.RegularExpressions;
using DoomSummarizer.Helpers;
using DoomSummarizer.Models;
using Spectre.Console;

namespace DoomSummarizer.Commands;

/// <summary>
/// Spectre Console markup rendering utilities: word wrap, markdown conversion, visible length,
/// and deterministic sources section.
/// </summary>
public sealed partial class ScrollCommand
{
    /// <summary>
    /// Render a compact "Sources Used" panel showing the top documents
    /// with their most salient excerpt. Deterministic — no LLM involved.
    /// </summary>
    private static void RenderSourcesUsed(
        List<(string title, string summary, string topic, float sentiment, string url, double relevance)> analyzedItems,
        List<ContentItem> uniqueItems,
        int maxWidth,
        int topN = 5)
    {
        if (analyzedItems.Count == 0) return;

        var sourceParts = new List<string>();
        var shown = 0;

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in analyzedItems.OrderByDescending(a => a.relevance))
        {
            if (shown >= topN) break;

            // Deduplicate by normalized URL or title
            var normalizedUrl = !string.IsNullOrEmpty(item.url)
                ? item.url.Split('?')[0].TrimEnd('/').ToLowerInvariant()
                : "";
            var dedupeKey = !string.IsNullOrEmpty(normalizedUrl)
                ? normalizedUrl
                : item.title.ToLowerInvariant().Trim();
            if (!seenKeys.Add(dedupeKey))
                continue;

            // Count how many times this article appears
            var refCount = analyzedItems.Count(a =>
            {
                var aUrl = !string.IsNullOrEmpty(a.url) ? a.url.Split('?')[0].TrimEnd('/').ToLowerInvariant() : "";
                var aKey = !string.IsNullOrEmpty(aUrl) ? aUrl : a.title.ToLowerInvariant().Trim();
                return string.Equals(aKey, dedupeKey, StringComparison.OrdinalIgnoreCase);
            });

            // Find matching ContentItem for excerpt
            var contentItem = uniqueItems.FirstOrDefault(u =>
                string.Equals(u.Title, item.title, StringComparison.Ordinal) ||
                string.Equals(u.Url, item.url, StringComparison.Ordinal));

            // Extract most salient excerpt: prefer summary (top salience segments), fall back to content
            var excerptSource = !string.IsNullOrEmpty(item.summary) && item.summary != item.title
                ? item.summary
                : contentItem?.Content ?? item.summary;
            var excerpt = ExtractLeadExcerpt(excerptSource, maxChars: 120);

            // Domain from URL
            var domain = GetDomainFromUrl(item.url);
            var sourceLabel = !string.IsNullOrEmpty(domain) ? domain : (contentItem?.Source ?? "web");

            var refSuffix = refCount > 1 ? $" ({refCount} refs)" : "";
            sourceParts.Add($"[cyan]{Markup.Escape(FormattingHelpers.Truncate(item.title, 70))}[/]");
            // Show full URL if available, otherwise just domain
            if (!string.IsNullOrEmpty(item.url))
                sourceParts.Add($"  [link={Markup.Escape(item.url)}][dim underline]{Markup.Escape(item.url)}[/][/]");
            sourceParts.Add($"  [dim]{Markup.Escape(sourceLabel)}[/] [grey]|[/] [dim]{item.relevance:F2}{refSuffix}[/]");
            if (!string.IsNullOrEmpty(excerpt))
                sourceParts.Add($"  [grey]{Markup.Escape(excerpt)}[/]");
            sourceParts.Add("");
            shown++;
        }

        if (sourceParts.Count == 0) return;

        // Remove trailing blank line
        if (sourceParts.Count > 0 && string.IsNullOrEmpty(sourceParts[^1]))
            sourceParts.RemoveAt(sourceParts.Count - 1);

        var content = string.Join("\n", sourceParts);
        var wrapped = WordWrapMarkup(content, maxWidth);
        AnsiConsole.Write(new Panel(wrapped)
            .Header("[bold dim]Sources Used[/]")
            .Border(BoxBorder.Rounded)
            .Padding(1, 0));
    }

    /// <summary>
    /// Extract the lead sentence or first N characters as an excerpt.
    /// </summary>
    private static string ExtractLeadExcerpt(string text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        // Strip HTML tags, markdown noise, blockquotes, links, escaped chars
        text = HtmlTagPattern().Replace(text, "");
        text = MarkdownHeadingPrefixPattern().Replace(text, "");
        text = BlockquotePattern().Replace(text, "");
        text = MarkdownLinkPattern().Replace(text, "$1"); // [text](url) → text
        text = text.Replace("**", "").Replace("*", "").Replace("`", "");
        text = text.Replace("\\(", "(").Replace("\\)", ")").Replace("\\[", "[").Replace("\\]", "]");
        text = text.Replace("~~~", "").Replace("```", "");
        text = WhitespaceCollapsePattern().Replace(text, " ").Trim();

        // Try to find first complete sentence (avoid IndexOf with startIndex > length)
        var startIdx = Math.Min(20, text.Length);
        var dotIdx = startIdx > 0 ? text.IndexOf(". ", startIdx, StringComparison.Ordinal) : -1;
        if (dotIdx > 0 && dotIdx < maxChars)
            return text[..(dotIdx + 1)];

        // Fall back to truncation
        if (text.Length <= maxChars) return text;
        var spaceIdx = text.LastIndexOf(' ', Math.Min(maxChars, text.Length - 1));
        return spaceIdx > 40 ? text[..spaceIdx] + "..." : text[..Math.Min(maxChars, text.Length)] + "...";
    }

    /// <summary>
    /// Extract domain name from a URL for display.
    /// </summary>
    private static string GetDomainFromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        try
        {
            var uri = new Uri(url);
            var host = uri.Host;
            // Strip www. prefix
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                host = host[4..];
            return host;
        }
        catch { return ""; }
    }

    /// <summary>
    /// Word-wrap Spectre markup text to a max visible width.
    /// Uses token-based approach: markup tags are atomic (never split).
    /// </summary>
    private static string WordWrapMarkup(string text, int maxWidth)
    {
        if (maxWidth <= 0 || string.IsNullOrEmpty(text)) return text;

        var result = new System.Text.StringBuilder();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var visibleLen = VisibleLength(line);

            if (visibleLen <= maxWidth)
            {
                result.AppendLine(line);
                continue;
            }

            // Tokenize line into segments: markup tags (atomic) and visible text words
            var tokens = TokenizeMarkupLine(line);
            var currentLine = new System.Text.StringBuilder();
            var currentVisible = 0;

            foreach (var (token, isTag) in tokens)
            {
                if (isTag)
                {
                    // Markup tags don't contribute to visible width — always add them
                    currentLine.Append(token);
                    continue;
                }

                // Visible text — check if adding it exceeds width
                var tokenVisible = token.Length;

                if (currentVisible + tokenVisible > maxWidth && currentVisible > 0)
                {
                    // Emit current line and start new one
                    result.AppendLine(currentLine.ToString().TrimEnd());
                    currentLine.Clear();
                    currentVisible = 0;

                    // Skip leading space on continuation
                    var trimmedToken = token.TrimStart();
                    currentLine.Append(trimmedToken);
                    currentVisible = trimmedToken.Length;
                }
                else
                {
                    currentLine.Append(token);
                    currentVisible += tokenVisible;
                }
            }

            if (currentLine.Length > 0)
                result.AppendLine(currentLine.ToString().TrimEnd());
        }

        return result.ToString().TrimEnd();
    }

    /// <summary>
    /// Split a Spectre markup line into tokens: (text, isTag) pairs.
    /// Tags like [bold cyan] are single atomic tokens. Text between tags
    /// is split at word boundaries so wrapping can occur.
    /// </summary>
    private static List<(string token, bool isTag)> TokenizeMarkupLine(string line)
    {
        var tokens = new List<(string token, bool isTag)>();
        var i = 0;

        while (i < line.Length)
        {
            // Escaped bracket [[
            if (line[i] == '[' && i + 1 < line.Length && line[i + 1] == '[')
            {
                tokens.Add(("[[", false));
                i += 2;
                continue;
            }

            // Markup tag [...]
            if (line[i] == '[' && i + 1 < line.Length && line[i + 1] != '[')
            {
                var closeIdx = line.IndexOf(']', i + 1);
                if (closeIdx >= 0)
                {
                    tokens.Add((line[i..(closeIdx + 1)], true));
                    i = closeIdx + 1;
                    continue;
                }
            }

            // Visible text — collect up to next tag or space (for word-boundary wrapping)
            var start = i;
            while (i < line.Length && line[i] != '[')
            {
                i++;
            }

            if (i > start)
            {
                // Split this text segment at spaces for word-wrap boundaries
                var segment = line[start..i];
                var wordStart = 0;
                for (var j = 0; j < segment.Length; j++)
                {
                    if (segment[j] == ' ' && j > wordStart)
                    {
                        tokens.Add((segment[wordStart..j], false));
                        tokens.Add((" ", false));
                        wordStart = j + 1;
                    }
                }
                if (wordStart < segment.Length)
                    tokens.Add((segment[wordStart..], false));
            }
        }

        return tokens;
    }

    /// <summary>
    /// Count visible characters in a Spectre markup string (excluding [tags]).
    /// </summary>
    private static int VisibleLength(string markup)
    {
        var count = 0;
        var i = 0;
        while (i < markup.Length)
        {
            if (markup[i] == '[' && i + 1 < markup.Length)
            {
                if (markup[i + 1] == '[')
                {
                    count++;
                    i += 2;
                    continue;
                }

                var closeIdx = markup.IndexOf(']', i + 1);
                if (closeIdx >= 0)
                {
                    i = closeIdx + 1;
                    continue;
                }
            }
            count++;
            i++;
        }
        return count;
    }

    /// <summary>
    /// Convert markdown to Spectre.Console markup for rich console rendering.
    /// Handles headings, bold, italic, links, bullets, and horizontal rules.
    /// </summary>
    internal static string MarkdownToSpectre(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return "";

        var lines = markdown.Split('\n');
        var result = new System.Text.StringBuilder();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // Horizontal rule
            if (line.Trim() is "---" or "***" or "___")
            {
                result.AppendLine("[dim]────────────────────────────────────────[/]");
                continue;
            }

            // Headings
            if (line.StartsWith("### "))
            {
                var text = Markup.Escape(line[4..].Trim());
                result.AppendLine($"[bold cyan]{text}[/]");
                continue;
            }
            if (line.StartsWith("## "))
            {
                var text = Markup.Escape(line[3..].Trim());
                result.AppendLine($"[bold underline yellow]{text}[/]");
                continue;
            }
            if (line.StartsWith("# "))
            {
                var text = Markup.Escape(line[2..].Trim());
                result.AppendLine($"[bold underline green]{text}[/]");
                continue;
            }

            // Bullet points
            if (line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("* "))
            {
                var indent = line.Length - line.TrimStart().Length;
                var bulletText = line.TrimStart()[2..];
                var indentStr = new string(' ', indent);
                result.AppendLine($"{indentStr}[cyan]•[/] {FormatInlineMarkdown(bulletText)}");
                continue;
            }

            // Regular line — process inline markdown
            result.AppendLine(FormatInlineMarkdown(line));
        }

        return result.ToString().TrimEnd();
    }

    /// <summary>
    /// Process inline markdown: **bold**, *italic*, [links](url), `code`.
    /// Uses placeholder tokens so markdown patterns are processed on raw text
    /// (with their content escaped), then remaining literal text is escaped afterward.
    /// </summary>
    private static string FormatInlineMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var replacements = new List<string>();
        string Store(string spectreMarkup)
        {
            var idx = replacements.Count;
            replacements.Add(spectreMarkup);
            return $"\x01{idx}\x02";
        }

        // Process patterns on raw text (order matters: code > links > bold > italic)
        // Each captured group's content is Markup.Escaped individually.

        // Inline code: `code` → [grey on grey15]code[/]
        text = InlineCodePattern().Replace(text,
            m => Store($"[grey on grey15]{Markup.Escape(m.Groups[1].Value)}[/]"));

        // Links: [text](url) → [cyan underline]text[/] [dim](url)[/]
        text = MarkdownLinkCapturePattern().Replace(text,
            m => Store($"[cyan underline]{Markup.Escape(m.Groups[1].Value)}[/] [dim]({Markup.Escape(m.Groups[2].Value)})[/]"));

        // Bold: **text** → [bold]text[/]
        text = BoldPattern().Replace(text,
            m => Store($"[bold]{Markup.Escape(m.Groups[1].Value)}[/]"));

        // Italic: *text* → [italic]text[/] (single asterisks not consumed by bold)
        text = ItalicPattern().Replace(text,
            m => Store($"[italic]{Markup.Escape(m.Groups[1].Value)}[/]"));

        // Escape remaining literal text (Markup.Escape only affects [ and ],
        // so \x01/\x02 placeholder chars pass through untouched)
        text = Markup.Escape(text);

        // Restore placeholders with their Spectre markup
        for (var i = 0; i < replacements.Count; i++)
            text = text.Replace($"\x01{i}\x02", replacements[i]);

        return text;
    }

    // --- Source-generated regex for rendering ---

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagPattern();

    [GeneratedRegex(@"^#{1,6}\s+", RegexOptions.Multiline)]
    private static partial Regex MarkdownHeadingPrefixPattern();

    [GeneratedRegex(@"^>\s*", RegexOptions.Multiline)]
    private static partial Regex BlockquotePattern();

    [GeneratedRegex(@"\[([^\]]+)\]\([^\)]+\)")]
    private static partial Regex MarkdownLinkPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceCollapsePattern();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCodePattern();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)")]
    private static partial Regex MarkdownLinkCapturePattern();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex BoldPattern();

    [GeneratedRegex(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)")]
    private static partial Regex ItalicPattern();
}
