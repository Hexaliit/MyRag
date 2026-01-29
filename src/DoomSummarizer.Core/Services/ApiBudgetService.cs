using DoomSummarizer.Models;
using Microsoft.Data.Sqlite;
namespace DoomSummarizer.Services;

/// <summary>
/// Central budget tracker for all paid/limited APIs.
/// Reads per-service limits from ApiKeyEntry definitions.
/// Global limits from ApiBudgetConfig.
/// SQLite-backed usage tracking.
/// </summary>
public class ApiBudgetService : IAsyncDisposable
{
    private readonly ApiBudgetConfig _globalConfig;
    private readonly ApiKeyService _keys;
    private readonly SqliteConnection _db;
    private readonly SemaphoreSlim _dbLock = new(1, 1);  // Thread-safety for SQLite connection
    private bool _initialized;

    public ApiBudgetService(ApiBudgetConfig globalConfig, ApiKeyService keys, string dbPath)
    {
        _globalConfig = globalConfig;
        _keys = keys;
        _db = new SqliteConnection($"Data Source={dbPath}");
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

    /// <summary>
    /// Check if a request is allowed (within budget). Does NOT record it.
    /// </summary>
    public async Task<BudgetCheckResult> CheckBudgetAsync(string service)
    {
        await InitializeAsync();

        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        var svcEntry = _keys.GetService(service);
        var costPerRequest = svcEntry?.CostPerRequest ?? 0.005;

        // Check service is enabled
        if (svcEntry is { Enabled: false })
            return BudgetCheckResult.Denied($"{service}: disabled in config");

        // Check per-service daily limit
        var dailyCount = await GetDailyCountAsync(service, today);
        var serviceLimit = svcEntry?.MaxRequestsPerDay ?? _globalConfig.GlobalMaxRequestsPerDay;
        if (serviceLimit > 0 && dailyCount >= serviceLimit)
            return BudgetCheckResult.Denied($"{service}: daily limit reached ({dailyCount}/{serviceLimit})");

        // Check per-service lifetime limit
        if (svcEntry is { MaxRequests: > 0 })
        {
            var totalCount = await GetTotalCountAsync(service);
            if (totalCount >= svcEntry.MaxRequests)
                return BudgetCheckResult.Denied($"{service}: lifetime limit reached ({totalCount}/{svcEntry.MaxRequests})");
        }

        // Check per-service daily budget
        if (svcEntry is { DailyBudgetUsd: > 0 })
        {
            var serviceDailyCost = await GetServiceDailyCostAsync(service, today);
            if (serviceDailyCost + costPerRequest > svcEntry.DailyBudgetUsd)
                return BudgetCheckResult.Denied(
                    $"{service}: daily budget exhausted (${serviceDailyCost:F3}/${svcEntry.DailyBudgetUsd:F2})");
        }

        // Check global daily request limit
        var globalDailyCount = await GetGlobalDailyCountAsync(today);
        if (_globalConfig.GlobalMaxRequestsPerDay > 0 && globalDailyCount >= _globalConfig.GlobalMaxRequestsPerDay)
            return BudgetCheckResult.Denied(
                $"Global daily limit reached ({globalDailyCount}/{_globalConfig.GlobalMaxRequestsPerDay})");

        // Check global daily budget
        if (_globalConfig.GlobalDailyBudgetUsd > 0)
        {
            var globalDailyCost = await GetGlobalDailyCostAsync(today);
            if (globalDailyCost + costPerRequest > _globalConfig.GlobalDailyBudgetUsd)
                return BudgetCheckResult.Denied(
                    $"Global daily budget exhausted (${globalDailyCost:F3}/${_globalConfig.GlobalDailyBudgetUsd:F2})");
        }

        return BudgetCheckResult.Allowed(dailyCount, serviceLimit, costPerRequest);
    }

    /// <summary>
    /// Record a completed API request.
    /// </summary>
    public async Task RecordUsageAsync(string service, int count = 1)
    {
        await InitializeAsync();

        var svcEntry = _keys.GetService(service);
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        var cost = (svcEntry?.CostPerRequest ?? 0.005) * count;

        await _dbLock.WaitAsync();
        try
        {
            // Upsert daily
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

            // Upsert total
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

    /// <summary>
    /// Print current usage summary.
    /// </summary>
    public async Task PrintUsageSummaryAsync()
    {
        await InitializeAsync();

        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        var hasRows = false;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Service | Today | Cost Today | All Time | Total Cost");
        sb.AppendLine(new string('-', 60));

        await _dbLock.WaitAsync();
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT u.service, u.request_count, u.estimated_cost_usd,
                       COALESCE(t.total_requests, 0), COALESCE(t.total_cost_usd, 0)
                FROM api_usage u
                LEFT JOIN api_usage_total t ON u.service = t.service
                WHERE u.date = @date
                ORDER BY u.service
                """;
            cmd.Parameters.AddWithValue("@date", today);

            using var reader = await cmd.ExecuteReaderAsync();
            hasRows = reader.HasRows;

            while (await reader.ReadAsync())
            {
                var svc = reader.GetString(0);
                var svcEntry = _keys.GetService(svc);
                var limit = svcEntry?.MaxRequestsPerDay ?? _globalConfig.GlobalMaxRequestsPerDay;
                sb.AppendLine(
                    $"{svc} | {reader.GetInt32(1)}/{limit} | ${reader.GetDouble(2):F3} | {reader.GetInt64(3)} | ${reader.GetDouble(4):F3}");
            }
        }
        finally
        {
            _dbLock.Release();
        }

        if (!hasRows)
        {
            System.Diagnostics.Debug.WriteLine("No API usage today");
            return;
        }

        System.Diagnostics.Debug.WriteLine(sb.ToString());

        var globalCost = await GetGlobalDailyCostAsync(today);
        if (_globalConfig.GlobalDailyBudgetUsd > 0)
            System.Diagnostics.Debug.WriteLine($"  Global daily budget: ${globalCost:F3} / ${_globalConfig.GlobalDailyBudgetUsd:F2}");
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

    public async ValueTask DisposeAsync()
    {
        _dbLock.Dispose();
        await _db.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

public record BudgetCheckResult
{
    public bool IsAllowed { get; init; }
    public string? DenialReason { get; init; }
    public int DailyCount { get; init; }
    public int DailyLimit { get; init; }
    public double CostPerRequest { get; init; }

    public static BudgetCheckResult Allowed(int dailyCount, int dailyLimit, double costPerRequest) =>
        new() { IsAllowed = true, DailyCount = dailyCount, DailyLimit = dailyLimit, CostPerRequest = costPerRequest };

    public static BudgetCheckResult Denied(string reason) =>
        new() { IsAllowed = false, DenialReason = reason };
}
