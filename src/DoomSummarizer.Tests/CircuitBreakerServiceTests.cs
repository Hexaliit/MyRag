using Mostlylucid.DocSummarizer.Resilience;

namespace DoomSummarizer.Tests;

public class CircuitBreakerServiceTests : IAsyncLifetime
{
    private CircuitBreakerService _circuit = null!;
    private string _dbPath = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"doom_circuit_test_{Guid.NewGuid():N}.db");
        _circuit = new CircuitBreakerService(_dbPath);
        await _circuit.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _circuit.DisposeAsync();
        try
        {
            File.Delete(_dbPath);
        }
        catch
        {
            /* cleanup best-effort */
        }
    }

    #region Trip Circuit

    [Fact]
    public async Task TripCircuitAsync_OpensCircuitWithMessage()
    {
        await _circuit.TripCircuitAsync("brave_search", CircuitFailureType.DailyLimit, "Daily limit exhausted");

        _circuit.IsCircuitOpen("brave_search").Should().BeTrue();
        var state = _circuit.GetState("brave_search");
        state!.Status.Should().Be(CircuitStatus.Open);
        state.FailureType.Should().Be(CircuitFailureType.DailyLimit);
    }

    #endregion

    #region Initial State

    [Fact]
    public void IsCircuitOpen_UnknownService_ReturnsFalse()
    {
        _circuit.IsCircuitOpen("nonexistent").Should().BeFalse();
    }

    [Fact]
    public async Task IsServiceAvailableAsync_UnknownService_ReturnsTrue()
    {
        var available = await _circuit.IsServiceAvailableAsync("nonexistent");
        available.Should().BeTrue();
    }

    [Fact]
    public void GetState_UnknownService_ReturnsNull()
    {
        _circuit.GetState("nonexistent").Should().BeNull();
    }

    #endregion

    #region Report Success

    [Fact]
    public async Task ReportSuccessAsync_ClosesCircuit()
    {
        await _circuit.ReportSuccessAsync("brave_search");

        _circuit.IsCircuitOpen("brave_search").Should().BeFalse();
        var state = _circuit.GetState("brave_search");
        state.Should().NotBeNull();
        state!.Status.Should().Be(CircuitStatus.Closed);
        state.FailureCount.Should().Be(0);
        state.FailureType.Should().Be(CircuitFailureType.None);
    }

    [Fact]
    public async Task ReportSuccessAsync_AfterFailure_ResetsState()
    {
        // Trip the circuit first
        await _circuit.ReportFailureAsync("serper", CircuitFailureType.ServerError, "HTTP 500");
        _circuit.IsCircuitOpen("serper").Should().BeTrue();

        // Success resets everything
        await _circuit.ReportSuccessAsync("serper");
        _circuit.IsCircuitOpen("serper").Should().BeFalse();

        var state = _circuit.GetState("serper");
        state!.Status.Should().Be(CircuitStatus.Closed);
        state.FailureCount.Should().Be(0);
    }

    #endregion

    #region Report Failure

    [Fact]
    public async Task ReportFailureAsync_OpensCircuit()
    {
        await _circuit.ReportFailureAsync("brave_search", CircuitFailureType.RateLimit, "429 Too Many Requests");

        _circuit.IsCircuitOpen("brave_search").Should().BeTrue();
        var state = _circuit.GetState("brave_search");
        state!.Status.Should().Be(CircuitStatus.Open);
        state.FailureType.Should().Be(CircuitFailureType.RateLimit);
        state.FailureCount.Should().Be(1);
        state.LastFailureReason.Should().Be("429 Too Many Requests");
    }

    [Fact]
    public async Task ReportFailureAsync_IncrementsFailureCount()
    {
        await _circuit.ReportFailureAsync("serper", CircuitFailureType.ServerError, "HTTP 500");
        await _circuit.ReportFailureAsync("serper", CircuitFailureType.ServerError, "HTTP 502");
        await _circuit.ReportFailureAsync("serper", CircuitFailureType.ServerError, "HTTP 503");

        var state = _circuit.GetState("serper");
        state!.FailureCount.Should().Be(3);
        state.LastFailureReason.Should().Be("HTTP 503");
    }

    [Fact]
    public async Task ReportFailureAsync_AuthError_SetsPermanentRetry()
    {
        await _circuit.ReportFailureAsync("openai", CircuitFailureType.AuthError, "Invalid API key");

        var state = _circuit.GetState("openai");
        state!.RetryAfter.Should().Be(DateTimeOffset.MaxValue);
        _circuit.IsCircuitOpen("openai").Should().BeTrue();
    }

    [Fact]
    public async Task ReportFailureAsync_LifetimeLimit_SetsPermanentRetry()
    {
        await _circuit.ReportFailureAsync("serper", CircuitFailureType.LifetimeLimit, "Lifetime limit reached");

        var state = _circuit.GetState("serper");
        state!.RetryAfter.Should().Be(DateTimeOffset.MaxValue);
    }

    [Fact]
    public async Task ReportFailureAsync_DailyLimit_SetsRetryToMidnight()
    {
        await _circuit.ReportFailureAsync("brave_search", CircuitFailureType.DailyLimit, "Daily limit reached");

        var state = _circuit.GetState("brave_search");
        var expectedMidnight = DateTimeOffset.UtcNow.Date.AddDays(1);
        state!.RetryAfter.Should().BeCloseTo(expectedMidnight, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReportFailureAsync_BudgetExhausted_SetsRetryToMidnight()
    {
        await _circuit.ReportFailureAsync("openai", CircuitFailureType.BudgetExhausted, "Daily budget exceeded");

        var state = _circuit.GetState("openai");
        var expectedMidnight = DateTimeOffset.UtcNow.Date.AddDays(1);
        state!.RetryAfter.Should().BeCloseTo(expectedMidnight, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReportFailureAsync_RateLimit_UsesExponentialBackoff()
    {
        // First failure: 5 minutes
        await _circuit.ReportFailureAsync("tavily", CircuitFailureType.RateLimit);
        var state1 = _circuit.GetState("tavily");
        state1!.RetryAfter.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(10));

        // Second failure: 15 minutes
        await _circuit.ReportFailureAsync("tavily", CircuitFailureType.RateLimit);
        var state2 = _circuit.GetState("tavily");
        state2!.RetryAfter.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(15), TimeSpan.FromSeconds(10));
        state2.FailureCount.Should().Be(2);

        // Third failure: 1 hour
        await _circuit.ReportFailureAsync("tavily", CircuitFailureType.RateLimit);
        var state3 = _circuit.GetState("tavily");
        state3!.RetryAfter.Should().BeCloseTo(DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromSeconds(10));
        state3.FailureCount.Should().Be(3);
    }

    [Fact]
    public async Task ReportFailureAsync_ServerError_UsesExponentialBackoff()
    {
        // First failure: 1 minute
        await _circuit.ReportFailureAsync("newsapi", CircuitFailureType.ServerError);
        var state1 = _circuit.GetState("newsapi");
        state1!.RetryAfter.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(1), TimeSpan.FromSeconds(10));

        // Second failure: 5 minutes
        await _circuit.ReportFailureAsync("newsapi", CircuitFailureType.ServerError);
        var state2 = _circuit.GetState("newsapi");
        state2!.RetryAfter.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(10));
    }

    #endregion

    #region IsServiceAvailableAsync (Half-Open)

    [Fact]
    public async Task IsServiceAvailableAsync_OpenCircuit_ReturnsFalse()
    {
        await _circuit.ReportFailureAsync("brave_search", CircuitFailureType.RateLimit);
        // RetryAfter is 5 minutes in the future, so circuit should block
        var available = await _circuit.IsServiceAvailableAsync("brave_search");
        available.Should().BeFalse();
    }

    [Fact]
    public async Task IsServiceAvailableAsync_PermanentCircuit_NeverAvailable()
    {
        await _circuit.ReportFailureAsync("openai", CircuitFailureType.AuthError, "Bad key");
        var available = await _circuit.IsServiceAvailableAsync("openai");
        available.Should().BeFalse();
    }

    #endregion

    #region Persistence

    [Fact]
    public async Task CircuitState_SurvivesRestart()
    {
        // Trip a circuit
        await _circuit.ReportFailureAsync("brave_search", CircuitFailureType.DailyLimit, "Hit daily limit");
        var stateBefore = _circuit.GetState("brave_search");

        // Dispose and create a new instance against the same DB
        await _circuit.DisposeAsync();

        var circuit2 = new CircuitBreakerService(_dbPath);
        await circuit2.InitializeAsync();

        try
        {
            // State should be preserved
            var stateAfter = circuit2.GetState("brave_search");
            stateAfter.Should().NotBeNull();
            stateAfter!.Status.Should().Be(CircuitStatus.Open);
            stateAfter.FailureType.Should().Be(CircuitFailureType.DailyLimit);
            stateAfter.FailureCount.Should().Be(1);
            stateAfter.LastFailureReason.Should().Be("Hit daily limit");

            // IsCircuitOpen should still work
            circuit2.IsCircuitOpen("brave_search").Should().BeTrue();
        }
        finally
        {
            await circuit2.DisposeAsync();
        }
    }

    [Fact]
    public async Task MultipleServices_IndependentState()
    {
        await _circuit.ReportFailureAsync("brave_search", CircuitFailureType.RateLimit);
        await _circuit.ReportSuccessAsync("serper");
        await _circuit.ReportFailureAsync("tavily", CircuitFailureType.DailyLimit);

        _circuit.IsCircuitOpen("brave_search").Should().BeTrue();
        _circuit.IsCircuitOpen("serper").Should().BeFalse();
        _circuit.IsCircuitOpen("tavily").Should().BeTrue();
    }

    [Fact]
    public async Task SuccessAfterFailure_PersistsClosedState()
    {
        await _circuit.ReportFailureAsync("brave_search", CircuitFailureType.ServerError);
        await _circuit.ReportSuccessAsync("brave_search");

        // Restart
        await _circuit.DisposeAsync();
        var circuit2 = new CircuitBreakerService(_dbPath);
        await circuit2.InitializeAsync();

        try
        {
            circuit2.IsCircuitOpen("brave_search").Should().BeFalse();
            var state = circuit2.GetState("brave_search");
            state!.Status.Should().Be(CircuitStatus.Closed);
            state.FailureCount.Should().Be(0);
        }
        finally
        {
            await circuit2.DisposeAsync();
        }
    }

    #endregion

    #region Classification Helpers

    [Theory]
    [InlineData(429, CircuitFailureType.RateLimit)]
    [InlineData(401, CircuitFailureType.AuthError)]
    [InlineData(403, CircuitFailureType.AuthError)]
    [InlineData(500, CircuitFailureType.ServerError)]
    [InlineData(502, CircuitFailureType.ServerError)]
    [InlineData(503, CircuitFailureType.ServerError)]
    [InlineData(504, CircuitFailureType.ServerError)]
    [InlineData(200, CircuitFailureType.ServerError)] // unexpected status falls to default
    public void ClassifyHttpStatus_ReturnsCorrectType(int statusCode, CircuitFailureType expected)
    {
        CircuitBreakerService.ClassifyHttpStatus(statusCode).Should().Be(expected);
    }

    [Theory]
    [InlineData("Exceeded daily limit for brave_search", CircuitFailureType.DailyLimit)]
    [InlineData("lifetime limit reached", CircuitFailureType.LifetimeLimit)]
    [InlineData("daily budget exceeded", CircuitFailureType.BudgetExhausted)]
    [InlineData("Global daily limit reached", CircuitFailureType.DailyLimit)]
    [InlineData("Global daily budget exceeded", CircuitFailureType.BudgetExhausted)]
    [InlineData("Service disabled", CircuitFailureType.AuthError)]
    [InlineData("Unknown reason", CircuitFailureType.DailyLimit)] // default fallback
    [InlineData(null, CircuitFailureType.DailyLimit)] // null fallback
    public void ClassifyBudgetDenial_ReturnsCorrectType(string? reason, CircuitFailureType expected)
    {
        CircuitBreakerService.ClassifyBudgetDenial(reason).Should().Be(expected);
    }

    #endregion

    #region PrintCircuitStatus

    [Fact]
    public async Task PrintCircuitStatus_NoOpenCircuits_DoesNotThrow()
    {
        // Just verify it doesn't throw — output goes to AnsiConsole
        _circuit.PrintCircuitStatus();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task PrintCircuitStatus_WithOpenCircuits_DoesNotThrow()
    {
        await _circuit.ReportFailureAsync("brave_search", CircuitFailureType.RateLimit, "429");
        await _circuit.ReportFailureAsync("serper", CircuitFailureType.AuthError, "Invalid key");

        _circuit.PrintCircuitStatus();
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task InitializeAsync_CalledTwice_IsIdempotent()
    {
        // Already initialized in constructor; calling again should be safe
        await _circuit.InitializeAsync();
        _circuit.IsCircuitOpen("anything").Should().BeFalse();
    }

    [Fact]
    public async Task ReportFailure_ThenSuccess_ThenFailure_TracksCorrectly()
    {
        await _circuit.ReportFailureAsync("serper", CircuitFailureType.RateLimit);
        _circuit.GetState("serper")!.FailureCount.Should().Be(1);

        await _circuit.ReportSuccessAsync("serper");
        _circuit.GetState("serper")!.FailureCount.Should().Be(0);

        // New failure should start fresh at count 1
        await _circuit.ReportFailureAsync("serper", CircuitFailureType.ServerError);
        var state = _circuit.GetState("serper");
        state!.FailureCount.Should().Be(1);
        state.FailureType.Should().Be(CircuitFailureType.ServerError);
    }

    [Fact]
    public async Task IsCircuitOpen_SyncCheck_PermanentFailure_ReturnsTrue()
    {
        await _circuit.ReportFailureAsync("openai", CircuitFailureType.AuthError);

        // Sync check should block permanently
        _circuit.IsCircuitOpen("openai").Should().BeTrue();
    }

    #endregion
}