using System.Collections.Immutable;
using System.Diagnostics;

namespace DoomSummarizer.Plugins;

/// <summary>
///     Registry for processor plugins.
///     Finds the best plugin for a given document based on content analysis.
///     Thread-safe: uses immutable snapshots for lock-free reads.
/// </summary>
public sealed class ProcessorPluginRegistry
{
    private readonly Dictionary<string, IProcessorPlugin> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private volatile ImmutableList<IProcessorPlugin> _all = [];
    private volatile ImmutableList<IDocumentReader>? _readerCache;

    /// <summary>All registered processor plugins.</summary>
    public IReadOnlyList<IProcessorPlugin> All => _all;

    /// <summary>Register a processor plugin.</summary>
    public void Register(IProcessorPlugin plugin)
    {
        lock (_lock)
        {
            _all = _all.Add(plugin);
            _readerCache = null; // Invalidate reader cache

            if (!_byName.TryAdd(plugin.Metadata.Name, plugin))
                Debug.WriteLine($"[ProcessorPluginRegistry] Warning: duplicate name '{plugin.Metadata.Name}'");
        }
    }

    /// <summary>Find a plugin by name.</summary>
    public IProcessorPlugin? FindByName(string name)
    {
        lock (_lock)
        {
            return _byName.GetValueOrDefault(name);
        }
    }

    /// <summary>
    ///     Find the best plugin for the given content.
    ///     Returns null if no plugin can handle it.
    /// </summary>
    public IProcessorPlugin? FindBestPlugin(string markdown, ProcessingContext context)
    {
        IProcessorPlugin? best = null;
        var bestScore = 0;

        foreach (var plugin in _all)
        {
            if (!plugin.CanProcess(markdown, context))
                continue;

            // Score by specificity: plugins that match document type get priority
            var score = 1;
            if (context.DocumentType != null &&
                plugin.Metadata.DocumentTypes.Contains(context.DocumentType, StringComparer.OrdinalIgnoreCase))
                score += 10;

            if (context.FileName != null)
            {
                var ext = Path.GetExtension(context.FileName);
                if (plugin.Metadata.SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    score += 5;
            }

            if (score > bestScore)
            {
                best = plugin;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>
    ///     Find all plugins that handle a given file extension.
    /// </summary>
    public IReadOnlyList<IProcessorPlugin> FindByExtension(string extension)
    {
        return _all.Where(p => p.Metadata.SupportedExtensions
            .Contains(extension, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Initialize all registered processor plugins.</summary>
    public async Task InitializeAllAsync(ProcessorPluginServices services, CancellationToken ct = default)
    {
        var failed = new List<string>();

        foreach (var plugin in _all)
            try
            {
                await plugin.InitializeAsync(services, ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[ProcessorPluginRegistry] Failed to initialize {plugin.Metadata.DisplayName}: {ex.Message}");
                failed.Add(plugin.Metadata.Name);
            }

        // Remove failed plugins so they don't get returned by FindBestPlugin
        if (failed.Count > 0)
            lock (_lock)
            {
                _all = _all.RemoveAll(p => failed.Contains(p.Metadata.Name));
                foreach (var name in failed)
                    _byName.Remove(name);
                _readerCache = null;
            }
    }

    /// <summary>
    ///     Collect all document readers from all registered plugins. Cached.
    /// </summary>
    public IReadOnlyList<IDocumentReader> GetAllReaders()
    {
        var cache = _readerCache;
        if (cache != null) return cache;

        var readers = _all.SelectMany(p => p.Readers).ToImmutableList();
        _readerCache = readers;
        return readers;
    }

    /// <summary>
    ///     Find the best reader for a file extension across all plugins.
    ///     Returns the reader with the highest priority.
    /// </summary>
    public IDocumentReader? FindReaderForExtension(string extension)
    {
        return GetAllReaders()
            .Where(r => r.SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Priority)
            .FirstOrDefault();
    }

    /// <summary>
    ///     All file extensions across all registered processor plugins.
    /// </summary>
    public IReadOnlySet<string> GetAllSupportedExtensions()
    {
        return _all.SelectMany(p => p.Metadata.SupportedExtensions)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}