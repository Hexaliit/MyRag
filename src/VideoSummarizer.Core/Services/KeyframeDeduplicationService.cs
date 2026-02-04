using System.Numerics;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace VideoSummarizer.Core.Services;

/// <summary>
///     Fast perceptual hash-based keyframe deduplication.
///     Computes difference hashes (dHash) to identify visually similar frames
///     before expensive ML analysis.
/// </summary>
public class KeyframeDeduplicationService
{
    // dHash parameters: 9x8 grayscale = 64 bits
    private const int HashWidth = 9;
    private const int HashHeight = 8;

    // Hamming distance threshold - frames within this distance are considered duplicates
    // Lower = stricter (fewer duplicates), Higher = looser (more duplicates)
    private const int DefaultHammingThreshold = 10;
    private readonly ILogger<KeyframeDeduplicationService> _logger;

    public KeyframeDeduplicationService(ILogger<KeyframeDeduplicationService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///     Filter out visually similar frames from the candidate list.
    ///     Returns only unique frames that should receive full analysis.
    /// </summary>
    /// <param name="framePaths">Dictionary of timestamp -> frame path</param>
    /// <param name="hammingThreshold">Max Hamming distance to consider as duplicate (default 10)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Filtered list of unique frame candidates</returns>
    public async Task<List<KeyframeCandidate>> FilterSimilarFramesAsync(
        Dictionary<double, string> framePaths,
        int hammingThreshold = DefaultHammingThreshold,
        CancellationToken ct = default)
    {
        var candidates = new List<KeyframeCandidate>();
        var seenHashes = new List<(double Timestamp, ulong DHash)>();

        // Sort by timestamp for temporal ordering
        var sortedFrames = framePaths.OrderBy(k => k.Key).ToList();

        _logger.LogInformation(
            "Deduplicating {Count} keyframes with Hamming threshold {Threshold}",
            sortedFrames.Count, hammingThreshold);

        foreach (var (timestamp, path) in sortedFrames)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // Compute fast dHash
                var dHash = await ComputeDHashAsync(path, ct);

                // Check against recent hashes (only need to check recent frames for temporal locality)
                var recentHashes = seenHashes.TakeLast(20).ToList();
                var isDuplicate = recentHashes.Any(seen =>
                    HammingDistance(dHash, seen.DHash) <= hammingThreshold);

                if (!isDuplicate)
                {
                    candidates.Add(new KeyframeCandidate
                    {
                        Timestamp = timestamp,
                        FramePath = path,
                        DHash = dHash
                    });
                    seenHashes.Add((timestamp, dHash));
                }
                else
                {
                    _logger.LogDebug(
                        "Skipping duplicate frame at {Timestamp:F2}s (similar to recent frame)",
                        timestamp);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compute hash for frame at {Timestamp:F2}s, including anyway",
                    timestamp);
                // Include frame if hashing fails
                candidates.Add(new KeyframeCandidate
                {
                    Timestamp = timestamp,
                    FramePath = path,
                    DHash = 0
                });
            }
        }

        var skipped = sortedFrames.Count - candidates.Count;
        var skipPercent = sortedFrames.Count > 0 ? 100.0 * skipped / sortedFrames.Count : 0;

        _logger.LogInformation(
            "Deduplication complete: {Original} -> {Unique} frames ({Skipped} duplicates, {Percent:F1}% reduction)",
            sortedFrames.Count, candidates.Count, skipped, skipPercent);

        return candidates;
    }

    /// <summary>
    ///     Compute difference hash (dHash) for an image.
    ///     Uses 9x8 grayscale comparison producing 64-bit hash.
    ///     Very fast and effective for near-duplicate detection.
    /// </summary>
    public async Task<ulong> ComputeDHashAsync(string imagePath, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            using var image = Image.Load<Rgba32>(imagePath);

            // Resize to 9x8 (one extra column for gradient comparison)
            image.Mutate(x => x
                .Resize(HashWidth, HashHeight)
                .Grayscale());

            ulong hash = 0;
            var bit = 0;

            // Compare adjacent pixels horizontally
            for (var y = 0; y < HashHeight; y++)
            for (var x = 0; x < HashWidth - 1; x++)
            {
                var left = image[x, y].R;
                var right = image[x + 1, y].R;

                // Set bit if left pixel is brighter than right
                if (left > right) hash |= 1UL << bit;
                bit++;
            }

            return hash;
        }, ct);
    }

    /// <summary>
    ///     Calculate Hamming distance between two hashes.
    ///     Returns the number of differing bits.
    /// </summary>
    public static int HammingDistance(ulong a, ulong b)
    {
        return BitOperations.PopCount(a ^ b);
    }

    /// <summary>
    ///     Calculate similarity percentage between two hashes.
    /// </summary>
    public static double HashSimilarity(ulong a, ulong b)
    {
        var distance = HammingDistance(a, b);
        return 1.0 - distance / 64.0;
    }
}

/// <summary>
///     Candidate keyframe after deduplication filtering.
/// </summary>
public record KeyframeCandidate
{
    public double Timestamp { get; init; }
    public string FramePath { get; init; } = "";
    public ulong DHash { get; init; }
}