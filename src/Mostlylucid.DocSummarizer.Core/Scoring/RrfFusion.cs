namespace Mostlylucid.DocSummarizer.Scoring;

/// <summary>
/// A single ranked signal for RRF fusion.
/// Contains an ordered list of items (best-first) and a weight.
/// </summary>
public readonly record struct RankedSignal<T>(
    IReadOnlyList<T> Items,
    double Weight = 1.0);

/// <summary>
/// Result of RRF fusion for a single item.
/// </summary>
public readonly record struct FusedScore<T>(
    T Item,
    double Score);

/// <summary>
/// Generic Reciprocal Rank Fusion (Cormack et al., 2009).
/// Combines multiple ranked lists into a single fused ranking using:
///   RRF(d) = Σ weight_i × 1/(k + rank_i(d))
/// where k=60 is the standard smoothing constant.
/// </summary>
public static class RrfFusion
{
    /// <summary>
    /// Default RRF smoothing constant (Cormack et al., 2009).
    /// </summary>
    public const int DefaultK = 60;

    /// <summary>
    /// Fuse multiple ranked signal lists into a single score per item.
    /// Items are identified across lists by a key function.
    /// </summary>
    /// <typeparam name="T">Item type.</typeparam>
    /// <param name="signals">Ranked signal lists with weights.</param>
    /// <param name="keySelector">Extracts a unique key per item.</param>
    /// <param name="k">RRF smoothing constant (default 60).</param>
    /// <param name="normalize">If true, normalize scores to [0, 1].</param>
    /// <returns>Items sorted by descending fused score.</returns>
    public static List<FusedScore<T>> Fuse<T>(
        IEnumerable<RankedSignal<T>> signals,
        Func<T, string> keySelector,
        int k = DefaultK,
        bool normalize = false)
    {
        var scores = new Dictionary<string, (T Item, double Score)>();

        foreach (var signal in signals)
        {
            if (signal.Weight <= 0) continue;

            for (var rank = 0; rank < signal.Items.Count; rank++)
            {
                var item = signal.Items[rank];
                var key = keySelector(item);
                var contribution = signal.Weight * (1.0 / (k + rank + 1));

                if (scores.TryGetValue(key, out var existing))
                {
                    scores[key] = (existing.Item, existing.Score + contribution);
                }
                else
                {
                    scores[key] = (item, contribution);
                }
            }
        }

        if (scores.Count == 0)
            return [];

        var results = scores.Values
            .OrderByDescending(x => x.Score)
            .ToList();

        if (!normalize)
        {
            return results
                .Select(x => new FusedScore<T>(x.Item, x.Score))
                .ToList();
        }

        var maxScore = results[0].Score;
        return maxScore > 0
            ? results.Select(x => new FusedScore<T>(x.Item, x.Score / maxScore)).ToList()
            : results.Select(x => new FusedScore<T>(x.Item, 0)).ToList();
    }

    /// <summary>
    /// Fuse multiple ranked lists, returning only the top N results.
    /// </summary>
    public static List<FusedScore<T>> FuseTopN<T>(
        IEnumerable<RankedSignal<T>> signals,
        Func<T, string> keySelector,
        int topN,
        int k = DefaultK,
        bool normalize = false)
    {
        var all = Fuse(signals, keySelector, k, normalize);
        return all.Count <= topN ? all : all.GetRange(0, topN);
    }
}
