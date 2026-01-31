using DoomSummarizer.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DoomSummarizer.Services;

/// <summary>
/// Resolves a vibe name into a full <see cref="Vibe"/> by checking:
///   1. Lens YAML files (~/.doomsummarizer/lenses/*.lens.yaml)
///   2. DoomConfig.Vibes dictionary (simple name → prompt mapping)
///   3. Arbitrary text (used as-is for the prompt instruction)
///
/// This is the unification point between LucidRAG lenses and DoomSummarizer vibes.
/// A vibe IS the personality facet of a lens. When a lens YAML is found, the full
/// personality (tone, persona, style notes, phrase preferences) is extracted and
/// carried through synthesis.
/// </summary>
public sealed class VibeResolver
{
    private readonly Dictionary<string, Vibe> _lensVibes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _configVibes;
    private bool _loaded;

    /// <summary>
    /// All lens directories that were scanned.
    /// </summary>
    public IReadOnlyList<string> LensDirectories { get; }

    /// <summary>
    /// Names of all available vibes (from lenses + config).
    /// </summary>
    public IReadOnlyList<string> AvailableVibes =>
        _lensVibes.Keys
            .Concat(_configVibes.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v)
            .ToList();

    public VibeResolver(DoomConfig config)
    {
        _configVibes = new Dictionary<string, string>(config.Vibes, StringComparer.OrdinalIgnoreCase);

        // Lens directories: user override first, then app data
        var dirs = new List<string>();
        var userLenses = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".doomsummarizer", "lenses");
        dirs.Add(userLenses);

        var appDataLenses = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DoomSummarizer", "lenses");
        if (!string.Equals(userLenses, appDataLenses, StringComparison.OrdinalIgnoreCase))
            dirs.Add(appDataLenses);

        LensDirectories = dirs;
    }

    /// <summary>
    /// Load lens YAML files from all configured directories,
    /// plus optional embedded resources from the calling assembly.
    /// Call once during startup.
    /// </summary>
    /// <param name="embeddedResourceAssembly">
    /// Optional assembly to scan for embedded *.lens.yaml resources.
    /// Embedded lenses are loaded first, then filesystem lenses override them.
    /// </param>
    public void LoadLenses(System.Reflection.Assembly? embeddedResourceAssembly = null)
    {
        if (_loaded) return;
        _loaded = true;

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        // 1. Load embedded resource lenses (lowest priority — filesystem overrides)
        if (embeddedResourceAssembly != null)
        {
            foreach (var resourceName in embeddedResourceAssembly.GetManifestResourceNames()
                .Where(n => n.EndsWith(".lens.yaml", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    using var stream = embeddedResourceAssembly.GetManifestResourceStream(resourceName);
                    if (stream == null) continue;
                    using var reader = new StreamReader(stream);
                    var yaml = reader.ReadToEnd();
                    LoadLensYaml(yaml, $"embedded:{resourceName}", deserializer);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load embedded lens {resourceName}: {ex.Message}");
                }
            }
        }

        // 2. Load filesystem lenses (higher priority — overrides embedded)
        foreach (var dir in LensDirectories)
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.GetFiles(dir, "*.lens.yaml"))
            {
                try
                {
                    var yaml = File.ReadAllText(file);
                    LoadLensYaml(yaml, file, deserializer);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load lens {file}: {ex.Message}");
                }
            }
        }
    }

    private void LoadLensYaml(string yaml, string source, IDeserializer deserializer)
    {
        var manifest = deserializer.Deserialize<LensYamlManifest>(yaml);
        if (manifest == null || string.IsNullOrWhiteSpace(manifest.Name)) return;
        if (!manifest.Enabled) return;

        var vibe = ConvertLensToVibe(manifest);
        _lensVibes[vibe.Name] = vibe;
    }

    /// <summary>
    /// Resolve a vibe name (or arbitrary text) into a full <see cref="Vibe"/>.
    /// Priority: lens YAML → config vibes → custom text.
    /// </summary>
    public Vibe Resolve(string vibeNameOrText)
    {
        if (string.IsNullOrWhiteSpace(vibeNameOrText))
            vibeNameOrText = "neutral";

        // 1. Check lens-backed vibes
        if (_lensVibes.TryGetValue(vibeNameOrText, out var lensVibe))
            return lensVibe;

        // 2. Check config vibes dictionary
        if (_configVibes.TryGetValue(vibeNameOrText, out var configPrompt))
        {
            return new Vibe
            {
                Name = vibeNameOrText,
                Prompt = configPrompt,
                SearchQualifier = GetBuiltinSearchQualifier(vibeNameOrText),
                RepresentativeText = GetBuiltinRepresentativeText(vibeNameOrText)
            };
        }

        // 3. Arbitrary text — use as direct instruction
        return new Vibe
        {
            Name = vibeNameOrText,
            Prompt = $"Apply this tone/perspective: {vibeNameOrText}. Filter and present content through this lens.",
            SearchQualifier = $"{vibeNameOrText} articles stories about",
            RepresentativeText = vibeNameOrText
        };
    }

    private static Vibe ConvertLensToVibe(LensYamlManifest manifest)
    {
        // Build the vibe prompt from the lens personality section
        var promptParts = new List<string>();

        if (manifest.Personality != null)
        {
            if (!string.IsNullOrEmpty(manifest.Personality.Tone))
                promptParts.Add($"Tone: {manifest.Personality.Tone}.");

            if (!string.IsNullOrEmpty(manifest.Personality.Persona))
                promptParts.Add($"Write as {manifest.Personality.Persona}.");

            if (manifest.Personality.StyleNotes?.Count > 0)
                promptParts.AddRange(manifest.Personality.StyleNotes);

            if (!string.IsNullOrEmpty(manifest.Personality.SpellingVariant))
                promptParts.Add($"Use {manifest.Personality.SpellingVariant} spelling.");
        }

        // Fall back to description if no personality configured
        var prompt = promptParts.Count > 0
            ? string.Join(" ", promptParts)
            : manifest.Description;

        // Build search qualifier from taxonomy
        string? searchQualifier = null;
        if (manifest.Taxonomy != null)
        {
            searchQualifier = manifest.Taxonomy.Domain switch
            {
                "blog" => "interesting accessible developments in",
                "technical" => "technical engineering implementation details about",
                "research" => "research papers findings methodology about",
                "legal" => "legal regulatory compliance aspects of",
                _ => $"{manifest.Taxonomy.Domain} content about"
            };
        }

        // Build representative text from taxonomy + tags
        var repParts = new List<string>();
        if (manifest.Taxonomy != null)
        {
            repParts.Add(manifest.Taxonomy.Domain);
            repParts.Add(manifest.Taxonomy.Style);
        }
        if (manifest.Tags?.Count > 0)
            repParts.AddRange(manifest.Tags);

        return new Vibe
        {
            Name = manifest.Name,
            Prompt = prompt,
            SearchQualifier = searchQualifier,
            RepresentativeText = repParts.Count > 0
                ? string.Join(" ", repParts)
                : null,
            LensId = manifest.Name,
            Tone = manifest.Personality?.Tone,
            SpellingVariant = manifest.Personality?.SpellingVariant,
            Persona = manifest.Personality?.Persona,
            StyleNotes = manifest.Personality?.StyleNotes?.AsReadOnly(),
            PhrasePreferences = manifest.Personality?.PhrasePreferences != null
                ? new Dictionary<string, string>(manifest.Personality.PhrasePreferences)
                : null
        };
    }

    /// <summary>
    /// Built-in search qualifiers for predefined vibes.
    /// Matches the existing logic in ScrollCommand.Helpers.
    /// </summary>
    private static string? GetBuiltinSearchQualifier(string vibe)
    {
        return vibe.ToLowerInvariant() switch
        {
            "doom" => "concerning problems risks issues in",
            "hopeful" => "positive breakthrough innovative upbeat news about",
            "snarky" => "controversial debate criticism of",
            "funny" => "amusing funny quirky weird news about",
            "upbeat" => "exciting breakthrough innovative new developments in",
            "friendly" => "interesting accessible developments in",
            "toon" => "dramatic wild surprising outrageous news about",
            "neutral" => null,
            _ => null
        };
    }

    /// <summary>
    /// Built-in representative text for predefined vibes.
    /// Matches the existing logic in ScrollCommand.Helpers.
    /// </summary>
    private static string? GetBuiltinRepresentativeText(string vibe)
    {
        return vibe.ToLowerInvariant() switch
        {
            "doom" => "security vulnerability breach layoffs downturn recession failure risk crisis warning concerning problem threat",
            "hopeful" => "innovation breakthrough positive growth opportunity success launch improvement exciting new achievement progress",
            "snarky" => "hype overrated controversy debate criticism reality check failure ironic absurd",
            "funny" => "amusing hilarious quirky bizarre unexpected weird funny comedy absurd entertaining",
            "upbeat" => "exciting amazing breakthrough launch success win achievement celebration progress incredible",
            "friendly" => "helpful accessible useful interesting development community collaboration knowledge",
            "toon" => "dramatic explosive wild surprising outrageous spectacular unbelievable crisis triumph adventure",
            "neutral" => "technology software engineering development news",
            _ => null
        };
    }

    /// <summary>
    /// Minimal YAML model for reading lens personality from .lens.yaml files.
    /// Only deserializes the fields relevant to vibe resolution —
    /// scoring, templates, and styles are ignored here (used only by LucidRAG).
    /// </summary>
    private sealed class LensYamlManifest
    {
        [YamlMember(Alias = "name")]
        public string Name { get; set; } = "";

        [YamlMember(Alias = "display_name")]
        public string DisplayName { get; set; } = "";

        [YamlMember(Alias = "description")]
        public string Description { get; set; } = "";

        [YamlMember(Alias = "enabled")]
        public bool Enabled { get; set; } = true;

        [YamlMember(Alias = "personality")]
        public LensPersonality? Personality { get; set; }

        [YamlMember(Alias = "taxonomy")]
        public LensTaxonomy? Taxonomy { get; set; }

        [YamlMember(Alias = "tags")]
        public List<string>? Tags { get; set; }
    }

    private sealed class LensPersonality
    {
        [YamlMember(Alias = "tone")]
        public string? Tone { get; set; }

        [YamlMember(Alias = "spelling_variant")]
        public string? SpellingVariant { get; set; }

        [YamlMember(Alias = "persona")]
        public string? Persona { get; set; }

        [YamlMember(Alias = "style_notes")]
        public List<string>? StyleNotes { get; set; }

        [YamlMember(Alias = "phrase_preferences")]
        public Dictionary<string, string>? PhrasePreferences { get; set; }
    }

    private sealed class LensTaxonomy
    {
        [YamlMember(Alias = "domain")]
        public string Domain { get; set; } = "general";

        [YamlMember(Alias = "style")]
        public string Style { get; set; } = "conversational";

        [YamlMember(Alias = "audience")]
        public string Audience { get; set; } = "general";
    }
}
