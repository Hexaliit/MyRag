using System.Collections.Concurrent;
using DoomSummarizer.Models;
using Microsoft.Data.Sqlite;
namespace DoomSummarizer.Services;

/// <summary>
/// Persistent circuit breaker with failure-type-aware retry semantics.
/// SQLite-backed — circuit state survives restarts.
/// Replaces per-service _dailyLimitExhausted flags with smart retry timing.
/// </summary>
public class CircuitBreakerService : IAsyncDisposable
{
    private readonly SqliteConnection _db;
    private readonly ConcurrentDictionary<string, CircuitEntry> _cache = new();
    private bool _initialized;

    public CircuitBreakerService(string dbPath)
    {
        _db = new SqliteConnection($"Data Source={dbPath}");
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        await _db.OpenAsync();

        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
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

        // Load existing state into cache
        await LoadAllAsync();
        _initialized = true;
    }

    /// <summary>
    /// Fast synchronous check for IsAvailable properties.
    /// Returns true if the circuit is open (service should NOT be used).
    /// </summary>
    public bool IsCircuitOpen(string service)
    {
        if (!_cache.TryGetValue(service, out var entry))
            return false;

        if (entry.Status == CircuitStatus.Closed)
            return false;

        if (entry.RetryAfter == DateTimeOffset.MaxValue)
            return true; // permanent (auth error, lifetime limit)

        // Check if retry window has passed — if so, allow a probe
        if (DateTimeOffset.UtcNow >= entry.RetryAfter)
            return false; // will transition to half-open on next async check

        return true;
    }

    /// <summary>
    /// Full async check — loads from DB if not cached, handles half-open transition.
    /// Returns true if the service is available for requests.
    /// </summary>
    public async Task<bool> IsServiceAvailableAsync(string service)
    {
        var entry = await GetOrLoadAsync(service);

        if (entry.Status == CircuitStatus.Closed)
            return true;

        if (entry.RetryAfter == DateTimeOffset.MaxValue)
            return false; // permanent

        if (DateTimeOffset.UtcNow >= entry.RetryAfter)
        {
            // Transition to half-open — allow one probe
            await TransitionAsync(service, CircuitStatus.HalfOpen);
            return true;
        }

        return false; // still waiting
    }

    /// <summary>
    /// Report a successful request — closes circuit, resets failure count.
    /// </summary>
    public async Task ReportSuccessAsync(string service)
    {
        var entry = new CircuitEntry
        {
            Service = service,
            Status = CircuitStatus.Closed,
            FailureType = CircuitFailureType.None,
            FailureCount = 0,
            TrippedAt = default,
            RetryAfter = default,
            LastFailureReason = null,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _cache[service] = entry;
        await PersistAsync(entry);
    }

    /// <summary>
    /// Report a failure — classifies, computes retry_after, persists.
    /// </summary>
    public async Task ReportFailureAsync(string service, CircuitFailureType failureType, string? reason = null)
    {
        var existing = await GetOrLoadAsync(service);
        var failureCount = existing.Status != CircuitStatus.Closed
            ? existing.FailureCount + 1
            : 1;

        var now = DateTimeOffset.UtcNow;
        var retryAfter = ComputeRetryAfter(failureType, failureCount);

        var entry = new CircuitEntry
        {
            Service = service,
            Status = CircuitStatus.Open,
            FailureType = failureType,
            FailureCount = failureCount,
            TrippedAt = existing.Status == CircuitStatus.Closed ? now : existing.TrippedAt,
            RetryAfter = retryAfter,
            LastFailureReason = reason,
            UpdatedAt = now
        };

        _cache[service] = entry;
        await PersistAsync(entry);
    }

    /// <summary>
    /// Trip circuit directly (called by budget service on denial).
    /// </summary>
    public async Task TripCircuitAsync(string service, CircuitFailureType failureType, string? reason = null)
    {
        await ReportFailureAsync(service, failureType, reason);
        System.Diagnostics.Debug.WriteLine($"{service}: circuit tripped ({failureType}) -- {reason}");
    }

    /// <summary>
    /// Get current state for diagnostics/status display.
    /// </summary>
    public CircuitEntry? GetState(string service)
    {
        return _cache.TryGetValue(service, out var entry) ? entry : null;
    }

    /// <summary>
    /// Print status table for --debug flag. Only shows non-Closed circuits.
    /// </summary>
    public void PrintCircuitStatus()
    {
        var openCircuits = _cache.Values
            .Where(e => e.Status != CircuitStatus.Closed)
            .OrderBy(e => e.Service)
            .ToList();

        if (openCircuits.Count == 0)
        {
            System.Diagnostics.Debug.WriteLine("All circuits closed (normal operation)");
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Circuit Breaker Status:");
        sb.AppendLine("Service | Status | Failure Type | Failures | Retry After | Last Reason");
        sb.AppendLine(new string('-', 80));

        foreach (var e in openCircuits)
        {
            var retryStr = e.RetryAfter == DateTimeOffset.MaxValue
                ? "never"
                : e.RetryAfter.ToString("yyyy-MM-dd HH:mm:ss UTC");

            sb.AppendLine(
                $"{e.Service} | {e.Status} | {e.FailureType} | {e.FailureCount} | {retryStr} | {Truncate(e.LastFailureReason ?? "", 40)}");
        }

        System.Diagnostics.Debug.WriteLine(sb.ToString());
    }

    /// <summary>
    /// Classify an HTTP status code into a CircuitFailureType.
    /// </summary>
    public static CircuitFailureType ClassifyHttpStatus(int statusCode) => statusCode switch
    {
        429 => CircuitFailureType.RateLimit,
        401 or 403 => CircuitFailureType.AuthError,
        >= 500 => CircuitFailureType.ServerError,
        _ => CircuitFailureType.ServerError
    };

    /// <summary>
    /// Classify a budget denial reason into a CircuitFailureType.
    /// </summary>
    public static CircuitFailureType ClassifyBudgetDenial(string? reason) => reason switch
    {
        _ when reason?.Contains("daily limit", StringComparison.OrdinalIgnoreCase) == true => CircuitFailureType.DailyLimit,
        _ when reason?.Contains("lifetime limit", StringComparison.OrdinalIgnoreCase) == true => CircuitFailureType.LifetimeLimit,
        _ when reason?.Contains("daily budget", StringComparison.OrdinalIgnoreCase) == true => CircuitFailureType.BudgetExhausted,
        _ when reason?.Contains("Global daily limit", StringComparison.OrdinalIgnoreCase) == true => CircuitFailureType.DailyLimit,
        _ when reason?.Contains("Global daily budget", StringComparison.OrdinalIgnoreCase) == true => CircuitFailureType.BudgetExhausted,
        _ when reason?.Contains("disabled", StringComparison.OrdinalIgnoreCase) == true => CircuitFailureType.AuthError,
        _ => CircuitFailureType.DailyLimit
    };

    private static DateTimeOffset ComputeRetryAfter(CircuitFailureType type, int failureCount)
    {
        var now = DateTimeOffset.UtcNow;
        return type switch
        {
            CircuitFailureType.DailyLimit or CircuitFailureType.BudgetExhausted =>
                now.Date.AddDays(1), // next UTC midnight

            CircuitFailureType.RateLimit => failureCount switch
            {
                <= 1 => now.AddMinutes(5),
                2 => now.AddMinutes(15),
                3 => now.AddHours(1),
                4 => now.AddHours(4),
                _ => now.Date.AddDays(1) // give up until tomorrow
            },

            CircuitFailureType.ServerError => failureCount switch
            {
                <= 1 => now.AddMinutes(1),
                2 => now.AddMinutes(5),
                3 => now.AddMinutes(15),
                4 => now.AddHours(1),
                _ => now.AddHours(4)
            },

            CircuitFailureType.AuthError or CircuitFailureType.LifetimeLimit =>
                DateTimeOffset.MaxValue, // never retry

            _ => now.AddMinutes(5)
        };
    }

    private async Task TransitionAsync(string service, CircuitStatus newStatus)
    {
        if (!_cache.TryGetValue(service, out var existing))
            return;

        var updated = existing with
        {
            Status = newStatus,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _cache[service] = updated;
        await PersistAsync(updated);
    }

    private async Task<CircuitEntry> GetOrLoadAsync(string service)
    {
        if (_cache.TryGetValue(service, out var cached))
            return cached;

        // Try loading from DB
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT status, failure_type, failure_count, tripped_at, retry_after,
                   last_failure_reason, updated_at
            FROM circuit_state WHERE service = @s
            """;
        cmd.Parameters.AddWithValue("@s", service);

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var entry = new CircuitEntry
            {
                Service = service,
                Status = (CircuitStatus)reader.GetInt32(0),
                FailureType = (CircuitFailureType)reader.GetInt32(1),
                FailureCount = reader.GetInt32(2),
                TrippedAt = ParseDateTimeOffset(reader.IsDBNull(3) ? null : reader.GetString(3)),
                RetryAfter = ParseDateTimeOffset(reader.IsDBNull(4) ? null : reader.GetString(4)),
                LastFailureReason = reader.IsDBNull(5) ? null : reader.GetString(5),
                UpdatedAt = ParseDateTimeOffset(reader.IsDBNull(6) ? null : reader.GetString(6))
            };
            _cache[service] = entry;
            return entry;
        }

        // No record — circuit is closed (default)
        var defaultEntry = new CircuitEntry { Service = service };
        _cache[service] = defaultEntry;
        return defaultEntry;
    }

    private async Task LoadAllAsync()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT service, status, failure_type, failure_count, tripped_at,
                   retry_after, last_failure_reason, updated_at
            FROM circuit_state
            """;

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var entry = new CircuitEntry
            {
                Service = reader.GetString(0),
                Status = (CircuitStatus)reader.GetInt32(1),
                FailureType = (CircuitFailureType)reader.GetInt32(2),
                FailureCount = reader.GetInt32(3),
                TrippedAt = ParseDateTimeOffset(reader.IsDBNull(4) ? null : reader.GetString(4)),
                RetryAfter = ParseDateTimeOffset(reader.IsDBNull(5) ? null : reader.GetString(5)),
                LastFailureReason = reader.IsDBNull(6) ? null : reader.GetString(6),
                UpdatedAt = ParseDateTimeOffset(reader.IsDBNull(7) ? null : reader.GetString(7))
            };
            _cache[entry.Service] = entry;
        }
    }

    private async Task PersistAsync(CircuitEntry entry)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO circuit_state (service, status, failure_type, failure_count,
                                       tripped_at, retry_after, last_failure_reason, updated_at)
            VALUES (@service, @status, @failureType, @failureCount,
                    @trippedAt, @retryAfter, @reason, @updatedAt)
            ON CONFLICT(service) DO UPDATE SET
                status = @status,
                failure_type = @failureType,
                failure_count = @failureCount,
                tripped_at = @trippedAt,
                retry_after = @retryAfter,
                last_failure_reason = @reason,
                updated_at = @updatedAt
            """;

        cmd.Parameters.AddWithValue("@service", entry.Service);
        cmd.Parameters.AddWithValue("@status", (int)entry.Status);
        cmd.Parameters.AddWithValue("@failureType", (int)entry.FailureType);
        cmd.Parameters.AddWithValue("@failureCount", entry.FailureCount);
        cmd.Parameters.AddWithValue("@trippedAt",
            entry.TrippedAt == default ? DBNull.Value : entry.TrippedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@retryAfter",
            entry.RetryAfter == default ? DBNull.Value :
            entry.RetryAfter == DateTimeOffset.MaxValue ? "9999-12-31T23:59:59+00:00" :
            entry.RetryAfter.ToString("O"));
        cmd.Parameters.AddWithValue("@reason", (object?)entry.LastFailureReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@updatedAt", entry.UpdatedAt.ToString("O"));

        await cmd.ExecuteNonQueryAsync();
    }

    private static DateTimeOffset ParseDateTimeOffset(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return default;
        if (value == "9999-12-31T23:59:59+00:00")
            return DateTimeOffset.MaxValue;
        return DateTimeOffset.TryParse(value, out var result) ? result : default;
    }

    private static string Truncate(string s, int max) =>
        s.Length > max ? s[..max] + "..." : s;

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
