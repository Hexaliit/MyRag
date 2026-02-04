using LucidRAG.Lenses;
using LucidRAG.Manifests;
using LensManifest = LucidRAG.Manifests.LensManifest;
using LensScoringConfig = LucidRAG.Lenses.LensScoringConfig;
using LensTemplatesConfig = LucidRAG.Lenses.LensTemplatesConfig;

namespace LucidRAG.Services.Lenses;

/// <summary>
///     Loads lens packages from YAML manifests.
///     Replaces the old JSON-based lens loader.
/// </summary>
public sealed class YamlLensLoader : ILensLoader
{
    private readonly ILogger<YamlLensLoader> _logger;
    private readonly IManifestLoader<LensManifest> _manifestLoader;

    public YamlLensLoader(
        IManifestLoader<LensManifest> manifestLoader,
        ILogger<YamlLensLoader> logger)
    {
        _manifestLoader = manifestLoader;
        _logger = logger;
    }

    public async Task<IReadOnlyList<LensPackage>> LoadFromDirectoryAsync(string directory,
        CancellationToken ct = default)
    {
        var manifests = await _manifestLoader.LoadAllAsync(ct);

        var packages = manifests
            .Where(m => m.Enabled)
            .Select(ConvertToLensPackage)
            .OrderByDescending(p => p.Manifest.Priority)
            .ToList();

        _logger.LogInformation("Loaded {Count} lens package(s) from YAML manifests", packages.Count);

        return packages;
    }

    public async Task<LensPackage?> LoadPackageAsync(string packagePath, CancellationToken ct = default)
    {
        // Extract lens name from path (e.g., "lenses/blog" -> "blog")
        var lensName = Path.GetFileName(packagePath);

        var manifest = await _manifestLoader.LoadByNameAsync(lensName, ct);
        if (manifest == null)
            return null;

        return ConvertToLensPackage(manifest);
    }

    private LensPackage ConvertToLensPackage(LensManifest manifest)
    {
        // Convert personality from YAML model to Lenses model
        LensPersonality? personality = null;
        if (manifest.Personality != null)
            personality = new LensPersonality(
                manifest.Personality.Tone,
                manifest.Personality.SpellingVariant,
                manifest.Personality.Persona,
                manifest.Personality.StyleNotes,
                manifest.Personality.PhrasePreferences
            );

        return new LensPackage
        {
            Manifest = new LucidRAG.Lenses.LensManifest(
                manifest.Name,
                manifest.DisplayName,
                manifest.Description,
                manifest.Version,
                manifest.Author,
                manifest.Priority,
                new LensScoringConfig(
                    manifest.Scoring.DenseWeight,
                    manifest.Scoring.Bm25Weight,
                    manifest.Scoring.SalienceWeight,
                    manifest.Scoring.FreshnessWeight
                ),
                new LensTemplatesConfig(
                    "inline", // Templates are inline in YAML
                    "inline",
                    manifest.Templates.Response != null ? "inline" : null
                ),
                manifest.Styles?.InlineCss != null || manifest.Styles?.CssFile != null ? "inline" : null,
                manifest.Defaults,
                personality
            ),
            BasePath = "", // Not needed for YAML-based lenses
            SystemPromptTemplate = manifest.Templates.SystemPrompt ?? "",
            CitationTemplate = manifest.Templates.Citation ?? "",
            ResponseTemplate = manifest.Templates.Response,
            Styles = manifest.Styles?.InlineCss ?? (manifest.Styles?.CssFile != null
                ? LoadCssFile(manifest.Styles.CssFile)
                : null)
        };
    }

    private string? LoadCssFile(string cssFile)
    {
        try
        {
            if (File.Exists(cssFile))
                return File.ReadAllText(cssFile);

            _logger.LogWarning("CSS file not found: {File}", cssFile);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading CSS file: {File}", cssFile);
            return null;
        }
    }
}