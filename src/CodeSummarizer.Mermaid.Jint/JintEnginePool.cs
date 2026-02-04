using System.Collections.Concurrent;
using System.Reflection;
using Jint;
using Microsoft.Extensions.Logging;

namespace CodeSummarizer.Mermaid.Jint;

/// <summary>
///     Thread-safe pool of Jint engines pre-loaded with mermaid.js.
///     Jint engines are not thread-safe, so each concurrent parse needs its own engine.
///     Engines are recycled via a ConcurrentBag for low-contention reuse.
/// </summary>
public sealed class JintEnginePool : IDisposable
{
    private readonly ILogger _logger;
    private readonly int _maxPoolSize;
    private readonly string? _mermaidBundle;
    private readonly string _polyfillScript;
    private readonly ConcurrentBag<Engine> _pool = new();
    private volatile bool _disposed;

    public JintEnginePool(ILogger logger, int? maxPoolSize = null)
    {
        _logger = logger;
        _maxPoolSize = maxPoolSize ?? Math.Min(Environment.ProcessorCount * 2, 16);
        _polyfillScript = MermaidDomStubs.GetPolyfill();
        _mermaidBundle = LoadMermaidBundle();

        if (_mermaidBundle == null)
            _logger.LogWarning(
                "Mermaid JS bundle not found as embedded resource — Jint parser will always fall back to regex");
    }

    /// <summary>
    ///     Whether the mermaid JS bundle was loaded successfully.
    /// </summary>
    public bool IsBundleLoaded => _mermaidBundle != null;

    public void Dispose()
    {
        _disposed = true;
        while (_pool.TryTake(out _))
        {
            // Engines don't implement IDisposable but clearing the pool allows GC
        }
    }

    /// <summary>
    ///     Rent an engine from the pool. Must be returned via <see cref="Return" />.
    /// </summary>
    public Engine Rent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pool.TryTake(out var engine))
            return engine;

        return CreateEngine();
    }

    /// <summary>
    ///     Return an engine to the pool for reuse.
    /// </summary>
    public void Return(Engine engine)
    {
        if (_disposed || _pool.Count >= _maxPoolSize)
            return; // Let it be GC'd

        _pool.Add(engine);
    }

    private Engine CreateEngine()
    {
        var engine = new Engine(options =>
        {
            options.TimeoutInterval(TimeSpan.FromSeconds(5));
            options.MaxStatements(500_000);
            options.LimitMemory(50 * 1024 * 1024); // 50 MB
            options.Strict(false);
        });

        // Load DOM polyfills first
        engine.Execute(_polyfillScript);

        // Load mermaid bundle
        if (_mermaidBundle != null)
            try
            {
                engine.Execute(_mermaidBundle);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load mermaid JS bundle into Jint engine");
            }

        return engine;
    }

    private string? LoadMermaidBundle()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("mermaid-parser-bundle.js", StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
            return null;

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            return null;

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}