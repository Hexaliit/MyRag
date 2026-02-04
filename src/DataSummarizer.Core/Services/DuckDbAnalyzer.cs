using System.Data;
using System.Runtime.CompilerServices;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Data.Models;

namespace Mostlylucid.DocSummarizer.Data.Services;

/// <summary>
///     Ephemeral DuckDB analyzer for streaming data analysis.
///     Creates an in-memory database per analysis session.
/// </summary>
public class DuckDbAnalyzer : IDisposable
{
    private readonly ILogger<DuckDbAnalyzer>? _logger;
    private DuckDBConnection? _connection;
    private DataFile? _currentFile;

    public DuckDbAnalyzer(ILogger<DuckDbAnalyzer>? logger = null)
    {
        _logger = logger;
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
        _currentFile = null;
    }

    /// <summary>
    ///     Open an ephemeral DuckDB connection for analyzing a data file.
    /// </summary>
    public async Task OpenAsync(DataFile file, CancellationToken ct = default)
    {
        // Close any existing connection
        await CloseAsync();

        _currentFile = file;

        // Create in-memory DuckDB
        _connection = new DuckDBConnection("DataSource=:memory:");
        await _connection.OpenAsync(ct);

        _logger?.LogDebug("Opened ephemeral DuckDB connection for {FileName}", file.FileName);

        // Excel files need special handling - can't use DuckDB directly
        if (file.Format == DataFormat.Excel)
        {
            _logger?.LogDebug("Excel file detected - DuckDB analytics limited");
            return;
        }

        // Create a view for the data file
        var readExpr = file.GetDuckDbReadExpression();
        var createViewSql = $"CREATE VIEW data AS SELECT * FROM {readExpr}";

        try
        {
            await ExecuteNonQueryAsync(createViewSql, ct);
            _logger?.LogDebug("Created DuckDB view for {FileName}", file.FileName);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to create DuckDB view for {FileName}", file.FileName);
            throw;
        }
    }

    /// <summary>
    ///     Execute a scalar query and return the result.
    /// </summary>
    public async Task<T?> QueryScalarAsync<T>(string sql, CancellationToken ct = default)
    {
        EnsureConnected();

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;

        var result = await cmd.ExecuteScalarAsync(ct);
        if (result == null || result == DBNull.Value)
            return default;

        return (T)Convert.ChangeType(result, typeof(T));
    }

    /// <summary>
    ///     Execute a query and return results as dictionaries.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> QueryAsync(
        string sql,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureConnected();

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var columns = new List<string>();
        for (var i = 0; i < reader.FieldCount; i++) columns.Add(reader.GetName(i));

        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < columns.Count; i++) row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            yield return row;
        }
    }

    /// <summary>
    ///     Execute a query and return results as a data table.
    /// </summary>
    public async Task<DataTable> QueryTableAsync(string sql, CancellationToken ct = default)
    {
        EnsureConnected();

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var table = new DataTable();
        table.Load(reader);
        return table;
    }

    /// <summary>
    ///     Execute a non-query command.
    /// </summary>
    public async Task<int> ExecuteNonQueryAsync(string sql, CancellationToken ct = default)
    {
        EnsureConnected();

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    ///     Get row count from the data view.
    /// </summary>
    public async Task<long> GetRowCountAsync(CancellationToken ct = default)
    {
        return await QueryScalarAsync<long>("SELECT COUNT(*) FROM data", ct);
    }

    /// <summary>
    ///     Get column names from the data view.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetColumnNamesAsync(CancellationToken ct = default)
    {
        var columns = new List<string>();

        await foreach (var row in QueryAsync("DESCRIBE data", ct))
            if (row.TryGetValue("column_name", out var name) && name is string colName)
                columns.Add(colName);

        return columns;
    }

    /// <summary>
    ///     Get DuckDB's summarize output for basic statistics.
    /// </summary>
    public async Task<DataTable> GetSummarizeAsync(CancellationToken ct = default)
    {
        return await QueryTableAsync("SUMMARIZE data", ct);
    }

    /// <summary>
    ///     Sample N rows from the data.
    /// </summary>
    public async Task<List<Dictionary<string, object?>>> SampleRowsAsync(int count, CancellationToken ct = default)
    {
        var rows = new List<Dictionary<string, object?>>();

        await foreach (var row in QueryAsync($"SELECT * FROM data USING SAMPLE {count}", ct))
            rows.Add(new Dictionary<string, object?>(row));

        return rows;
    }

    /// <summary>
    ///     Get distinct value count for a column.
    /// </summary>
    public async Task<long> GetCardinalityAsync(string column, CancellationToken ct = default)
    {
        return await QueryScalarAsync<long>($"SELECT COUNT(DISTINCT \"{column}\") FROM data", ct);
    }

    /// <summary>
    ///     Get top N values by frequency for a column.
    /// </summary>
    public async Task<Dictionary<string, int>> GetTopValuesAsync(string column, int limit = 10,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, int>();
        var sql = $"""
                   SELECT "{column}" as val, COUNT(*) as cnt
                   FROM data
                   WHERE "{column}" IS NOT NULL
                   GROUP BY "{column}"
                   ORDER BY cnt DESC
                   LIMIT {limit}
                   """;

        await foreach (var row in QueryAsync(sql, ct))
        {
            var val = row["val"]?.ToString() ?? "(null)";
            var cnt = Convert.ToInt32(row["cnt"]);
            result[val] = cnt;
        }

        return result;
    }

    /// <summary>
    ///     Get null count for a column.
    /// </summary>
    public async Task<long> GetNullCountAsync(string column, CancellationToken ct = default)
    {
        return await QueryScalarAsync<long>($"SELECT COUNT(*) FROM data WHERE \"{column}\" IS NULL", ct);
    }

    /// <summary>
    ///     Get basic statistics for a numeric column.
    /// </summary>
    public async Task<(double min, double max, double avg, double stddev)?> GetNumericStatsAsync(
        string column, CancellationToken ct = default)
    {
        var sql = $"""
                   SELECT
                       MIN("{column}")::DOUBLE as min_val,
                       MAX("{column}")::DOUBLE as max_val,
                       AVG("{column}")::DOUBLE as avg_val,
                       STDDEV("{column}")::DOUBLE as stddev_val
                   FROM data
                   WHERE "{column}" IS NOT NULL
                   """;

        await foreach (var row in QueryAsync(sql, ct))
        {
            if (row["min_val"] == null) return null;

            return (
                Convert.ToDouble(row["min_val"]),
                Convert.ToDouble(row["max_val"]),
                Convert.ToDouble(row["avg_val"]),
                Convert.ToDouble(row["stddev_val"] ?? 0)
            );
        }

        return null;
    }

    /// <summary>
    ///     Get quartiles for a numeric column.
    /// </summary>
    public async Task<(double q1, double median, double q3)?> GetQuartilesAsync(
        string column, CancellationToken ct = default)
    {
        var sql = $"""
                   SELECT
                       PERCENTILE_CONT(0.25) WITHIN GROUP (ORDER BY "{column}")::DOUBLE as q1,
                       PERCENTILE_CONT(0.50) WITHIN GROUP (ORDER BY "{column}")::DOUBLE as median,
                       PERCENTILE_CONT(0.75) WITHIN GROUP (ORDER BY "{column}")::DOUBLE as q3
                   FROM data
                   WHERE "{column}" IS NOT NULL
                   """;

        await foreach (var row in QueryAsync(sql, ct))
        {
            if (row["median"] == null) return null;

            return (
                Convert.ToDouble(row["q1"]),
                Convert.ToDouble(row["median"]),
                Convert.ToDouble(row["q3"])
            );
        }

        return null;
    }

    /// <summary>
    ///     Get correlation between two numeric columns.
    /// </summary>
    public async Task<double?> GetCorrelationAsync(string col1, string col2, CancellationToken ct = default)
    {
        var sql = $"""
                   SELECT CORR("{col1}"::DOUBLE, "{col2}"::DOUBLE) as correlation
                   FROM data
                   WHERE "{col1}" IS NOT NULL AND "{col2}" IS NOT NULL
                   """;

        return await QueryScalarAsync<double?>(sql, ct);
    }

    /// <summary>
    ///     Get duplicate row count.
    /// </summary>
    public async Task<int> GetDuplicateCountAsync(CancellationToken ct = default)
    {
        var sql = """
                  SELECT COUNT(*) - COUNT(DISTINCT *) as dups
                  FROM data
                  """;

        return await QueryScalarAsync<int>(sql, ct);
    }

    /// <summary>
    ///     Close the DuckDB connection.
    /// </summary>
    public async Task CloseAsync()
    {
        if (_connection != null)
        {
            await _connection.CloseAsync();
            await _connection.DisposeAsync();
            _connection = null;
            _currentFile = null;
        }
    }

    private void EnsureConnected()
    {
        if (_connection == null || _connection.State != ConnectionState.Open)
            throw new InvalidOperationException("DuckDB connection is not open. Call OpenAsync first.");
    }
}