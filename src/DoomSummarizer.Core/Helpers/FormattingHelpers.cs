using Spectre.Console;
using Spectre.Console.Rendering;

namespace DoomSummarizer.Helpers;

public static class FormattingHelpers
{
    public static string Truncate(string text, int maxLength)
    {
        return text.Length <= maxLength ? text : text[..(maxLength - 3)] + "...";
    }

    public static string FormatAge(DateTimeOffset timestamp)
    {
        var age = DateTimeOffset.UtcNow - timestamp;
        return age.TotalMinutes switch
        {
            < 1 => "just now",
            < 60 => $"{(int)age.TotalMinutes}m ago",
            < 1440 => $"{(int)age.TotalHours}h ago",
            _ => $"{(int)age.TotalDays}d ago"
        };
    }

    // ── Centralized Spectre.Console markup safety ──────────────────────

    /// <summary>
    /// Safely strip Spectre.Console markup tags from text.
    /// Unlike Markup.Remove(), this does NOT throw on invalid markup like "[Go.pdf]".
    /// Falls back to Markup.Escape() if the text can't be parsed as valid markup.
    /// </summary>
    public static string SafeStripMarkup(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        try { return Markup.Remove(text); }
        catch { return Markup.Escape(text); }
    }

    /// <summary>
    /// Escape user data for Spectre.Console markup. Null-safe.
    /// Single source of truth for all markup escaping.
    /// </summary>
    public static string Esc(string? text) => Markup.Escape(text ?? "");

    /// <summary>
    /// Truncate and escape in one call — the most common pattern for titles.
    /// </summary>
    public static string TruncEsc(string? text, int maxLength)
        => Markup.Escape(Truncate(text ?? "", maxLength));

    /// <summary>
    /// Safely write a Spectre renderable (Panel, Table, Tree, Rule, etc.).
    /// Falls back to plain-text on markup parse errors instead of crashing.
    /// </summary>
    public static void SafeWrite(IRenderable renderable, string? fallbackText = null)
    {
        try
        {
            AnsiConsole.Write(renderable);
        }
        catch (Exception ex) when (ex.Message.Contains("color or style") || ex.Message.Contains("markup"))
        {
            if (fallbackText != null)
                Console.WriteLine(fallbackText);
            else
                AnsiConsole.MarkupLine($"[dim](rendering error: {Markup.Escape(ex.Message)})[/]");
        }
    }

    /// <summary>
    /// Safely call AnsiConsole.MarkupLine, falling back to plain text on markup errors.
    /// </summary>
    public static void SafeMarkupLine(string markup)
    {
        try
        {
            AnsiConsole.MarkupLine(markup);
        }
        catch (Exception ex) when (ex.Message.Contains("color or style") || ex.Message.Contains("markup"))
        {
            // Strip markup tags and write as plain text (SafeStripMarkup won't throw)
            Console.WriteLine(SafeStripMarkup(markup));
        }
    }
}
