using FluentAssertions;
using Microsoft.Data.Sqlite;
using Mostlylucid.DocSummarizer.Resilience;
using Xunit;

namespace Mostlylucid.DocSummarizer.Rdbms.Sqlite.Tests;

/// <summary>
///     Simple test implementation of IServiceBudgetLookup that returns
///     pre-configured ServiceBudgetInfo for known services.
/// </summary>
internal class TestBudgetLookup : IServiceBudgetLookup
{
    private readonly Dictionary<string, ServiceBudgetInfo> _services = new(StringComparer.OrdinalIgnoreCase);

    public ServiceBudgetInfo? GetServiceBudgetInfo(string service)
    {
        return _services.TryGetValue(service, out var info) ? info : null;
    }

    public void Register(string service, ServiceBudgetInfo info)
    {
        _services[service] = info;
    }
}

public class SqliteApiBudgetServiceTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly ApiBudgetConfig _globalConfig;
    private readonly TestBudgetLookup _lookup;
    private SqliteApiBudgetService _sut;

    public SqliteApiBudgetServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db");
        _lookup = new TestBudgetLookup();
        _globalConfig = new ApiBudgetConfig
        {
            GlobalMaxRequestsPerDay = 200,
            GlobalDailyBudgetUsd = 2.0
        };
        _sut = new SqliteApiBudgetService(_globalConfig, _lookup, _dbPath);
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
    public async Task UnknownService_IsAllowed()
    {
        // Arrange - no service registered in lookup

        // Act
        var result = await _sut.CheckBudgetAsync("unknown-service");

        // Assert
        result.IsAllowed.Should().BeTrue();
        result.DenialReason.Should().BeNull();
    }

    [Fact]
    public async Task DailyLimit_DeniesAfterExceeded()
    {
        // Arrange - register service with daily limit of 5
        var service = "limited-api";
        _lookup.Register(service, new ServiceBudgetInfo
        {
            Enabled = true,
            MaxRequestsPerDay = 5,
            CostPerRequest = 0.001
        });

        // Act - record 5 requests to hit the limit
        for (var i = 0; i < 5; i++) await _sut.RecordUsageAsync(service);

        var result = await _sut.CheckBudgetAsync(service);

        // Assert
        result.IsAllowed.Should().BeFalse();
        result.DenialReason.Should().Contain("daily limit");
    }

    [Fact]
    public async Task LifetimeLimit_DeniesAfterExceeded()
    {
        // Arrange - register service with lifetime cap of 3
        var service = "capped-api";
        _lookup.Register(service, new ServiceBudgetInfo
        {
            Enabled = true,
            MaxRequestsPerDay = 100, // high daily limit so it does not trip first
            MaxRequests = 3, // lifetime cap
            CostPerRequest = 0.001
        });

        // Act - record 3 requests to hit lifetime cap
        for (var i = 0; i < 3; i++) await _sut.RecordUsageAsync(service);

        var result = await _sut.CheckBudgetAsync(service);

        // Assert
        result.IsAllowed.Should().BeFalse();
        result.DenialReason.Should().Contain("lifetime limit");
    }

    [Fact]
    public async Task RecordUsage_IncrementsCount()
    {
        // Arrange
        var service = "counter-api";
        _lookup.Register(service, new ServiceBudgetInfo
        {
            Enabled = true,
            MaxRequestsPerDay = 100,
            CostPerRequest = 0.01
        });

        // Act - record 3 usages
        await _sut.RecordUsageAsync(service);
        await _sut.RecordUsageAsync(service);
        await _sut.RecordUsageAsync(service);

        var result = await _sut.CheckBudgetAsync(service);

        // Assert
        result.IsAllowed.Should().BeTrue();
        result.DailyCount.Should().Be(3);
    }

    [Fact]
    public async Task GlobalDailyLimit_AppliesWhenNoPerServiceLimit()
    {
        // Arrange - use a very low global limit, no per-service limit registered
        var lowGlobalConfig = new ApiBudgetConfig
        {
            GlobalMaxRequestsPerDay = 3,
            GlobalDailyBudgetUsd = 100.0 // high budget so cost limit does not trip
        };
        var lookup = new TestBudgetLookup();
        // Do not register the service so per-service limit falls through to global

        await _sut.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);

        var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db");
        _sut = new SqliteApiBudgetService(lowGlobalConfig, lookup, dbPath);
        await _sut.InitializeAsync();

        var service = "global-limited-api";

        // Act - record enough to exceed global limit
        for (var i = 0; i < 3; i++) await _sut.RecordUsageAsync(service);

        var result = await _sut.CheckBudgetAsync(service);

        // Assert - should be denied by global daily limit
        // The per-service limit defaults to globalConfig.GlobalMaxRequestsPerDay when
        // svcInfo is null, so it hits the per-service branch (using global as fallback).
        result.IsAllowed.Should().BeFalse();
        result.DenialReason.Should().NotBeNullOrEmpty();

        // Clean up the temp db
        await _sut.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (File.Exists(dbPath))
            File.Delete(dbPath);

        // Re-create original SUT for DisposeAsync cleanup
        _sut = new SqliteApiBudgetService(_globalConfig, _lookup, _dbPath);
        await _sut.InitializeAsync();
    }

    [Fact]
    public async Task DisabledService_IsDenied()
    {
        // Arrange - register a disabled service
        var service = "disabled-api";
        _lookup.Register(service, new ServiceBudgetInfo
        {
            Enabled = false,
            MaxRequestsPerDay = 100,
            CostPerRequest = 0.01
        });

        // Act
        var result = await _sut.CheckBudgetAsync(service);

        // Assert
        result.IsAllowed.Should().BeFalse();
        result.DenialReason.Should().Contain("disabled");
    }
}