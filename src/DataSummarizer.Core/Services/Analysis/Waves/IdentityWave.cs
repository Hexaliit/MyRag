using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Data.Models;

namespace Mostlylucid.DocSummarizer.Data.Services.Analysis.Waves;

/// <summary>
/// First wave: extracts fundamental file and schema information.
/// </summary>
public class IdentityWave : IDataAnalysisWave
{
    private readonly DuckDbAnalyzer _analyzer;
    private readonly ILogger<IdentityWave>? _logger;

    public IdentityWave(DuckDbAnalyzer analyzer, ILogger<IdentityWave>? logger = null)
    {
        _analyzer = analyzer;
        _logger = logger;
    }

    public string Name => "IdentityWave";
    public int Priority => 110;
    public IReadOnlyList<string> Tags => [DataSignalTags.Identity];

    public async Task<IEnumerable<DataSignal>> AnalyzeAsync(
        DataFile file,
        DataAnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<DataSignal>();

        // File identity
        signals.Add(CreateSignal(DataSignalKeys.Format, file.Format.ToString().ToLowerInvariant()));
        signals.Add(CreateSignal(DataSignalKeys.SizeBytes, file.SizeBytes));

        // For Excel files, we need special handling (DuckDB can't read Excel directly)
        if (file.Format == DataFormat.Excel)
        {
            return await AnalyzeExcelAsync(file, signals, ct);
        }

        // Open DuckDB for the file
        try
        {
            await _analyzer.OpenAsync(file, ct);

            // Get row count
            var rowCount = await _analyzer.GetRowCountAsync(ct);
            signals.Add(CreateSignal(DataSignalKeys.RowCount, rowCount));

            // Get column names
            var columns = await _analyzer.GetColumnNamesAsync(ct);
            signals.Add(CreateSignal(DataSignalKeys.ColumnNames, columns.ToArray()));
            signals.Add(CreateSignal(DataSignalKeys.ColumnCount, columns.Count));

            _logger?.LogDebug("IdentityWave: {FileName} has {RowCount} rows, {ColCount} columns",
                file.FileName, rowCount, columns.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "IdentityWave: Failed to analyze {FileName} with DuckDB", file.FileName);

            // Add error signal but still return what we have
            signals.Add(new DataSignal
            {
                Key = "error.identity.duckdb",
                Value = ex.Message,
                Source = Name,
                Confidence = 1.0,
                Tags = [DataSignalTags.Identity, "error"]
            });
        }

        return signals;
    }

    private async Task<IEnumerable<DataSignal>> AnalyzeExcelAsync(
        DataFile file,
        List<DataSignal> signals,
        CancellationToken ct)
    {
        // Use ClosedXML for Excel files
        try
        {
            using var workbook = new ClosedXML.Excel.XLWorkbook(file.FilePath);
            var worksheet = workbook.Worksheets.First();
            var usedRange = worksheet.RangeUsed();

            if (usedRange != null)
            {
                var rowCount = usedRange.RowCount() - 1; // Exclude header
                var headerRow = usedRange.FirstRow();
                var columns = headerRow.Cells()
                    .Select(c => c.GetString() ?? $"Column{c.Address.ColumnNumber}")
                    .ToArray();

                signals.Add(CreateSignal(DataSignalKeys.RowCount, (long)rowCount));
                signals.Add(CreateSignal(DataSignalKeys.ColumnNames, columns));
                signals.Add(CreateSignal(DataSignalKeys.ColumnCount, columns.Length));
            }
            else
            {
                signals.Add(CreateSignal(DataSignalKeys.RowCount, 0L));
                signals.Add(CreateSignal(DataSignalKeys.ColumnNames, Array.Empty<string>()));
                signals.Add(CreateSignal(DataSignalKeys.ColumnCount, 0));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "IdentityWave: Failed to analyze Excel file {FileName}", file.FileName);
            signals.Add(new DataSignal
            {
                Key = "error.identity.excel",
                Value = ex.Message,
                Source = Name,
                Confidence = 1.0,
                Tags = [DataSignalTags.Identity, "error"]
            });
        }

        return signals;
    }

    private DataSignal CreateSignal(string key, object? value)
    {
        return new DataSignal
        {
            Key = key,
            Value = value,
            Source = Name,
            Confidence = 1.0,
            Tags = [DataSignalTags.Identity]
        };
    }
}
