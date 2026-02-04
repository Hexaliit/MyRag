using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mostlylucid.DoomSummarizer.Plugin.Books.Splitters;

/// <summary>
///     A named pattern (novel, play, anthology, academic) with compiled regex markers.
///     Loaded from embedded YAML resources.
/// </summary>
public class StructuralPattern
{
    public required string Name { get; init; }
    public required string Description { get; init; }

    public IReadOnlyDictionary<string, List<Regex>> MarkersByLevel { get; init; } =
        new Dictionary<string, List<Regex>>();
}

/// <summary>
///     Loads and caches structural patterns from embedded YAML resources.
/// </summary>
public static class StructuralPatterns
{
    private static readonly Lazy<IReadOnlyDictionary<string, StructuralPattern>> _patterns
        = new(LoadAll);

    /// <summary>All loaded patterns, keyed by name.</summary>
    public static IReadOnlyDictionary<string, StructuralPattern> All => _patterns.Value;

    /// <summary>Get a specific pattern by name, or null if not found.</summary>
    public static StructuralPattern? Get(string name)
    {
        return All.GetValueOrDefault(name);
    }

    /// <summary>
    ///     Get the level hierarchy for a pattern.
    ///     Returns levels in descending order of scope (broadest first).
    /// </summary>
    public static IReadOnlyList<string> GetLevelHierarchy(string patternName)
    {
        return patternName switch
        {
            "novel" => ["part", "chapter", "prologue", "epilogue", "section_break"],
            "play" => ["work", "act", "scene"],
            "anthology" => ["work", "part", "chapter", "section"],
            "academic" => ["section", "subsection"],
            _ => ["chapter", "section"]
        };
    }

    private static IReadOnlyDictionary<string, StructuralPattern> LoadAll()
    {
        var patterns = new Dictionary<string, StructuralPattern>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(StructuralPatterns).Assembly;
        var prefix = "Mostlylucid.DoomSummarizer.Plugin.Books.Resources.patterns.";

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(prefix, StringComparison.Ordinal)
                                 && n.EndsWith(".yaml", StringComparison.Ordinal)))
            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null) continue;
                using var reader = new StreamReader(stream);
                var yaml = reader.ReadToEnd();

                var raw = deserializer.Deserialize<PatternYaml>(yaml);
                if (raw == null) continue;

                var markersByLevel = new Dictionary<string, List<Regex>>();
                if (raw.Markers != null)
                    foreach (var (level, regexStrings) in raw.Markers)
                        markersByLevel[level] = regexStrings
                            .Select(r =>
                            {
                                try
                                {
                                    return new Regex(r, RegexOptions.Compiled | RegexOptions.Multiline);
                                }
                                catch
                                {
                                    return null;
                                }
                            })
                            .Where(r => r != null)
                            .Cast<Regex>()
                            .ToList();

                patterns[raw.Name] = new StructuralPattern
                {
                    Name = raw.Name,
                    Description = raw.Description ?? "",
                    MarkersByLevel = markersByLevel
                };
            }
            catch
            {
                // Skip invalid pattern files
            }

        return patterns;
    }

    // YAML deserialization model
    private class PatternYaml
    {
        [YamlMember(Alias = "name")] public string Name { get; set; } = "";

        [YamlMember(Alias = "description")] public string? Description { get; set; }

        [YamlMember(Alias = "markers")] public Dictionary<string, List<string>>? Markers { get; set; }
    }
}