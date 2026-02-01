using System.Reflection;
using DoomSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;
using Spectre.Console;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DoomSummarizer.Services;

/// <summary>
/// Fetches documentation from GitHub (or local repo fallback), converts to
/// ContentItems, and indexes via the existing pipeline. The manual corpus
/// uses the reserved source tag "manual".
/// </summary>
public sealed class ManualLoader
{
    public const string ManualSource = "manual";

    private readonly StorageService _storage;
    private readonly IEmbeddingService _embedding;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public ManualLoader(StorageService storage, IEmbeddingService embedding)
    {
        _storage = storage;
        _embedding = embedding;
    }

    /// <summary>
    /// Check whether the manual corpus has already been indexed.
    /// </summary>
    public async Task<bool> IsManualLoadedAsync()
    {
        var items = await _storage.GetRecentItemsAsync(days: 36500, source: ManualSource);
        return items.Count > 0;
    }

    /// <summary>
    /// Load the manual corpus: fetch docs, embed, and index them.
    /// </summary>
    public async Task LoadManualAsync(ItemProcessor processor, bool refresh, CancellationToken ct)
    {
        var manifest = LoadManifest();

        if (refresh)
        {
            AnsiConsole.MarkupLine("[grey]Clearing existing manual corpus...[/]");
            await _storage.DeleteItemsBySourceAsync(ManualSource);
        }

        AnsiConsole.MarkupLine($"[cyan]Loading manual: {manifest.Documents.Count} documents from {manifest.Name} v{manifest.Version}[/]");

        var loaded = 0;
        var failed = 0;

        foreach (var doc in manifest.Documents)
        {
            ct.ThrowIfCancellationRequested();

            var content = await FetchDocumentAsync(manifest.BaseUrl, doc.Path, ct);
            if (content == null)
            {
                AnsiConsole.MarkupLine($"[yellow]  skip: {doc.Title} (fetch failed)[/]");
                failed++;
                continue;
            }

            var item = new ContentItem
            {
                Id = $"manual:{doc.Path.Replace('/', '-').Replace('\\', '-')}",
                Source = ManualSource,
                Title = doc.Title,
                Url = $"{manifest.BaseUrl}/{doc.Path}",
                Content = content,
                Summary = content.Length > 300 ? content[..300] + "..." : content,
                IsEnriched = true,
                CreatedAt = DateTimeOffset.UtcNow,
                FetchedAt = DateTimeOffset.UtcNow,
                Tags = doc.Tags
            };

            // Compute embedding
            var textToEmbed = $"{item.Title} {item.Content}".Trim();
            if (textToEmbed.Length > 1000)
                textToEmbed = textToEmbed[..1000];
            item.Embedding = await _embedding.EmbedAsync(textToEmbed, ct);

            // Score and index
            processor.ScoreSentimentAndTopic(item);
            await processor.IndexItemAsync(item);

            loaded++;
            AnsiConsole.MarkupLine($"[green]  loaded: {doc.Title}[/]");
        }

        AnsiConsole.MarkupLine($"[cyan]Manual loaded: {loaded}/{manifest.Documents.Count} documents indexed ({failed} failed)[/]");
    }

    /// <summary>
    /// Load the manifest from the embedded resource.
    /// </summary>
    internal static ManualManifest LoadManifest()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "DoomSummarizer.Resources.manual.manifest.yaml";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        return deserializer.Deserialize<ManualManifest>(reader);
    }

    /// <summary>
    /// Fetch a document from GitHub raw URL, falling back to local repo path.
    /// </summary>
    private static async Task<string?> FetchDocumentAsync(string baseUrl, string path, CancellationToken ct)
    {
        // Try GitHub raw first
        var url = $"{baseUrl}/{path}";
        try
        {
            var response = await Http.GetAsync(url, ct);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            // Fall through to local
        }

        // Fall back to local repo path (relative to exe or known locations)
        var localPaths = new[]
        {
            // Relative to working directory
            Path.Combine(Environment.CurrentDirectory, path),
            // Relative to exe directory
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", path),
            // Common dev layout: repo root
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", path),
        };

        foreach (var localPath in localPaths)
        {
            var resolved = Path.GetFullPath(localPath);
            if (File.Exists(resolved))
            {
                try
                {
                    return await File.ReadAllTextAsync(resolved, ct);
                }
                catch
                {
                    // Try next path
                }
            }
        }

        return null;
    }
}
