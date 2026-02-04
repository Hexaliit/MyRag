using Jint;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSummarizer.Mermaid.Jint.Tests;

/// <summary>
///     Tests for <see cref="JintEnginePool" /> — concurrent access, pool sizing, disposal.
/// </summary>
public class JintEnginePoolTests
{
    [Fact]
    public void Rent_ReturnsWorkingEngine()
    {
        using var pool = new JintEnginePool(NullLogger<JintMermaidParser>.Instance, 2);

        var engine = pool.Rent();
        engine.Should().NotBeNull();

        // Engine should be able to evaluate basic JS
        var result = engine.Evaluate("1 + 1");
        result.ToString().Should().Be("2");

        pool.Return(engine);
    }

    [Fact]
    public void RentReturn_MultipleEngines_WorkConcurrently()
    {
        using var pool = new JintEnginePool(NullLogger<JintMermaidParser>.Instance, 4);

        var engines = new List<Engine>();
        // Rent multiple engines
        for (var i = 0; i < 4; i++)
        {
            var engine = pool.Rent();
            engine.Should().NotBeNull();
            engines.Add(engine);
        }

        // Return all
        foreach (var engine in engines)
            pool.Return(engine);
    }

    [Fact]
    public void Dispose_PreventsRent()
    {
        var pool = new JintEnginePool(NullLogger<JintMermaidParser>.Instance, 2);
        pool.Dispose();

        var act = () => pool.Rent();
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void IsBundleLoaded_ReturnsTrueWhenBundleExists()
    {
        using var pool = new JintEnginePool(NullLogger<JintMermaidParser>.Instance);

        // Bundle should be loaded from embedded resource
        pool.IsBundleLoaded.Should().BeTrue("mermaid-parser-bundle.js should be embedded in the assembly");
    }

    [Fact]
    public async Task ConcurrentAccess_DoesNotCorrupt()
    {
        using var pool = new JintEnginePool(NullLogger<JintMermaidParser>.Instance, 4);

        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            var engine = pool.Rent();
            try
            {
                var result = engine.Evaluate("2 * 3");
                result.ToString().Should().Be("6");
            }
            finally
            {
                pool.Return(engine);
            }
        }));

        await Task.WhenAll(tasks);
    }
}