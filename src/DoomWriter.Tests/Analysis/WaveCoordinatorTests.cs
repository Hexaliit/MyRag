using Mostlylucid.Summarizer.Core.Analysis;

namespace DoomWriter.Tests.Analysis;

public class WaveCoordinatorTests
{
    // --- Basic execution ---

    [Fact]
    public async Task ExecuteAsync_RunsAllEnabledWaves()
    {
        var coordinator = new WaveCoordinator();
        coordinator.RegisterWaves([
            new CountingWave("wave1", priority: 90),
            new CountingWave("wave2", priority: 50)
        ]);

        var result = await coordinator.ExecuteAsync("test content");

        result.Signals.Should().HaveCount(4); // 2 signals per wave
        result.ExecutionLog.Should().HaveCount(2);
        result.ExecutionLog.Should().AllSatisfy(e => e.Status.Should().Be(WaveStatus.Success));
    }

    [Fact]
    public async Task ExecuteAsync_SkipsDisabledWaves()
    {
        var disabledWave = new CountingWave("disabled", priority: 90) { Enabled = false };
        var enabledWave = new CountingWave("enabled", priority: 50);

        var coordinator = new WaveCoordinator();
        coordinator.RegisterWaves([disabledWave, enabledWave]);

        var result = await coordinator.ExecuteAsync("test");

        result.Signals.Should().HaveCount(2); // Only from enabled wave
    }

    [Fact]
    public async Task ExecuteAsync_RespectsWavePriority()
    {
        var order = new List<string>();
        var coordinator = new WaveCoordinator();
        coordinator.RegisterWaves([
            new TrackingWave("low", priority: 10, order),
            new TrackingWave("high", priority: 90, order),
            new TrackingWave("mid", priority: 50, order)
        ]);

        // Use single-lane profile to force sequential execution
        var profile = new CoordinatorProfile
        {
            Name = "sequential",
            WaveTimeoutMs = 5000,
            Lanes = new()
            {
                ["fast"] = new LaneConfig { Name = "fast", MaxConcurrency = 1 }
            }
        };

        var result = await coordinator.ExecuteAsync("test", profile: profile);

        // Waves should execute in priority order: high (90), mid (50), low (10)
        order.Should().ContainInOrder("high", "mid", "low");
    }

    // --- Signal passing between waves ---

    [Fact]
    public async Task ExecuteAsync_WavesCanReadEarlierSignalsViaContext()
    {
        var coordinator = new WaveCoordinator();
        coordinator.RegisterWaves([
            new ProducerWave("producer", priority: 90),
            new ConsumerWave("consumer", priority: 50)
        ]);

        var profile = new CoordinatorProfile
        {
            Name = "sequential",
            WaveTimeoutMs = 5000,
            Lanes = new()
            {
                ["fast"] = new LaneConfig { Name = "fast", MaxConcurrency = 1 }
            }
        };

        var result = await coordinator.ExecuteAsync("test", profile: profile);

        // Consumer should have read producer's cached value
        var consumedSignal = result.Signals.FirstOrDefault(s => s.Key == "consumer.read_value");
        consumedSignal.Should().NotBeNull();
        consumedSignal!.GetValue<string>().Should().Be("hello from producer");
    }

    // --- Error handling ---

    [Fact]
    public async Task ExecuteAsync_CapturesWaveErrors()
    {
        var coordinator = new WaveCoordinator();
        coordinator.RegisterWaves([
            new FailingWave("fails"),
            new CountingWave("succeeds", priority: 50)
        ]);

        var result = await coordinator.ExecuteAsync("test");

        // Failing wave should produce an error signal
        result.Signals.Should().Contain(s => s.Key == "fails.error");
        result.ExecutionLog.Should().Contain(e => e.Status == WaveStatus.Error);
        // Other wave should still succeed
        result.ExecutionLog.Should().Contain(e => e.WaveName == "succeeds" && e.Status == WaveStatus.Success);
    }

    // --- ShouldRun precondition ---

    [Fact]
    public async Task ExecuteAsync_SkipsWaveWhenShouldRunReturnsFalse()
    {
        var coordinator = new WaveCoordinator();
        coordinator.RegisterWaves([
            new ConditionalWave("conditional", shouldRun: false)
        ]);

        var result = await coordinator.ExecuteAsync("test");

        result.Signals.Should().BeEmpty();
        result.ExecutionLog.Should().ContainSingle(e =>
            e.WaveName == "conditional" && e.Status == WaveStatus.Skipped);
    }

    // --- Telemetry ---

    [Fact]
    public async Task ExecuteAsync_TracksTotalDuration()
    {
        var coordinator = new WaveCoordinator();
        coordinator.RegisterWave(new CountingWave("wave1", priority: 90));

        var result = await coordinator.ExecuteAsync("test");

        result.TotalDurationMs.Should().BeGreaterThanOrEqualTo(0);
        result.Profile.Should().Be("default");
    }

    [Fact]
    public async Task ExecuteAsync_LogsLaneAssignment()
    {
        var coordinator = new WaveCoordinator();
        coordinator.RegisterWaves([
            new CountingWave("fast_wave", priority: 90), // No ml/llm/io tags → fast lane
            new MlWave("ml_wave")                        // "ml" tag → ml lane
        ]);

        var result = await coordinator.ExecuteAsync("test");

        result.ExecutionLog.Should().Contain(e => e.WaveName == "fast_wave" && e.Lane == "fast");
        result.ExecutionLog.Should().Contain(e => e.WaveName == "ml_wave" && e.Lane == "ml");
    }

    // --- Cancellation ---

    [Fact]
    public async Task ExecuteAsync_RespectsExternalCancellation()
    {
        var coordinator = new WaveCoordinator();
        coordinator.RegisterWave(new SlowWave("slow"));

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // Coordinator handles cancellation gracefully — no signals produced
        var result = await coordinator.ExecuteAsync("test", ct: cts.Token);
        result.Signals.Should().BeEmpty();
    }

    // --- Helper waves for testing ---

    private class CountingWave(string name, int priority) : ITypedAnalysisWave<string>
    {
        public string Name => name;
        public string Description => "Test wave";
        public int Priority => priority;
        public IReadOnlyList<string> Tags => [];
        public bool Enabled { get; set; } = true;

        public bool ShouldRun(string content, AnalysisContext context) => true;

        public Task<IEnumerable<Signal>> AnalyzeAsync(string content, AnalysisContext context, CancellationToken ct)
        {
            var signals = new List<Signal>
            {
                new() { Key = $"{name}.length", Value = content.Length, Source = name, Confidence = 1.0 },
                new() { Key = $"{name}.processed", Value = true, Source = name, Confidence = 1.0 }
            };
            return Task.FromResult<IEnumerable<Signal>>(signals);
        }
    }

    private class TrackingWave(string name, int priority, List<string> order) : ITypedAnalysisWave<string>
    {
        public string Name => name;
        public string Description => "Tracks execution order";
        public int Priority => priority;
        public IReadOnlyList<string> Tags => [];
        public bool Enabled { get; set; } = true;

        public bool ShouldRun(string content, AnalysisContext context) => true;

        public Task<IEnumerable<Signal>> AnalyzeAsync(string content, AnalysisContext context, CancellationToken ct)
        {
            order.Add(name);
            return Task.FromResult<IEnumerable<Signal>>([]);
        }
    }

    private class ProducerWave(string name, int priority) : ITypedAnalysisWave<string>
    {
        public string Name => name;
        public string Description => "Produces cached data";
        public int Priority => priority;
        public IReadOnlyList<string> Tags => [];
        public bool Enabled { get; set; } = true;

        public bool ShouldRun(string content, AnalysisContext context) => true;

        public Task<IEnumerable<Signal>> AnalyzeAsync(string content, AnalysisContext context, CancellationToken ct)
        {
            context.SetCached("shared_value", "hello from producer");
            return Task.FromResult<IEnumerable<Signal>>(
            [
                new Signal { Key = "producer.done", Value = true, Source = name, Confidence = 1.0 }
            ]);
        }
    }

    private class ConsumerWave(string name, int priority) : ITypedAnalysisWave<string>
    {
        public string Name => name;
        public string Description => "Reads cached data from producer";
        public int Priority => priority;
        public IReadOnlyList<string> Tags => [];
        public bool Enabled { get; set; } = true;

        public bool ShouldRun(string content, AnalysisContext context) => true;

        public Task<IEnumerable<Signal>> AnalyzeAsync(string content, AnalysisContext context, CancellationToken ct)
        {
            var value = context.GetCached<string>("shared_value") ?? "not found";
            return Task.FromResult<IEnumerable<Signal>>(
            [
                new Signal { Key = "consumer.read_value", Value = value, Source = name, Confidence = 1.0 }
            ]);
        }
    }

    private class FailingWave(string name) : ITypedAnalysisWave<string>
    {
        public string Name => name;
        public string Description => "Always throws";
        public int Priority => 90;
        public IReadOnlyList<string> Tags => [];
        public bool Enabled { get; set; } = true;

        public bool ShouldRun(string content, AnalysisContext context) => true;

        public Task<IEnumerable<Signal>> AnalyzeAsync(string content, AnalysisContext context, CancellationToken ct)
        {
            throw new InvalidOperationException("Test failure");
        }
    }

    private class ConditionalWave(string name, bool shouldRun) : ITypedAnalysisWave<string>
    {
        public string Name => name;
        public string Description => "Conditional execution";
        public int Priority => 50;
        public IReadOnlyList<string> Tags => [];
        public bool Enabled { get; set; } = true;

        public bool ShouldRun(string content, AnalysisContext context) => shouldRun;

        public Task<IEnumerable<Signal>> AnalyzeAsync(string content, AnalysisContext context, CancellationToken ct)
        {
            return Task.FromResult<IEnumerable<Signal>>(
            [
                new Signal { Key = "conditional.ran", Value = true, Source = name, Confidence = 1.0 }
            ]);
        }
    }

    private class MlWave(string name) : ITypedAnalysisWave<string>
    {
        public string Name => name;
        public string Description => "ML-tagged wave";
        public int Priority => 50;
        public IReadOnlyList<string> Tags => ["ml"];
        public bool Enabled { get; set; } = true;

        public bool ShouldRun(string content, AnalysisContext context) => true;

        public Task<IEnumerable<Signal>> AnalyzeAsync(string content, AnalysisContext context, CancellationToken ct)
        {
            return Task.FromResult<IEnumerable<Signal>>(
            [
                new Signal { Key = "ml.done", Value = true, Source = name, Confidence = 1.0 }
            ]);
        }
    }

    private class SlowWave(string name) : ITypedAnalysisWave<string>
    {
        public string Name => name;
        public string Description => "Slow wave for cancellation testing";
        public int Priority => 50;
        public IReadOnlyList<string> Tags => [];
        public bool Enabled { get; set; } = true;

        public bool ShouldRun(string content, AnalysisContext context) => true;

        public async Task<IEnumerable<Signal>> AnalyzeAsync(string content, AnalysisContext context, CancellationToken ct)
        {
            await Task.Delay(10000, ct);
            return [];
        }
    }
}
