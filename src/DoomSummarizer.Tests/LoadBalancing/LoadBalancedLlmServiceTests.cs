using LucidRAG.LLM.Services.LoadBalancing;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Tests.LoadBalancing;

public class LoadBalancedLlmServiceTests : IDisposable
{
    private readonly List<EndpointState> _endpoints;
    private readonly Dictionary<string, ILlmService> _services;
    private LoadBalancedLlmService? _sut;

    public LoadBalancedLlmServiceTests()
    {
        _endpoints =
        [
            new EndpointState("http://a:11434", "a"),
            new EndpointState("http://b:11434", "b")
        ];
        _services = new Dictionary<string, ILlmService>
        {
            ["http://a:11434"] = new FakeLlmService("a"),
            ["http://b:11434"] = new FakeLlmService("b")
        };
    }

    public void Dispose()
    {
        _sut?.Dispose();
    }

    [Fact]
    public async Task GenerateAsync_RoutesToSelectedEndpoint()
    {
        _sut = CreateService(new RoundRobinSelector());

        var result = await _sut.GenerateAsync("test");

        result.Should().BeOneOf("a:test", "b:test");
    }

    [Fact]
    public async Task GenerateAsync_FailsOver_OnEndpointFailure()
    {
        _services["http://a:11434"] = new FakeLlmService("a", true);
        // Use a selector that always picks "a" first, so failover to "b" is guaranteed
        _sut = CreateService(new FixedOrderSelector());

        var result = await _sut.GenerateAsync("test");

        result.Should().Be("b:test");
        _endpoints[0].ConsecutiveFailures.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GenerateAsync_MarksEndpointUnhealthy()
    {
        _services["http://a:11434"] = new FakeLlmService("a", true);
        _sut = CreateService(new FixedOrderSelector());

        // Each request will fail on "a" then succeed on "b"
        for (var i = 0; i < 3; i++)
            await _sut.GenerateAsync($"test{i}");

        _endpoints[0].IsHealthy.Should().BeFalse();
        _endpoints[1].IsHealthy.Should().BeTrue();
    }

    [Fact]
    public async Task GenerateAsync_AllEndpointsExhausted_Throws()
    {
        _services["http://a:11434"] = new FakeLlmService("a", true);
        _services["http://b:11434"] = new FakeLlmService("b", true);
        _sut = CreateService(new RoundRobinSelector());

        var act = () => _sut.GenerateAsync("test");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*endpoints exhausted*");
    }

    [Fact]
    public async Task GenerateJsonAsync_FailsOver()
    {
        _services["http://a:11434"] = new FakeLlmService("a", true);
        _sut = CreateService(new FixedOrderSelector());

        var result = await _sut.GenerateJsonAsync<SimpleDto>("test");

        result.Should().NotBeNull();
        result!.Value.Should().Be("b:test");
    }

    [Fact]
    public async Task IsAvailableAsync_TrueWhenAnyHealthy()
    {
        _endpoints[0].RecordFailure();
        _endpoints[0].RecordFailure();
        _endpoints[0].RecordFailure();
        _sut = CreateService(new RoundRobinSelector());

        var available = await _sut.IsAvailableAsync();

        available.Should().BeTrue();
    }

    [Fact]
    public async Task IsAvailableAsync_FalseWhenAllUnhealthy()
    {
        foreach (var ep in _endpoints)
        {
            ep.RecordFailure();
            ep.RecordFailure();
            ep.RecordFailure();
        }

        _sut = CreateService(new RoundRobinSelector());

        var available = await _sut.IsAvailableAsync();

        available.Should().BeFalse();
    }

    [Fact]
    public void ProviderName_IncludesEndpointCount()
    {
        _sut = CreateService(new RoundRobinSelector());

        _sut.ProviderName.Should().Contain("2 endpoints");
    }

    [Fact]
    public async Task RecordSuccess_UpdatesEma()
    {
        _sut = CreateService(new RoundRobinSelector());

        await _sut.GenerateAsync("test");

        // At least one endpoint should have recorded a success
        _endpoints.Should().Contain(e => e.TotalRequests > 0 && e.EmaResponseTimeMs < double.MaxValue);
    }

    private LoadBalancedLlmService CreateService(IEndpointSelector selector)
    {
        return new LoadBalancedLlmService("test-backend", _endpoints, _services, selector,
            NullLogger<LoadBalancedLlmService>.Instance,
            0);
        // Disable health monitor in tests
    }

    /// <summary>
    ///     Selector that always returns the first healthy endpoint (deterministic for failover tests).
    /// </summary>
    private class FixedOrderSelector : IEndpointSelector
    {
        public EndpointState? Select(IReadOnlyList<EndpointState> endpoints)
        {
            return endpoints.FirstOrDefault(e => e.IsHealthy);
        }
    }

    // Simple DTO for JSON deserialization tests
    private class SimpleDto
    {
        public string Value { get; set; } = "";
    }
}