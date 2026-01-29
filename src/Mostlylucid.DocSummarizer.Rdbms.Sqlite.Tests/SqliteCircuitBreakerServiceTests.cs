using FluentAssertions;
using Microsoft.Data.Sqlite;
using Mostlylucid.DocSummarizer.Rdbms.Sqlite;
using Mostlylucid.DocSummarizer.Resilience;
using Xunit;

namespace Mostlylucid.DocSummarizer.Rdbms.Sqlite.Tests;

public class SqliteCircuitBreakerServiceTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private SqliteCircuitBreakerService _sut;

    public SqliteCircuitBreakerServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db");
        _sut = new SqliteCircuitBreakerService(_dbPath);
    }

    public async Task InitializeAsync()
    {
        await _sut.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _sut.DisposeAsync();
        SqliteConnection.ClearAllPools();

        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Fact]
    public void UnknownService_CircuitIsNotOpen()
    {
        // Arrange & Act
        var isOpen = _sut.IsCircuitOpen("unknown-service");

        // Assert
        isOpen.Should().BeFalse();
    }

    [Fact]
    public async Task ReportFailure_TripsCircuit_AfterThreshold()
    {
        // Arrange
        var service = "test-api";

        // Act - report multiple failures
        await _sut.ReportFailureAsync(service, CircuitFailureType.ServerError, "500 error");
        await _sut.ReportFailureAsync(service, CircuitFailureType.ServerError, "500 error again");
        await _sut.ReportFailureAsync(service, CircuitFailureType.ServerError, "500 error third time");

        // Assert - circuit should be open after failures
        var isOpen = _sut.IsCircuitOpen(service);
        isOpen.Should().BeTrue();

        var entry = _sut.GetCircuitEntry(service);
        entry.Should().NotBeNull();
        entry!.Status.Should().Be(CircuitStatus.Open);
        entry.FailureType.Should().Be(CircuitFailureType.ServerError);
        entry.FailureCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task ReportSuccess_ClosesCircuit()
    {
        // Arrange - trip the circuit first
        var service = "test-api";
        await _sut.TripCircuitAsync(service, CircuitFailureType.RateLimit, "429 too many");

        _sut.IsCircuitOpen(service).Should().BeTrue();

        // Act - report success
        await _sut.ReportSuccessAsync(service);

        // Assert - circuit should be closed
        _sut.IsCircuitOpen(service).Should().BeFalse();

        var entry = _sut.GetCircuitEntry(service);
        entry.Should().NotBeNull();
        entry!.Status.Should().Be(CircuitStatus.Closed);
        entry.FailureCount.Should().Be(0);
        entry.FailureType.Should().Be(CircuitFailureType.None);
    }

    [Fact]
    public async Task TripCircuit_DirectlyOpensCircuit()
    {
        // Arrange
        var service = "test-api";

        // Act
        await _sut.TripCircuitAsync(service, CircuitFailureType.DailyLimit, "daily limit reached");

        // Assert
        _sut.IsCircuitOpen(service).Should().BeTrue();

        var entry = _sut.GetCircuitEntry(service);
        entry.Should().NotBeNull();
        entry!.Status.Should().Be(CircuitStatus.Open);
        entry.FailureType.Should().Be(CircuitFailureType.DailyLimit);
        entry.LastFailureReason.Should().Be("daily limit reached");
    }

    [Fact]
    public void GetCircuitEntry_ReturnsNullForUnknown()
    {
        // Act
        var entry = _sut.GetCircuitEntry("nonexistent-service");

        // Assert
        entry.Should().BeNull();
    }

    [Fact]
    public async Task GetCircuitEntry_ReturnsEntryAfterTrip()
    {
        // Arrange
        var service = "test-api";

        // Act
        await _sut.TripCircuitAsync(service, CircuitFailureType.AuthError, "bad api key");

        // Assert
        var entry = _sut.GetCircuitEntry(service);
        entry.Should().NotBeNull();
        entry!.Service.Should().Be(service);
        entry.Status.Should().Be(CircuitStatus.Open);
        entry.FailureType.Should().Be(CircuitFailureType.AuthError);
        entry.LastFailureReason.Should().Be("bad api key");
        entry.TrippedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        entry.UpdatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PersistenceAcrossRestart()
    {
        // Arrange - trip a circuit and dispose
        var service = "persistent-api";
        await _sut.TripCircuitAsync(service, CircuitFailureType.BudgetExhausted, "budget gone");

        _sut.IsCircuitOpen(service).Should().BeTrue();

        // Dispose the current instance
        await _sut.DisposeAsync();
        SqliteConnection.ClearAllPools();

        // Act - create a new instance with the same database path
        var sut2 = new SqliteCircuitBreakerService(_dbPath);
        try
        {
            await sut2.InitializeAsync();

            // Assert - state should be persisted
            sut2.IsCircuitOpen(service).Should().BeTrue();

            var entry = sut2.GetCircuitEntry(service);
            entry.Should().NotBeNull();
            entry!.Status.Should().Be(CircuitStatus.Open);
            entry.FailureType.Should().Be(CircuitFailureType.BudgetExhausted);
            entry.LastFailureReason.Should().Be("budget gone");
        }
        finally
        {
            await sut2.DisposeAsync();
        }

        // Re-create the original SUT so DisposeAsync cleanup works
        _sut = new SqliteCircuitBreakerService(_dbPath);
        await _sut.InitializeAsync();
    }

    [Fact]
    public async Task HalfOpenTransition()
    {
        // Arrange - trip with ServerError (has short retry windows)
        var service = "flaky-api";
        await _sut.TripCircuitAsync(service, CircuitFailureType.ServerError, "server crashed");

        // The circuit should be open
        _sut.IsCircuitOpen(service).Should().BeTrue();

        var entry = _sut.GetCircuitEntry(service);
        entry.Should().NotBeNull();
        entry!.Status.Should().Be(CircuitStatus.Open);

        // ServerError with failureCount=1 sets RetryAfter to UtcNow + 1 minute.
        // The retry window should be in the future.
        entry.RetryAfter.Should().BeAfter(DateTimeOffset.UtcNow.AddSeconds(10));

        // IsServiceAvailableAsync should return false while retry window is active
        var available = await _sut.IsServiceAvailableAsync(service);
        available.Should().BeFalse();

        // After the circuit is probed (once retry window elapses), it transitions to HalfOpen.
        // We can simulate by reporting success which closes it.
        await _sut.ReportSuccessAsync(service);

        var afterSuccess = await _sut.IsServiceAvailableAsync(service);
        afterSuccess.Should().BeTrue();

        var closedEntry = _sut.GetCircuitEntry(service);
        closedEntry.Should().NotBeNull();
        closedEntry!.Status.Should().Be(CircuitStatus.Closed);
    }
}
