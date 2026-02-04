using LucidRAG.LLM.Services.LoadBalancing;

namespace DoomSummarizer.Tests.LoadBalancing;

public class RoundRobinSelectorTests
{
    [Fact]
    public void Select_EmptyList_ReturnsNull()
    {
        var selector = new RoundRobinSelector();

        selector.Select([]).Should().BeNull();
    }

    [Fact]
    public void Select_SingleEndpoint_ReturnsThatEndpoint()
    {
        var selector = new RoundRobinSelector();
        var endpoints = new List<EndpointState> { new("http://a:11434", "a") };

        var result = selector.Select(endpoints);

        result.Should().NotBeNull();
        result!.Url.Should().Be("http://a:11434");
    }

    [Fact]
    public void Select_DistributesEvenly()
    {
        var selector = new RoundRobinSelector();
        var endpoints = new List<EndpointState>
        {
            new("http://a:11434", "a"),
            new("http://b:11434", "b"),
            new("http://c:11434", "c")
        };

        var counts = new Dictionary<string, int>();
        for (var i = 0; i < 30; i++)
        {
            var ep = selector.Select(endpoints)!;
            counts[ep.Url] = counts.GetValueOrDefault(ep.Url) + 1;
        }

        counts.Should().HaveCount(3);
        counts.Values.Should().AllBeEquivalentTo(10);
    }

    [Fact]
    public void Select_SkipsUnhealthyEndpoints()
    {
        var selector = new RoundRobinSelector();
        var healthy = new EndpointState("http://a:11434", "a");
        var unhealthy = LoadBalancingTestHelpers.CreateUnhealthy("http://b:11434", "b");

        var endpoints = new List<EndpointState> { healthy, unhealthy };

        for (var i = 0; i < 10; i++)
        {
            var result = selector.Select(endpoints);
            result.Should().NotBeNull();
            result!.Url.Should().Be("http://a:11434");
        }
    }

    [Fact]
    public void Select_AllUnhealthy_ReturnsNull()
    {
        var selector = new RoundRobinSelector();
        var ep = LoadBalancingTestHelpers.CreateUnhealthy("http://a:11434", "a");

        selector.Select(new List<EndpointState> { ep }).Should().BeNull();
    }
}