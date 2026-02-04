using System.Collections.Concurrent;
using Mostlylucid.Summarizer.Core.Analysis;

namespace LucidRAG.Coordination;

/// <summary>
///     Manages concurrency lanes for wave execution.
///     Each lane has a semaphore limiting parallel execution.
/// </summary>
public sealed class LaneManager : IDisposable
{
    private readonly CoordinatorProfile _profile;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();

    public LaneManager(CoordinatorProfile profile)
    {
        _profile = profile;
    }

    public void Dispose()
    {
        foreach (var semaphore in _semaphores.Values)
            semaphore.Dispose();
    }

    /// <summary>
    ///     Acquire a slot in the named lane. Blocks until a slot is available.
    /// </summary>
    public async Task AcquireAsync(string laneName, CancellationToken ct = default)
    {
        var semaphore = GetSemaphore(laneName);
        await semaphore.WaitAsync(ct);
    }

    /// <summary>
    ///     Release a slot in the named lane.
    /// </summary>
    public void Release(string laneName)
    {
        if (_semaphores.TryGetValue(laneName, out var semaphore))
            semaphore.Release();
    }

    private SemaphoreSlim GetSemaphore(string laneName)
    {
        return _semaphores.GetOrAdd(laneName, name =>
        {
            var maxConcurrency = _profile.Lanes.TryGetValue(name, out var lane)
                ? lane.MaxConcurrency
                : LaneConfig.Fast.MaxConcurrency;

            return new SemaphoreSlim(maxConcurrency, maxConcurrency);
        });
    }
}