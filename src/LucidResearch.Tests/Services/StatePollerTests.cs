using LucidRAG.UltraResearch;
using LucidResearch;
using LucidResearch.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidResearch.Tests.Services;

public class StatePollerTests : IDisposable
{
    private readonly AppState _appState = new();
    private readonly UltraResearchOrchestrator _orchestrator;
    private readonly StatePoller _poller;

    public StatePollerTests()
    {
        // UltraResearchOrchestrator requires IServiceScopeFactory — but for polling
        // it only calls GetStatus/GetCheckpointsSnapshot which don't need scopes.
        // We'll construct it with a minimal service provider.
        var sc = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        sc.AddLogging(b => b.ClearProviders());
        var sp = sc.BuildServiceProvider();
        _orchestrator = new UltraResearchOrchestrator(
            sp,
            NullLogger<UltraResearchOrchestrator>.Instance);
        _poller = new StatePoller(
            _orchestrator,
            _appState,
            NullLogger<StatePoller>.Instance);
    }

    public void Dispose()
    {
        _poller.StopAsync().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Start_initiates_polling()
    {
        _poller.Start();
        // Calling start twice should be safe (idempotent)
        _poller.Start();

        // No exception means success
    }

    [Fact]
    public async Task StopAsync_gracefully_stops_polling()
    {
        _poller.Start();
        await Task.Delay(100);
        await _poller.StopAsync();

        // Calling stop twice should be safe
        await _poller.StopAsync();
    }

    [Fact]
    public async Task Poller_does_not_update_when_no_active_session()
    {
        _appState.ActiveSessionId.Value = null;

        _poller.Start();
        await Task.Delay(700); // Wait for at least one poll cycle
        await _poller.StopAsync();

        // State should remain at defaults since no session is active
        _appState.Iteration.Value.Should().Be(0);
        _appState.PapersFetched.Value.Should().Be(0);
    }

    [Fact]
    public async Task Poller_handles_unknown_session_gracefully()
    {
        _appState.ActiveSessionId.Value = Guid.NewGuid(); // Non-existent session

        _poller.Start();
        await Task.Delay(700);
        await _poller.StopAsync();

        // Should not crash, state unchanged
        _appState.Iteration.Value.Should().Be(0);
    }
}
