using LucidRAG.LLM.Services.LoadBalancing;

namespace DoomSummarizer.Tests.LoadBalancing;

public class FastestSelectorTests
{
    [Fact]
    public void Select_EmptyList_ReturnsNull()
    {
        var selector = new FastestSelector();

        selector.Select([]).Should().BeNull();
    }

    [Fact]
    public void Select_PicksLowestEma()
    {
        var selector = new FastestSelector();
        var slow = new EndpointState("http://slow:11434", "slow");
        var fast = new EndpointState("http://fast:11434", "fast");
        var medium = new EndpointState("http://medium:11434", "medium");

        slow.RecordSuccess(500.0);
        fast.RecordSuccess(50.0);
        medium.RecordSuccess(200.0);

        var endpoints = new List<EndpointState> { slow, fast, medium };

        var result = selector.Select(endpoints);
        result.Should().NotBeNull();
        result!.Url.Should().Be("http://fast:11434");
    }

    [Fact]
    public void Select_FallsBackToRoundRobin_WhenNoLatencyData()
    {
        var selector = new FastestSelector();
        var endpoints = new List<EndpointState>
        {
            new("http://a:11434", "a"),
            new("http://b:11434", "b"),
        };

        // All EMA = MaxValue, should fall back to round-robin
        var result = selector.Select(endpoints);
        result.Should().NotBeNull();
    }

    [Fact]
    public void Select_AdaptsWhenLatencyChanges()
    {
        var selector = new FastestSelector();
        var epA = new EndpointState("http://a:11434", "a");
        var epB = new EndpointState("http://b:11434", "b");

        // A starts faster
        epA.RecordSuccess(50.0);
        epB.RecordSuccess(200.0);

        var endpoints = new List<EndpointState> { epA, epB };
        selector.Select(endpoints)!.Url.Should().Be("http://a:11434");

        // A becomes slow, B becomes fast — record enough for EMA to flip
        for (var i = 0; i < 20; i++)
        {
            epA.RecordSuccess(500.0);
            epB.RecordSuccess(10.0);
        }

        selector.Select(endpoints)!.Url.Should().Be("http://b:11434");
    }

    [Fact]
    public void Select_SkipsUnhealthyEndpoints()
    {
        var selector = new FastestSelector();
        var fast = new EndpointState("http://fast:11434", "fast");
        var slow = new EndpointState("http://slow:11434", "slow");

        fast.RecordSuccess(10.0);
        slow.RecordSuccess(500.0);

        // Make the fast one unhealthy (3 failures after success resets counter)
        fast.RecordFailure();
        fast.RecordFailure();
        fast.RecordFailure();

        var endpoints = new List<EndpointState> { fast, slow };
        var result = selector.Select(endpoints);
        result.Should().NotBeNull();
        result!.Url.Should().Be("http://slow:11434");
    }

    [Fact]
    public void Select_AllUnhealthy_ReturnsNull()
    {
        var selector = new FastestSelector();
        var ep = LoadBalancingTestHelpers.CreateUnhealthy("http://a:11434", "a");

        selector.Select(new List<EndpointState> { ep }).Should().BeNull();
    }
}
