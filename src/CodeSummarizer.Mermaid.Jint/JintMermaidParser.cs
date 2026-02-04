using CodeSummarizer.Models;
using CodeSummarizer.Parsing;
using Jint.Runtime;
using Microsoft.Extensions.Logging;

namespace CodeSummarizer.Mermaid.Jint;

/// <summary>
///     Mermaid parser that uses Jint (pure .NET JavaScript interpreter) to run
///     mermaid.js in-process for 100% spec-conformant parsing.
///     Falls back to <see cref="RegexMermaidParser" /> on any failure.
/// </summary>
public sealed class JintMermaidParser : IMermaidParser, IDisposable
{
    private readonly IMermaidParser _fallback;
    private readonly ILogger _logger;
    private readonly JintEnginePool _pool;

    public JintMermaidParser(ILogger<JintMermaidParser> logger, IMermaidParser? fallback = null, int? poolSize = null)
    {
        _logger = logger;
        _fallback = fallback ?? new RegexMermaidParser();
        _pool = new JintEnginePool(logger, poolSize);
    }

    public void Dispose()
    {
        _pool.Dispose();
    }

    /// <inheritdoc />
    public MermaidSummary Parse(string mermaidCode)
    {
        if (string.IsNullOrWhiteSpace(mermaidCode))
            return _fallback.Parse(mermaidCode);

        // If the bundle didn't load, go straight to fallback
        if (!_pool.IsBundleLoaded)
            return _fallback.Parse(mermaidCode);

        var engine = _pool.Rent();
        try
        {
            var result = engine.Invoke("MermaidParser.parse", mermaidCode);

            if (result.Type is Types.Undefined or Types.Null)
                // parse() succeeded (no exception) but returned nothing — syntax is valid
                return MermaidDataExtractor.CreateValidationOnlySummary(mermaidCode);

            return MermaidDataExtractor.Extract(result, mermaidCode);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Jint mermaid parse failed, falling back to regex");
            return _fallback.Parse(mermaidCode);
        }
        finally
        {
            _pool.Return(engine);
        }
    }
}