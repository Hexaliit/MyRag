using DoomSummarizer.Models;
using DoomSummarizer.Plugins;
using DoomSummarizer.Services;

namespace DoomSummarizer.Tests.Plugins;

public class SourcePluginRegistryTests
{
    [Fact]
    public void Register_AddsPluginForAllKeys()
    {
        var registry = new SourcePluginRegistry();
        var plugin = new FakePlugin("test", ["test", "t", "tst"]);

        registry.Register(plugin);

        registry.FindByKey("test").Should().BeSameAs(plugin);
        registry.FindByKey("t").Should().BeSameAs(plugin);
        registry.FindByKey("tst").Should().BeSameAs(plugin);
    }

    [Fact]
    public void FindByKey_ReturnsSamePlugin_ForAllAliases()
    {
        var registry = new SourcePluginRegistry();
        var plugin = new FakePlugin("hn", ["hn", "hackernews"]);
        registry.Register(plugin);

        var byHn = registry.FindByKey("hn");
        var byHackernews = registry.FindByKey("hackernews");

        byHn.Should().BeSameAs(byHackernews);
    }

    [Fact]
    public void FindByKey_IsCaseInsensitive()
    {
        var registry = new SourcePluginRegistry();
        registry.Register(new FakePlugin("test", ["test"]));

        registry.FindByKey("TEST").Should().NotBeNull();
        registry.FindByKey("Test").Should().NotBeNull();
    }

    [Fact]
    public void FindByKey_ReturnsNull_ForUnknownKey()
    {
        var registry = new SourcePluginRegistry();
        registry.Register(new FakePlugin("test", ["test"]));

        registry.FindByKey("nonexistent").Should().BeNull();
    }

    [Fact]
    public void HasKey_ReturnsTrue_ForRegisteredKey()
    {
        var registry = new SourcePluginRegistry();
        registry.Register(new FakePlugin("test", ["test", "alias"]));

        registry.HasKey("test").Should().BeTrue();
        registry.HasKey("alias").Should().BeTrue();
        registry.HasKey("other").Should().BeFalse();
    }

    [Fact]
    public void All_ReturnsPluginsInRegistrationOrder()
    {
        var registry = new SourcePluginRegistry();
        var p1 = new FakePlugin("first", ["first"]);
        var p2 = new FakePlugin("second", ["second"]);
        var p3 = new FakePlugin("third", ["third"]);

        registry.Register(p1);
        registry.Register(p2);
        registry.Register(p3);

        registry.All.Should().ContainInOrder(p1, p2, p3);
    }

    [Fact]
    public void FindByCapability_FiltersCorrectly()
    {
        var registry = new SourcePluginRegistry();
        var searchPlugin = new FakePlugin("s", ["s"], SourceCapabilities.Search);
        var feedPlugin = new FakePlugin("f", ["f"], SourceCapabilities.Feed);
        var bothPlugin = new FakePlugin("b", ["b"], SourceCapabilities.Search | SourceCapabilities.Feed);

        registry.Register(searchPlugin);
        registry.Register(feedPlugin);
        registry.Register(bothPlugin);

        registry.FindByCapability(SourceCapabilities.Search).Should().Contain(searchPlugin).And.Contain(bothPlugin).And
            .NotContain(feedPlugin);
        registry.FindByCapability(SourceCapabilities.Feed).Should().Contain(feedPlugin).And.Contain(bothPlugin).And
            .NotContain(searchPlugin);
    }

    [Fact]
    public void FindFreePlugins_ReturnsOnlyNoAuthPlugins()
    {
        var registry = new SourcePluginRegistry();
        var freePlugin = new FakePlugin("free", ["free"]);
        var paidPlugin = new FakePlugin("paid", ["paid"], requiredApiKeys: ["api_key"]);

        registry.Register(freePlugin);
        registry.Register(paidPlugin);

        registry.FindFreePlugins().Should().Contain(freePlugin).And.NotContain(paidPlugin);
    }

    [Fact]
    public void DuplicateKey_DoesNotOverwrite()
    {
        var registry = new SourcePluginRegistry();
        var first = new FakePlugin("a", ["shared"]);
        var second = new FakePlugin("b", ["shared"]);

        registry.Register(first);
        registry.Register(second);

        // First registration wins
        registry.FindByKey("shared").Should().BeSameAs(first);
        // Both are in the All list
        registry.All.Should().HaveCount(2);
    }

    [Fact]
    public async Task InitializeAllAsync_CallsAllPlugins()
    {
        var registry = new SourcePluginRegistry();
        var p1 = new FakePlugin("a", ["a"]);
        var p2 = new FakePlugin("b", ["b"]);

        registry.Register(p1);
        registry.Register(p2);

        var services = CreateFakeServices();
        await registry.InitializeAllAsync(services);

        p1.Initialized.Should().BeTrue();
        p2.Initialized.Should().BeTrue();
    }

    [Fact]
    public void BuiltinPlugins_RegistersAllExpectedKeys()
    {
        var registry = new SourcePluginRegistry();
        BuiltinPlugins.RegisterAllSources(registry);

        // Core source keys that must be registered
        registry.HasKey("hn").Should().BeTrue();
        registry.HasKey("hackernews").Should().BeTrue();
        registry.HasKey("reddit").Should().BeTrue();
        registry.HasKey("gnews").Should().BeTrue();
        registry.HasKey("gnews_topic").Should().BeTrue();
        registry.HasKey("search").Should().BeTrue();
        registry.HasKey("brave").Should().BeTrue();
        registry.HasKey("serper").Should().BeTrue();
        registry.HasKey("tavily").Should().BeTrue();
        registry.HasKey("jina").Should().BeTrue();
        registry.HasKey("ddg").Should().BeTrue();
        registry.HasKey("arxiv").Should().BeTrue();
        registry.HasKey("so").Should().BeTrue();
        registry.HasKey("wiki").Should().BeTrue();
        registry.HasKey("wikipedia").Should().BeTrue();
        registry.HasKey("factcheck").Should().BeTrue();
        registry.HasKey("spaceflight").Should().BeTrue();
        registry.HasKey("earthquake").Should().BeTrue();
        registry.HasKey("web").Should().BeTrue();
        registry.HasKey("bbc").Should().BeTrue();
        registry.HasKey("guardian").Should().BeTrue();
    }

    private static SourcePluginServices CreateFakeServices()
    {
        return new SourcePluginServices
        {
            HttpClient = new HttpClient(),
            ApiKeys = ApiKeyService.Load(new DoomConfig().Keys),
            ApiBudget = new ApiBudgetService(new ApiBudgetConfig(), ApiKeyService.Load(new DoomConfig().Keys),
                Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db")),
            CircuitBreaker = new CircuitBreakerService(
                Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db"))
        };
    }

    private sealed class FakePlugin(
        string primaryKey,
        IReadOnlyList<string> keys,
        SourceCapabilities capabilities = SourceCapabilities.None,
        IReadOnlyList<string>? requiredApiKeys = null) : ISourcePlugin
    {
        public bool Initialized { get; private set; }

        public SourcePluginMetadata Metadata { get; } = new()
        {
            PrimaryKey = primaryKey,
            Keys = keys,
            DisplayName = $"Fake {primaryKey}",
            Description = $"Fake plugin for {primaryKey}",
            Capabilities = capabilities,
            RequiredApiKeys = requiredApiKeys ?? []
        };

        public Task InitializeAsync(SourcePluginServices services, CancellationToken ct = default)
        {
            Initialized = true;
            return Task.CompletedTask;
        }

        public Task<List<ContentItem>> FetchAsync(SourceFetchContext context, CancellationToken ct = default)
        {
            return Task.FromResult(new List<ContentItem>());
        }
    }
}