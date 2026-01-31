using System.Text.RegularExpressions;

namespace DoomSummarizer.Helpers;

/// <summary>
/// Derives collection names from URLs, file paths, or document names.
/// Used when no explicit --name is provided.
/// </summary>
public static partial class CollectionNaming
{
    /// <summary>
    /// Derive a collection name from a URL.
    /// Examples:
    ///   https://docs.example.com/api/v2 → docs-example-com
    ///   https://www.myblog.io → myblog-io
    /// </summary>
    public static string FromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Sanitize(url);

        var host = uri.Host
            .Replace("www.", "")
            .Replace('.', '-');

        return Sanitize(host);
    }

    /// <summary>
    /// Derive a collection name from a file or directory path.
    /// Examples:
    ///   E:\books\scifi → books-scifi
    ///   C:\Users\scott\Documents\research.pdf → research
    ///   /home/user/markdown → markdown
    /// </summary>
    public static string FromPath(string path)
    {
        path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Single file: use filename without extension
        if (File.Exists(path))
            return Sanitize(Path.GetFileNameWithoutExtension(path));

        // Directory: use last 2 path segments for context
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(p => !string.IsNullOrWhiteSpace(p) && p.Length > 1 && !IsDriveRoot(p))
            .TakeLast(2)
            .ToArray();

        return parts.Length > 0 ? Sanitize(string.Join("-", parts)) : "local";
    }

    /// <summary>
    /// Derive a collection name from any input (URL, path, or text).
    /// </summary>
    public static string Auto(string input)
    {
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http"))
            return FromUrl(input);

        if (input.Contains(Path.DirectorySeparatorChar) || input.Contains(Path.AltDirectorySeparatorChar))
            return FromPath(input);

        return Sanitize(input);
    }

    private static string Sanitize(string name)
    {
        // Lowercase, replace non-alphanumeric with hyphens, collapse
        var clean = InvalidCharsRx().Replace(name.ToLowerInvariant(), "-").Trim('-');
        clean = MultiHyphenRx().Replace(clean, "-");
        // Cap at 50 chars
        return clean.Length > 50 ? clean[..50].TrimEnd('-') : clean;
    }

    private static bool IsDriveRoot(string segment) =>
        segment.Length <= 2 && segment.EndsWith(':');

    [GeneratedRegex("[^a-z0-9-]")]
    private static partial Regex InvalidCharsRx();

    [GeneratedRegex("-{2,}")]
    private static partial Regex MultiHyphenRx();
}
