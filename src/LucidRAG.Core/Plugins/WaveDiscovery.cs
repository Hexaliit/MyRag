using System.Reflection;
using LucidRAG.Manifests;
using Mostlylucid.Summarizer.Core.Analysis;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LucidRAG.Plugins;

/// <summary>
///     Discovers IWave implementations and embedded wave manifests from assemblies.
///     Used during plugin registration to find all waves contributed by a plugin.
/// </summary>
public static class WaveDiscovery
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    ///     Scan an assembly for all types implementing IWave.
    ///     Returns the types (not instances — use DI to resolve them).
    /// </summary>
    public static IReadOnlyList<Type> ScanForWaveTypes(Assembly assembly)
    {
        return assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                        && typeof(IWave).IsAssignableFrom(t))
            .ToList();
    }

    /// <summary>
    ///     Load embedded *.wave.yaml manifests from an assembly.
    /// </summary>
    public static IReadOnlyList<WaveManifest> LoadEmbeddedManifests(
        Assembly assembly,
        ILogger? logger = null)
    {
        var manifests = new List<WaveManifest>();
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".wave.yaml", StringComparison.OrdinalIgnoreCase));

        foreach (var resourceName in resourceNames)
            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null)
                {
                    logger?.LogWarning("Could not load embedded resource: {Resource}", resourceName);
                    continue;
                }

                using var reader = new StreamReader(stream);
                var yaml = reader.ReadToEnd();
                var manifest = YamlDeserializer.Deserialize<WaveManifest>(yaml);

                if (manifest is not null)
                {
                    manifests.Add(manifest);
                    logger?.LogDebug("Loaded wave manifest: {Name} from {Resource}", manifest.Name, resourceName);
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to load wave manifest from {Resource}", resourceName);
            }

        return manifests;
    }

    /// <summary>
    ///     Load embedded *.prompt.yaml resources from an assembly.
    ///     Returns raw YAML strings keyed by name.
    /// </summary>
    public static IReadOnlyDictionary<string, string> LoadEmbeddedPrompts(Assembly assembly)
    {
        var prompts = new Dictionary<string, string>();
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".prompt.yaml", StringComparison.OrdinalIgnoreCase));

        foreach (var resourceName in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null) continue;

            using var reader = new StreamReader(stream);
            var name = ExtractName(resourceName, ".prompt.yaml");
            prompts[name] = reader.ReadToEnd();
        }

        return prompts;
    }

    /// <summary>
    ///     Load embedded *.lens.yaml resources from an assembly.
    ///     Returns raw YAML strings keyed by name.
    /// </summary>
    public static IReadOnlyDictionary<string, string> LoadEmbeddedLenses(Assembly assembly)
    {
        var lenses = new Dictionary<string, string>();
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".lens.yaml", StringComparison.OrdinalIgnoreCase));

        foreach (var resourceName in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null) continue;

            using var reader = new StreamReader(stream);
            var name = ExtractName(resourceName, ".lens.yaml");
            lenses[name] = reader.ReadToEnd();
        }

        return lenses;
    }

    private static string ExtractName(string resourceName, string suffix)
    {
        // "MyAssembly.Manifests.foo.wave.yaml" → "foo"
        var withoutSuffix = resourceName[..^suffix.Length];
        var lastDot = withoutSuffix.LastIndexOf('.');
        return lastDot >= 0 ? withoutSuffix[(lastDot + 1)..] : withoutSuffix;
    }
}