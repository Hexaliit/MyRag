using Microsoft.Data.Sqlite;
using Mostlylucid.DocSummarizer.Resilience;

namespace Mostlylucid.DocSummarizer.Rdbms.Sqlite;

/// <summary>
///     Central budget tracker for all paid/limited APIs.
///     SQLite-backed usage tracking with per-service and global limits.
/// </summary>
public class SqliteApiBudgetService : IApiBudget, IAsyncDisposable
{
    private readonly SqliteConnection _db;
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private readonly ApiBudgetConfig _globalConfig;
    private readonly IServiceBudgetLookup _lookup;
    private bool _initialized;

    public SqliteApiBudgetService(ApiBudgetConfig globalConfig, IServiceBudgetLookup lookup, string dbPath)
    {
        _globalConfig = globalConfig;
        _lookup = lookup;
        _db = new SqliteConnection($"Data Source={dbPath}");
    }

    /// <inheritdoc />
    public async Task<BudgetCheckResult> CheckBudgetAsync(string service)
    {
        await InitializeAsync();

        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        var svcInfo = _lookup.GetServiceBudgetInfo(service);
        var costPerRequest = svcInfo?.CostPerRequest ?? 0.005;

        if (svcInfo is { Enabled: false })
            return BudgetCheckResult.Denied($"{service}: disabled in config");

        var dailyCount = await GetDailyCountAsync(service, today);
        var serviceLimit = svcInfo?.MaxRequestsPerDay ?? _globalConfig.GlobalMaxRequestsPerDay;
        if (serviceLimit > 0 && dailyCount >= serviceLimit)
            return BudgetCheckResult.Denied($"{service}: daily limit reached ({dailyCount}/{serviceLimit})");

        if (svcInfo is { MaxRequests: > 0 })
        {
            var totalCount = await GetTotalCountAsync(service);
            if (totalCount >= svcInfo.MaxRequests)
                return BudgetCheckResult.Denied(
                    $"{service}: lifetime limit reached ({totalCount}/{svcInfo.MaxRequests})");
        }

        if (svcInfo is { DailyBudgetUsd: > 0 })
        {
            var serviceDailyCost = await GetServiceDailyCostAsync(service, today);
            if (serviceDailyCost + costPerRequest > svcInfo.DailyBudgetUsd)
                return BudgetCheckResult.Denied(
                    $"{service}: daily budget exhausted (${serviceDailyCost:F3}/${svcInfo.DailyBudgetUsd:F2})");
        }

        var globalDailyCount = await GetGlobalDailyCountAsync(today);
        if (_globalConfig.GlobalMaxRequestsPerDay > 0 && globalDailyCount >= _globalConfig.GlobalMaxRequestsPerDay)
            return BudgetCheckResult.Denied(
                $"Global daily limit reached ({globalDailyCount}/{_globalConfig.GlobalMaxRequestsPerDay})");

        if (_globalConfig.GlobalDailyBudgetUsd > 0)
        {
            var globalDailyCost = await GetGlobalDailyCostAsync(today);
            if (globalDailyCost + costPerRequest > _globalConfig.GlobalDailyBudgetUsd)
                return BudgetCheckResult.Denied(
                    $"Global daily budget exhausted (${globalDailyCost:F3}/${_globalConfig.GlobalDailyBudgetUsd:F2})");
        }

        return BudgetCheckResult.Allowed(dailyCount, serviceLimit, costPerRequest);
    }

    /// <inheritdoc />
    public async Task RecordUsageAsync(string service, int count = 1)
    {
        await InitializeAsync();

        var svcInfo = _lookup.GetServiceBudgetInfo(service);
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        var cost = (svcInfo?.CostPerRequest ?? 0.005) * count;

        await _dbLock.WaitAsync();
        try
        {
            using var cmd1 = _db.CreateCommand();
            cmd1.CommandText = """
                               INSERT INTO api_usage (service, date, request_count, estimated_cost_usd)
                               VALUES (@service, @date, @count, @cost)
                               ON CONFLICT(service, date) DO UPDATE SET
                                   request_count = request_count + @count,
                                   estimated_cost_usd = estimated_cost_usd + @cost
                               """;
            cmd1.Parameters.AddWithValue("@service", service);
            cmd1.Parameters.AddWithValue("@date", today);
            cmd1.Parameters.AddWithValue("@count", count);
            cmd1.Parameters.AddWithValue("@cost", cost);
            await cmd1.ExecuteNonQueryAsync();

            using var cmd2 = _db.CreateCommand();
            cmd2.CommandText = """
                               INSERT INTO api_usage_total (service, total_requests, total_cost_usd)
                               VALUES (@service, @count, @cost)
                               ON CONFLICT(service) DO UPDATE SET
                                   total_requests = total_requests + @count,
                                   total_cost_usd = total_cost_usd + @cost
                               """;
            cmd2.Parameters.AddWithValue("@service", service);
            cmd2.Parameters.AddWithValue("@count", count);
            cmd2.Parameters.AddWithValue("@cost", cost);
            await cmd2.ExecuteNonQueryAsync();
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _dbLock.Dispose();
        await _db.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        await _db.OpenAsync();

        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
                          CREATE TABLE IF NOT EXISTS api_usage (
                              service TEXT NOT NULL,
                              date TEXT NOT NULL,
                              request_count INTEGER DEFAULT 0,
                              estimated_cost_usd REAL DEFAULT 0,
                              PRIMARY KEY (service, date)
                          );
                          CREATE TABLE IF NOT EXISTS api_usage_total (
                              service TEXT PRIMARY KEY,
                              total_requests INTEGER DEFAULT 0,
                              total_cost_usd REAL DEFAULT 0
                          );
                          CREATE TABLE IF NOT EXISTS circuit_state (
                              service TEXT PRIMARY KEY,
                              status INTEGER NOT NULL DEFAULT 0,
                              failure_type INTEGER NOT NULL DEFAULT 0,
                              failure_count INTEGER NOT NULL DEFAULT 0,
                              tripped_at TEXT,
                              retry_after TEXT,
                              last_failure_reason TEXT,
                              updated_at TEXT NOT NULL
                          );
                          """;
        await cmd.ExecuteNonQueryAsync();
        _initialized = true;
    }

    private async Task<int> GetDailyCountAsync(string service, string date)
    {
        await _dbLock.WaitAsync();
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(request_count, 0) FROM api_usage WHERE service = @s AND date = @d";
            cmd.Parameters.AddWithValue("@s", service);
            cmd.Parameters.AddWithValue("@d", date);
            var result = await cmd.ExecuteScalarAsync();
            return result is long v ? (int)v : 0;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    private async Task<int> GetGlobalDailyCountAsync(string date)
    {
        await _dbLock.WaitAsync();
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(SUM(request_count), 0) FROM api_usage WHERE date = @d";
            cmd.Parameters.AddWithValue("@d", date);
            var result = await cmd.ExecuteScalarAsync();
            return result is long v ? (int)v : 0;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    private async Task<long> GetTotalCountAsync(string service)
    {
        await _dbLock.WaitAsync();
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(total_requests, 0) FROM api_usage_total WHERE service = @s";
            cmd.Parameters.AddWithValue("@s", service);
            var result = await cmd.ExecuteScalarAsync();
            return result is long v ? v : 0;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    private async Task<double> GetServiceDailyCostAsync(string service, string date)
    {
        await _dbLock.WaitAsync();
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(estimated_cost_usd, 0) FROM api_usage WHERE service = @s AND date = @d";
            cmd.Parameters.AddWithValue("@s", service);
            cmd.Parameters.AddWithValue("@d", date);
            var result = await cmd.ExecuteScalarAsync();
            return result is double v ? v : 0;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    private async Task<double> GetGlobalDailyCostAsync(string date)
    {
        await _dbLock.WaitAsync();
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(SUM(estimated_cost_usd), 0) FROM api_usage WHERE date = @d";
            cmd.Parameters.AddWithValue("@d", date);
            var result = await cmd.ExecuteScalarAsync();
            return result is double v ? v : 0;
        }
        finally
        {
            _dbLock.Release();
        }
    }
}