using LucidRAG.LLM.Services.LoadBalancing;

namespace DoomSummarizer.Tests.LoadBalancing;

public class EndpointStateTests
{
    [Fact]
    public void NewEndpoint_IsHealthy()
    {
        var state = new EndpointState("http://localhost:11434", "local");

        state.IsHealthy.Should().BeTrue();
        state.ConsecutiveFailures.Should().Be(0);
        state.TotalRequests.Should().Be(0);
        state.TotalFailures.Should().Be(0);
        state.EmaResponseTimeMs.Should().Be(double.MaxValue);
    }

    [Fact]
    public void RecordSuccess_UpdatesEma()
    {
        var state = new EndpointState("http://localhost:11434");

        state.RecordSuccess(100.0);

        state.EmaResponseTimeMs.Should().BeApproximately(100.0, 0.01);
        state.TotalRequests.Should().Be(1);
        state.ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public void RecordSuccess_AppliesEmaSmoothing()
    {
        var state = new EndpointState("http://localhost:11434");

        state.RecordSuccess(100.0); // EMA = 100
        state.RecordSuccess(200.0); // EMA = 0.3*200 + 0.7*100 = 130

        state.EmaResponseTimeMs.Should().BeApproximately(130.0, 0.01);
    }

    [Fact]
    public void RecordSuccess_ResetsConsecutiveFailures()
    {
        var state = new EndpointState("http://localhost:11434");

        state.RecordFailure();
        state.RecordFailure();
        state.ConsecutiveFailures.Should().Be(2);

        state.RecordSuccess(50.0);
        state.ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public void RecordFailure_IncrementsCounters()
    {
        var state = new EndpointState("http://localhost:11434");

        state.RecordFailure();

        state.ConsecutiveFailures.Should().Be(1);
        state.TotalFailures.Should().Be(1);
        state.TotalRequests.Should().Be(1);
    }

    [Fact]
    public void IsHealthy_FlipsAfterThreeFailures()
    {
        var state = new EndpointState("http://localhost:11434");

        state.RecordFailure();
        state.IsHealthy.Should().BeTrue();

        state.RecordFailure();
        state.IsHealthy.Should().BeTrue();

        state.RecordFailure();
        state.IsHealthy.Should().BeFalse();
    }

    [Fact]
    public void ResetHealth_RestoresHealthyState()
    {
        var state = new EndpointState("http://localhost:11434");

        state.RecordFailure();
        state.RecordFailure();
        state.RecordFailure();
        state.IsHealthy.Should().BeFalse();

        state.ResetHealth();
        state.IsHealthy.Should().BeTrue();
        state.ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public void ConcurrentRecordSuccess_DoesNotCorrupt()
    {
        var state = new EndpointState("http://localhost:11434");
        const int iterations = 10_000;

        Parallel.For(0, iterations, _ => { state.RecordSuccess(50.0); });

        state.TotalRequests.Should().Be(iterations);
        state.TotalFailures.Should().Be(0);
        state.IsHealthy.Should().BeTrue();
        state.EmaResponseTimeMs.Should().BeInRange(40, 60);
    }

    [Fact]
    public void ConcurrentMixedOperations_DoesNotCorrupt()
    {
        var state = new EndpointState("http://localhost:11434");

        Parallel.For(0, 5_000, i =>
        {
            if (i % 3 == 0)
                state.RecordFailure();
            else
                state.RecordSuccess(100.0);
        });

        state.TotalRequests.Should().Be(5_000);
    }
}