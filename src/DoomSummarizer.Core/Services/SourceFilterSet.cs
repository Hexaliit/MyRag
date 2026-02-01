namespace DoomSummarizer.Services;

/// <summary>
/// Parsed source filter supporting include, exclude, KB-exclusion, and URL glob semantics.
///
/// Syntax:
///   -s hn -s reddit          → Include hn OR reddit
///   -s "^reddit"             → Exclude reddit (include everything else)
///   -s "^*.bbc.*"            → Exclude URLs matching glob pattern
///   --name docs              → Include crawl:docs
///   --name web               → Auto-exclude KB sources (crawl:*, file:*, youtube:*, manual)
///   --name web,docs           → Non-KB sources + also include crawl:docs
///   --name ^manual            → Exclude manual collection
///
/// Source ordering matters for retrieval weighting: first source gets highest boost.
/// </summary>
public sealed class SourceFilterSet
{
    /// <summary>Sources to include (OR semantics). Empty = include all.</summary>
    public IReadOnlyList<string> Include { get; }

    /// <summary>Sources to exclude (AND NOT semantics). Applied after include.</summary>
    public IReadOnlyList<string> Exclude { get; }

    /// <summary>Auto-exclude KB sources (crawl:*, file:*, youtube:*, manual) unless web-validated.</summary>
    public bool ExcludeKbSources { get; }

    /// <summary>URL glob patterns to exclude (e.g., *.bbc.*).</summary>
    public IReadOnlyList<string> UrlExcludes { get; }

    /// <summary>True if any filters are active.</summary>
    public bool HasFilters => Include.Count > 0 || Exclude.Count > 0
                              || ExcludeKbSources || UrlExcludes.Count > 0;

    /// <summary>True if this is an include-only filter (no exclusions).</summary>
    public bool IsIncludeOnly => Include.Count > 0 && Exclude.Count == 0
                                 && !ExcludeKbSources && UrlExcludes.Count == 0;

    // --- Constants ---

    /// <summary>Source tag prefixes that identify KB (knowledge-base) sources.</summary>
    public static readonly string[] KbPrefixes = ["crawl:", "file:", "youtube:"];

    /// <summary>The source tag for manually loaded content.</summary>
    public const string ManualTag = "manual";

    /// <summary>Internal sentinel marker injected by MergeNameAndSource to signal KB exclusion.</summary>
    internal const string ExcludeKbMarker = "\0__exclude_kb__";

    public SourceFilterSet(
        IReadOnlyList<string> include,
        IReadOnlyList<string> exclude,
        bool excludeKbSources = false,
        IReadOnlyList<string>? urlExcludes = null)
    {
        Include = include;
        Exclude = exclude;
        ExcludeKbSources = excludeKbSources;
        UrlExcludes = urlExcludes ?? [];
    }

    // --- Static helpers ---

    /// <summary>
    /// Check whether a source tag identifies a KB source (crawl:*, file:*, youtube:*, manual).
    /// </summary>
    public static bool IsKbSource(string source) =>
        string.Equals(source, ManualTag, StringComparison.OrdinalIgnoreCase)
        || KbPrefixes.Any(p => source.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolve a bare collection name to a fully-qualified source tag.
    /// Names already containing ':' are returned as-is.
    /// </summary>
    public static string ResolveCollectionName(string name) =>
        name.Contains(':') ? name : $"crawl:{name}";

    /// <summary>
    /// Convert a glob pattern (using * and ?) to a SQL LIKE pattern.
    /// Escapes existing SQL wildcards with backslash (requires ESCAPE '\' in the query),
    /// then converts glob chars.
    /// </summary>
    public static string GlobToSqlLike(string glob) =>
        glob.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_")
            .Replace("*", "%").Replace("?", "_");

    /// <summary>
    /// Parse source strings into include/exclude/KB-exclusion/URL-glob sets.
    /// Strings starting with ^ are exclusions; ^ strings containing * or ? are URL globs.
    /// The internal ExcludeKbMarker triggers ExcludeKbSources mode.
    /// </summary>
    public static SourceFilterSet Parse(IReadOnlyList<string>? sources)
    {
        if (sources is not { Count: > 0 })
            return Empty;

        var include = new List<string>();
        var exclude = new List<string>();
        var urlExcludes = new List<string>();
        var excludeKb = false;

        foreach (var s in sources)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            if (s == ExcludeKbMarker) { excludeKb = true; continue; }

            if (s.StartsWith('^') && s.Length > 1)
            {
                var val = s[1..];
                if (val.Contains('*') || val.Contains('?'))
                    urlExcludes.Add(val);   // URL glob exclusion
                else
                    exclude.Add(val);       // Source tag exclusion
            }
            else
            {
                include.Add(s);
            }
        }

        return new SourceFilterSet(include, exclude, excludeKb, urlExcludes);
    }

    /// <summary>
    /// Merge --name collection values with -s source values into a unified source filter list.
    /// Handles: multiple comma-separated names, "web" keyword, smart prefix resolution, ^ exclusion.
    /// </summary>
    public static IReadOnlyList<string>? MergeNameAndSource(
        string? nameArg, IReadOnlyList<string>? sources)
    {
        var hasNames = !string.IsNullOrWhiteSpace(nameArg);
        var hasSources = sources is { Count: > 0 };
        if (!hasNames && !hasSources) return null;  // No filtering at all

        var result = new List<string>();

        if (hasNames)
        {
            var names = nameArg!.Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var name in names)
            {
                // "web" keyword = auto-exclude KB sources
                if (name.Equals("web", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(ExcludeKbMarker);
                    continue;
                }

                var isExclude = name.StartsWith('^');
                var raw = isExclude ? name[1..] : name;

                // Globs pass through (URL-level exclusion)
                if (raw.Contains('*') || raw.Contains('?'))
                {
                    result.Add(isExclude ? $"^{raw}" : raw);
                    continue;
                }

                // Resolve bare names to crawl:{name}
                var resolved = ResolveCollectionName(raw);
                result.Add(isExclude ? $"^{resolved}" : resolved);
            }
        }

        if (hasSources)
            result.AddRange(sources!);

        return result.Count > 0 ? result : null;
    }

    public static readonly SourceFilterSet Empty = new([], []);
}
