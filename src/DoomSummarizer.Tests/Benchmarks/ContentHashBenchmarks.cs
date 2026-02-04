using System.Diagnostics;
using DoomSummarizer.Services;
using Mostlylucid.Summarizer.Core.Utilities;
using Xunit.Abstractions;

namespace DoomSummarizer.Tests.Benchmarks;

/// <summary>
///     Benchmarks for XxHash64-based ContentHasher used for content cache comparison.
///     Validates throughput, allocation efficiency, and collision resistance.
/// </summary>
public class ContentHashBenchmarks(ITestOutputHelper output)
{
    // ── B1: Throughput — hash 10,000 strings of various sizes ──────────

    [Fact]
    public void B1_Throughput_100CharStrings()
    {
        const int count = 10_000;
        var strings = GenerateStrings(count, 100);

        // Warm up
        ContentHasher.ComputeHash(strings[0]);

        var sw = Stopwatch.StartNew();

        for (var i = 0; i < count; i++)
            ContentHasher.ComputeHash(strings[i]);

        sw.Stop();

        var opsPerSec = count / sw.Elapsed.TotalSeconds;
        output.WriteLine($"100-char strings: {opsPerSec:N0} hashes/sec ({count} in {sw.ElapsedMilliseconds}ms)");

        opsPerSec.Should().BeGreaterThan(100_000,
            "XxHash64 on 100-char strings should exceed 100K ops/sec");
    }

    [Fact]
    public void B1_Throughput_1KBStrings()
    {
        const int count = 10_000;
        var strings = GenerateStrings(count, 1024);

        // Warm up
        ContentHasher.ComputeHash(strings[0]);

        var sw = Stopwatch.StartNew();

        for (var i = 0; i < count; i++)
            ContentHasher.ComputeHash(strings[i]);

        sw.Stop();

        var opsPerSec = count / sw.Elapsed.TotalSeconds;
        output.WriteLine($"1KB strings: {opsPerSec:N0} hashes/sec ({count} in {sw.ElapsedMilliseconds}ms)");

        opsPerSec.Should().BeGreaterThan(50_000,
            "XxHash64 on 1KB strings should exceed 50K ops/sec");
    }

    [Fact]
    public void B1_Throughput_10KBStrings()
    {
        const int count = 10_000;
        var strings = GenerateStrings(count, 10 * 1024);

        // Warm up
        ContentHasher.ComputeHash(strings[0]);

        var sw = Stopwatch.StartNew();

        for (var i = 0; i < count; i++)
            ContentHasher.ComputeHash(strings[i]);

        sw.Stop();

        var opsPerSec = count / sw.Elapsed.TotalSeconds;
        output.WriteLine($"10KB strings: {opsPerSec:N0} hashes/sec ({count} in {sw.ElapsedMilliseconds}ms)");

        opsPerSec.Should().BeGreaterThan(10_000,
            "XxHash64 on 10KB strings should exceed 10K ops/sec");
    }

    [Fact]
    public void B1_Throughput_100KBStrings()
    {
        const int count = 10_000;
        var strings = GenerateStrings(count, 100 * 1024);

        // Warm up
        ContentHasher.ComputeHash(strings[0]);

        var sw = Stopwatch.StartNew();

        for (var i = 0; i < count; i++)
            ContentHasher.ComputeHash(strings[i]);

        sw.Stop();

        var opsPerSec = count / sw.Elapsed.TotalSeconds;
        output.WriteLine($"100KB strings: {opsPerSec:N0} hashes/sec ({count} in {sw.ElapsedMilliseconds}ms)");

        opsPerSec.Should().BeGreaterThan(1_000,
            "XxHash64 on 100KB strings should exceed 1K ops/sec");
    }

    // ── B2: Zero allocation for small strings ──────────────────────────

    [Fact]
    public void B2_SmallString_Allocation()
    {
        var smallString = new string('x', 100);

        // Warm up — let JIT compile and stabilise
        for (var i = 0; i < 100; i++)
            ContentHasher.ComputeHash(smallString);

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 1000; i++)
            ContentHasher.ComputeHash(smallString);

        var allocAfter = GC.GetAllocatedBytesForCurrentThread();
        var allocBytes = allocAfter - allocBefore;
        var bytesPerOp = allocBytes / 1000.0;

        output.WriteLine($"ComputeHash (100-char × 1000): {allocBytes:N0} total bytes, " +
                         $"{bytesPerOp:F0} bytes/op");

        // XxHash64 allocates a byte[] for UTF8 encoding + byte[] for result + hex string.
        // Per-op allocation should be minimal: UTF8 byte array + 8-byte hash + 16-char string.
        // For a 100-char ASCII string that is ~100 (UTF8) + 8 (hash) + ~48 (string obj) bytes.
        bytesPerOp.Should().BeLessThan(512,
            "per-hash allocation should be bounded by UTF8 byte[] + hash byte[] + hex string");
    }

    [Fact]
    public void B2_EmptyAndNull_NoAllocation()
    {
        // Warm up
        ContentHasher.ComputeHash("");
        ContentHasher.ComputeHash((string)null!);

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 1000; i++)
        {
            ContentHasher.ComputeHash("");
            ContentHasher.ComputeHash((string)null!);
        }

        var allocAfter = GC.GetAllocatedBytesForCurrentThread();
        var allocBytes = allocAfter - allocBefore;

        output.WriteLine($"Empty/null × 2000: {allocBytes:N0} bytes");

        allocBytes.Should().BeLessThan(1024,
            "empty/null fast-path returns string.Empty with zero allocation");
    }

    // ── B3: Collision resistance — 100K unique strings, no collisions ──

    [Fact]
    public void B3_CollisionResistance_100K_UniqueStrings()
    {
        const int count = 100_000;
        var hashes = new HashSet<string>(count);
        var collisions = 0;

        var sw = Stopwatch.StartNew();

        for (var i = 0; i < count; i++)
        {
            // Generate unique strings with varying structure
            var content = $"document-{i}-content-{i * 7}-extra-{i ^ 0xDEADBEEF}";
            var hash = ContentHasher.ComputeHash(content);

            if (!hashes.Add(hash))
                collisions++;
        }

        sw.Stop();

        output.WriteLine($"Collision test: {count:N0} unique strings, {collisions} collisions, " +
                         $"{hashes.Count:N0} unique hashes in {sw.ElapsedMilliseconds}ms");

        collisions.Should().Be(0,
            "XxHash64 should produce zero collisions across 100K unique strings");
        hashes.Count.Should().Be(count);
    }

    [Fact]
    public void B3_CollisionResistance_SimilarStrings()
    {
        // Test with strings that differ by a single character — stresses avalanche effect
        const int count = 100_000;
        var baseContent = new string('A', 500);
        var hashes = new HashSet<string>(count);
        var collisions = 0;

        for (var i = 0; i < count; i++)
        {
            // Modify a single position in the base string
            var chars = baseContent.ToCharArray();
            var pos = i % chars.Length;
            chars[pos] = (char)('A' + i / chars.Length % 26);
            // Also append the index to guarantee uniqueness
            var content = new string(chars) + i;
            var hash = ContentHasher.ComputeHash(content);

            if (!hashes.Add(hash))
                collisions++;
        }

        output.WriteLine($"Similar strings: {count:N0} inputs, {collisions} collisions, " +
                         $"{hashes.Count:N0} unique hashes");

        collisions.Should().Be(0,
            "XxHash64 avalanche property should avoid collisions on similar strings");
    }

    // ── B4: Hash format and determinism ────────────────────────────────

    [Fact]
    public void B4_HashFormat_16CharLowercaseHex()
    {
        var hash = ContentHasher.ComputeHash("test content for format validation");

        output.WriteLine($"Hash: '{hash}' (length={hash.Length})");

        hash.Should().HaveLength(16, "XxHash64 produces 8 bytes = 16 hex chars");
        hash.Should().MatchRegex("^[0-9a-f]{16}$",
            "hash should be lowercase hex characters only");
    }

    [Fact]
    public void B4_HashDeterminism()
    {
        const string content = "deterministic content check";

        var hash1 = ContentHasher.ComputeHash(content);
        var hash2 = ContentHasher.ComputeHash(content);
        var hash3 = ContentHasher.ComputeHash(content);

        hash1.Should().Be(hash2);
        hash2.Should().Be(hash3);

        output.WriteLine($"Deterministic hash: '{hash1}'");
    }

    // ── B5: ItemProcessor.ComputeContentHash delegation ────────────────

    [Fact]
    public void B5_ItemProcessor_DelegatesToContentHasher()
    {
        var content = "ItemProcessor delegation test content";

        var directHash = ContentHasher.ComputeHash(content);
        var delegatedHash = ItemProcessor.ComputeContentHash(content);

        output.WriteLine($"ContentHasher:  '{directHash}'");
        output.WriteLine($"ItemProcessor:  '{delegatedHash}'");

        delegatedHash.Should().Be(directHash,
            "ItemProcessor.ComputeContentHash must delegate to ContentHasher.ComputeHash");
    }

    [Fact]
    public void B5_ItemProcessor_DelegationThroughput()
    {
        const int count = 10_000;
        var strings = GenerateStrings(count, 200);

        // Warm up
        ItemProcessor.ComputeContentHash(strings[0]);
        ContentHasher.ComputeHash(strings[0]);

        var swDirect = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
            ContentHasher.ComputeHash(strings[i]);
        swDirect.Stop();

        var swDelegated = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
            ItemProcessor.ComputeContentHash(strings[i]);
        swDelegated.Stop();

        output.WriteLine($"ContentHasher direct:    {swDirect.ElapsedMilliseconds}ms");
        output.WriteLine($"ItemProcessor delegated: {swDelegated.ElapsedMilliseconds}ms");

        // Delegation overhead should be negligible — within 2x of direct call
        swDelegated.ElapsedMilliseconds.Should().BeLessThan(
            Math.Max(swDirect.ElapsedMilliseconds * 2, 100),
            "delegation overhead should be negligible");
    }

    [Fact]
    public void B5_ItemProcessor_EdgeCases()
    {
        // Empty string
        ItemProcessor.ComputeContentHash("").Should().Be(ContentHasher.ComputeHash(""));

        // Single character
        ItemProcessor.ComputeContentHash("x").Should().Be(ContentHasher.ComputeHash("x"));

        // Unicode content
        var unicode = "Unicode: \u00e9\u00e8\u00ea \u00fc\u00f6\u00e4 \u4e16\u754c \ud83d\ude80";
        ItemProcessor.ComputeContentHash(unicode).Should().Be(ContentHasher.ComputeHash(unicode));

        output.WriteLine("Edge cases: empty, single-char, unicode all match");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    ///     Generate an array of unique strings of approximately the given size.
    ///     Uses a seeded RNG for deterministic test data.
    /// </summary>
    private static string[] GenerateStrings(int count, int approximateLength)
    {
        var rng = new Random(42);
        var result = new string[count];

        for (var i = 0; i < count; i++)
        {
            var chars = new char[approximateLength];
            for (var j = 0; j < approximateLength; j++)
                chars[j] = (char)('a' + rng.Next(26));
            // Embed the index to guarantee uniqueness
            var prefix = i.ToString();
            prefix.AsSpan().CopyTo(chars.AsSpan());
            result[i] = new string(chars);
        }

        return result;
    }
}