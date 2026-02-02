using System.Diagnostics;
using LucidRAG.LLM.Services.LoadBalancing;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Tests.LoadBalancing;

/// <summary>
/// Performance benchmarks for load balancing components.
/// Uses xUnit [Fact] tests with Stopwatch timing (no BenchmarkDotNet dependency).
/// Each benchmark reports ops/sec and allocation bytes via test output.
/// </summary>
public class LoadBalancingBenchmarks(Xunit.Abstractions.ITestOutputHelper output)
{
    // ── B1: EndpointState throughput ──────────────────────────────────────

    [Fact]
    public void B1_RecordSuccess_Throughput()
    {
        const int iterations = 1_000_000;
        var state = new EndpointState("http://localhost:11434", "bench");

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
            state.RecordSuccess(50.0);

        sw.Stop();
        var allocAfter = GC.GetAllocatedBytesForCurrentThread();

        var opsPerSec = iterations / sw.Elapsed.TotalSeconds;
        output.WriteLine($"RecordSuccess: {opsPerSec:N0} ops/sec, {allocAfter - allocBefore:N0} bytes allocated");

        state.TotalRequests.Should().Be(iterations);
        opsPerSec.Should().BeGreaterThan(1_000_000, "RecordSuccess should exceed 1M ops/sec");
    }

    [Fact]
    public void B1_RecordSuccess_Contention()
    {
        const int threadsCount = 8;
        const int iterationsPerThread = 125_000;
        var state = new EndpointState("http://localhost:11434", "bench");

        var barrier = new Barrier(threadsCount);
        var sw = Stopwatch.StartNew();

        Parallel.For(0, threadsCount, _ =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < iterationsPerThread; i++)
                state.RecordSuccess(50.0);
        });

        sw.Stop();

        var totalOps = threadsCount * iterationsPerThread;
        var opsPerSec = totalOps / sw.Elapsed.TotalSeconds;
        output.WriteLine($"RecordSuccess (8 threads): {opsPerSec:N0} ops/sec");

        state.TotalRequests.Should().Be(totalOps);
    }

    // ── B2: Selector throughput ──────────────────────────────────────────

    [Fact]
    public void B2_RoundRobinSelector_Throughput()
    {
        const int iterations = 1_000_000;
        var selector = new RoundRobinSelector();
        var endpoints = CreateBenchEndpoints(5);

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
            selector.Select(endpoints);

        sw.Stop();
        var allocAfter = GC.GetAllocatedBytesForCurrentThread();
        var allocBytes = allocAfter - allocBefore;

        var opsPerSec = iterations / sw.Elapsed.TotalSeconds;
        output.WriteLine($"RoundRobinSelector.Select: {opsPerSec:N0} ops/sec, {allocBytes:N0} bytes allocated");

        opsPerSec.Should().BeGreaterThan(1_000_000, "RoundRobin should exceed 1M ops/sec");
        allocBytes.Should().BeLessThan(1024, "RoundRobin should be allocation-free");
    }

    [Fact]
    public void B2_FastestSelector_Throughput()
    {
        const int iterations = 1_000_000;
        var selector = new FastestSelector();
        var endpoints = CreateBenchEndpoints(5);

        // Give mixed EMA data: some with data, some without
        endpoints[0].RecordSuccess(100.0);
        endpoints[1].RecordSuccess(50.0);
        endpoints[2].RecordSuccess(200.0);
        // endpoints[3] and [4] have no data (EMA = MaxValue)

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
            selector.Select(endpoints);

        sw.Stop();
        var allocAfter = GC.GetAllocatedBytesForCurrentThread();
        var allocBytes = allocAfter - allocBefore;

        var opsPerSec = iterations / sw.Elapsed.TotalSeconds;
        output.WriteLine($"FastestSelector.Select: {opsPerSec:N0} ops/sec, {allocBytes:N0} bytes allocated");

        opsPerSec.Should().BeGreaterThan(1_000_000, "Fastest should exceed 1M ops/sec");
        allocBytes.Should().BeLessThan(1024, "Fastest should be allocation-free");
    }

    // ── B3: ExecuteWithFailover overhead ─────────────────────────────────

    [Fact]
    public async Task B3_HappyPath_Overhead()
    {
        const int iterations = 100_000;
        var (service, _) = CreateBenchService(3, new RoundRobinSelector());

        var sw = Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
            await service.GenerateAsync("test");

        sw.Stop();

        var opsPerSec = iterations / sw.Elapsed.TotalSeconds;
        output.WriteLine($"ExecuteWithFailover (happy path): {opsPerSec:N0} ops/sec");

        opsPerSec.Should().BeGreaterThan(100_000, "Happy path should exceed 100K ops/sec");

        service.Dispose();
    }

    [Fact]
    public async Task B3_FailoverPath_Overhead()
    {
        const int iterations = 100_000;
        // First endpoint always fails, second succeeds
        var endpoints = new List<EndpointState>
        {
            new("http://ep0:11434", "ep0"),
            new("http://ep1:11434", "ep1"),
            new("http://ep2:11434", "ep2"),
        };
        var services = new Dictionary<string, ILlmService>
        {
            ["http://ep0:11434"] = new FakeLlmService("ep0", shouldFail: true),
            ["http://ep1:11434"] = new FakeLlmService("ep1"),
            ["http://ep2:11434"] = new FakeLlmService("ep2"),
        };
        var sut = new LoadBalancedLlmService(
            "bench", endpoints, services,
            new FixedFirstSelector(),
            NullLogger<LoadBalancedLlmService>.Instance,
            healthCheckIntervalSeconds: 0);

        var sw = Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
            await sut.GenerateAsync("test");

        sw.Stop();

        var opsPerSec = iterations / sw.Elapsed.TotalSeconds;
        output.WriteLine($"ExecuteWithFailover (failover path): {opsPerSec:N0} ops/sec");

        opsPerSec.Should().BeGreaterThan(25_000, "Failover path should exceed 25K ops/sec");

        sut.Dispose();
    }

    // ── B4: Full LoadBalancedLlmService throughput ────────────────────────

    [Fact]
    public async Task B4_RoundRobin_Throughput()
    {
        const int iterations = 50_000;
        var (service, _) = CreateBenchService(3, new RoundRobinSelector());

        var sw = Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
            await service.GenerateAsync("test");

        sw.Stop();

        var opsPerSec = iterations / sw.Elapsed.TotalSeconds;
        output.WriteLine($"Full service (RoundRobin, 3 endpoints): {opsPerSec:N0} ops/sec");

        service.Dispose();
    }

    [Fact]
    public async Task B4_Fastest_Throughput()
    {
        const int iterations = 50_000;
        var (service, endpoints) = CreateBenchService(3, new FastestSelector());

        // Seed EMA data
        foreach (var ep in endpoints)
            ep.RecordSuccess(100.0);

        var sw = Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
            await service.GenerateAsync("test");

        sw.Stop();

        var opsPerSec = iterations / sw.Elapsed.TotalSeconds;
        output.WriteLine($"Full service (Fastest, 3 endpoints): {opsPerSec:N0} ops/sec");

        service.Dispose();
    }

    [Fact]
    public async Task B4_DirectCall_Baseline()
    {
        const int iterations = 50_000;
        var directService = new FakeLlmService("direct");

        var sw = Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
            await directService.GenerateAsync("test");

        sw.Stop();

        var opsPerSec = iterations / sw.Elapsed.TotalSeconds;
        output.WriteLine($"Direct FakeLlmService (baseline): {opsPerSec:N0} ops/sec");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static List<EndpointState> CreateBenchEndpoints(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new EndpointState($"http://ep{i}:11434", $"ep{i}"))
            .ToList();

    private static (LoadBalancedLlmService service, List<EndpointState> endpoints) CreateBenchService(
        int endpointCount,
        IEndpointSelector selector)
    {
        var endpoints = CreateBenchEndpoints(endpointCount);
        var services = endpoints.ToDictionary(
            e => e.Url,
            e => (ILlmService)new FakeLlmService(e.Name));

        var service = new LoadBalancedLlmService(
            "bench", endpoints, services, selector,
            NullLogger<LoadBalancedLlmService>.Instance,
            healthCheckIntervalSeconds: 0);

        return (service, endpoints);
    }

    /// <summary>
    /// Selector that always returns the first endpoint (to guarantee hitting
    /// a specific endpoint for failover benchmarks).
    /// </summary>
    private class FixedFirstSelector : IEndpointSelector
    {
        public EndpointState? Select(IReadOnlyList<EndpointState> endpoints) =>
            endpoints.Count > 0 ? endpoints[0] : null;
    }
}
