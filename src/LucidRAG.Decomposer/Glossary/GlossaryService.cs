using System.Reflection;
using LucidRAG.Decomposer.Orchestration;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LucidRAG.Decomposer.Glossary;

/// <summary>
/// Loads glossary files at startup and seeds the KB with foundational terms.
/// Plugin packs can ship their own glossary.yaml files (discovered via assembly scanning).
///
/// Lifecycle:
///   Startup glossary (static YAML) → seed at boot → Central Corpus (KB)
///   KnowledgeGapDetector (runtime) → enriches dynamically → Central Corpus (KB)
/// </summary>
public class GlossaryService
{
    private readonly ILogger<GlossaryService>? _logger;

    public GlossaryService(ILogger<GlossaryService>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Load the core glossary from embedded resource.
    /// </summary>
    public GlossaryConfig LoadCoreGlossary()
    {
        var assembly = typeof(GlossaryService).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("glossary.yaml", StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
        {
            _logger?.LogWarning("Core glossary.yaml not found as embedded resource");
            return new GlossaryConfig { Corpus = "core", Version = 1 };
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var yaml = reader.ReadToEnd();

        return ParseYaml(yaml);
    }

    /// <summary>
    /// Discover and load glossary files from plugin pack assemblies.
    /// Scans loaded assemblies for embedded resources named *glossary.yaml.
    /// </summary>
    public List<GlossaryConfig> DiscoverPluginGlossaries()
    {
        var glossaries = new List<GlossaryConfig>();

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic) continue;

            try
            {
                var resources = assembly.GetManifestResourceNames()
                    .Where(n => n.EndsWith("glossary.yaml", StringComparison.OrdinalIgnoreCase)
                                && n != typeof(GlossaryService).Assembly.GetManifestResourceNames()
                                    .FirstOrDefault(r => r.EndsWith("glossary.yaml")));

                foreach (var resource in resources)
                {
                    using var stream = assembly.GetManifestResourceStream(resource);
                    if (stream == null) continue;

                    using var reader = new StreamReader(stream);
                    var yaml = reader.ReadToEnd();
                    var config = ParseYaml(yaml);

                    _logger?.LogInformation("Discovered plugin glossary: {Corpus} ({Count} terms) from {Assembly}",
                        config.Corpus, config.Terms.Count, assembly.GetName().Name);

                    glossaries.Add(config);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to scan assembly {Assembly} for glossaries",
                    assembly.GetName().Name);
            }
        }

        return glossaries;
    }

    /// <summary>
    /// Seed all glossary terms into the KB via the provided executor.
    /// Skips terms already present and fresh (within TTL).
    /// </summary>
    public async Task SeedAsync(
        ISubQueryExecutor executor,
        Func<string, Task<bool>> isTermFreshInKb,
        CancellationToken ct = default)
    {
        var core = LoadCoreGlossary();
        var plugins = DiscoverPluginGlossaries();
        var allGlossaries = new[] { core }.Concat(plugins).ToList();

        var totalTerms = allGlossaries.Sum(g => g.Terms.Count);
        var seeded = 0;
        var skipped = 0;

        _logger?.LogInformation("Seeding glossary: {Total} terms from {Count} glossaries",
            totalTerms, allGlossaries.Count);

        foreach (var glossary in allGlossaries)
        {
            foreach (var term in glossary.Terms)
            {
                if (ct.IsCancellationRequested) break;

                // Check if already fresh in KB
                if (await isTermFreshInKb(term.Term))
                {
                    skipped++;
                    continue;
                }

                // Seed via executor (rate-limited)
                try
                {
                    var node = new Models.QueryNode
                    {
                        Query = term.Query,
                        Type = Models.QueryNodeType.KnowledgeGap,
                        IsPrerequisite = false,
                        StoreResultInKb = true,
                        Concept = Enum.TryParse<Models.ConceptType>(term.ConceptType, true, out var ct2)
                            ? ct2 : Models.ConceptType.Definition,
                        Depth = 0
                    };

                    await executor.ExecuteAsync(node, ct);
                    seeded++;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to seed glossary term: {Term}", term.Term);
                }
            }
        }

        _logger?.LogInformation("Glossary seeding complete: {Seeded} seeded, {Skipped} skipped (fresh)",
            seeded, skipped);
    }

    private static GlossaryConfig ParseYaml(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        return deserializer.Deserialize<GlossaryConfig>(yaml);
    }
}
