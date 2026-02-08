using DoomSummarizer.Services;
using LucidRAG.Data;
using LucidRAG.Services;
using LucidRAG.UltraResearch;
using LucidResearch;
using LucidResearch.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.DocSummarizer;

namespace LucidResearch.Tests;

public class ServiceRegistrationTests : IDisposable
{
    private readonly ServiceProvider _services;

    public ServiceRegistrationTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Data Source=:memory:"
            })
            .Build();

        var sc = new ServiceCollection();
        sc.AddSingleton<IConfiguration>(configuration);
        ServiceRegistration.ConfigureServices(sc, configuration);
        _services = sc.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Resolves_AppState_singleton()
    {
        var state1 = _services.GetRequiredService<AppState>();
        var state2 = _services.GetRequiredService<AppState>();

        state1.Should().NotBeNull();
        state1.Should().BeSameAs(state2, "AppState must be singleton");
    }

    [Fact]
    public void Resolves_StatePoller_singleton()
    {
        var poller1 = _services.GetRequiredService<StatePoller>();
        var poller2 = _services.GetRequiredService<StatePoller>();

        poller1.Should().NotBeNull();
        poller1.Should().BeSameAs(poller2, "StatePoller must be singleton");
    }

    [Fact]
    public void Resolves_UltraResearchOrchestrator_singleton()
    {
        var orch1 = _services.GetRequiredService<UltraResearchOrchestrator>();
        var orch2 = _services.GetRequiredService<UltraResearchOrchestrator>();

        orch1.Should().NotBeNull();
        orch1.Should().BeSameAs(orch2, "orchestrator must be singleton");
    }

    [Fact]
    public void Resolves_IDocumentIngester()
    {
        var ingester = _services.GetRequiredService<IDocumentIngester>();

        ingester.Should().NotBeNull();
        ingester.Should().BeOfType<DocumentIngester>();
    }

    [Fact]
    public void Resolves_RagDocumentsDbContext_in_scope()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDocumentsDbContext>();

        db.Should().NotBeNull();
    }

    [Fact]
    public void Resolves_ArxivFetcher_in_scope()
    {
        using var scope = _services.CreateScope();
        var fetcher = scope.ServiceProvider.GetRequiredService<ArxivFetcher>();

        fetcher.Should().NotBeNull();
    }

    [Fact]
    public void Resolves_ICitationResolver_in_scope()
    {
        using var scope = _services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ICitationResolver>();

        resolver.Should().NotBeNull();
        resolver.Should().BeOfType<CitationResolver>();
    }

    [Fact]
    public void Resolves_CitationGraphQueries_in_scope()
    {
        using var scope = _services.CreateScope();
        var queries = scope.ServiceProvider.GetRequiredService<CitationGraphQueries>();

        queries.Should().NotBeNull();
    }

    [Fact]
    public void Resolves_ResearchPaperFetcher_in_scope()
    {
        using var scope = _services.CreateScope();
        var fetcher = scope.ServiceProvider.GetRequiredService<ResearchPaperFetcher>();

        fetcher.Should().NotBeNull();
    }

    [Fact]
    public void Resolves_SemanticScholarClient_singleton()
    {
        var client1 = _services.GetRequiredService<SemanticScholarClient>();
        var client2 = _services.GetRequiredService<SemanticScholarClient>();

        client1.Should().NotBeNull();
        client1.Should().BeSameAs(client2, "SemanticScholarClient must be singleton");
    }

    [Fact]
    public void Resolves_IDocumentSummarizer_in_scope()
    {
        using var scope = _services.CreateScope();
        var summarizer = scope.ServiceProvider.GetRequiredService<IDocumentSummarizer>();

        summarizer.Should().NotBeNull();
    }

    [Fact]
    public void BuildServiceProvider_creates_valid_provider()
    {
        // This exercises the full BuildServiceProvider path with real config
        using var provider = ServiceRegistration.BuildServiceProvider([]);

        var state = provider.GetRequiredService<AppState>();
        state.Should().NotBeNull();

        var orchestrator = provider.GetRequiredService<UltraResearchOrchestrator>();
        orchestrator.Should().NotBeNull();
    }
}
