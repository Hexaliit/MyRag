using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Data.Models;
using Mostlylucid.DocSummarizer.Data.Services;
using Mostlylucid.DocSummarizer.Data.Services.Analysis;
using Mostlylucid.Summarizer.Core.FileAnalysis;
using Mostlylucid.Summarizer.Core.Pipeline;

namespace Mostlylucid.DocSummarizer.Data.Pipeline;

/// <summary>
///     Pipeline implementation for structured data files (CSV, JSON, Excel, Parquet).
///     Combines row chunking with wave-based data analysis for rich metadata.
/// </summary>
public class DataPipeline : PipelineBase
{
    private readonly IDataProcessor _dataProcessor;
    private readonly IFileSummarizer _fileSummarizer;
    private readonly ILogger<DataPipeline> _logger;
    private readonly DataAnalysisOrchestrator? _orchestrator;

    public DataPipeline(
        IDataProcessor dataProcessor,
        IFileSummarizer fileSummarizer,
        ILogger<DataPipeline> logger,
        DataAnalysisOrchestrator? orchestrator = null)
    {
        _dataProcessor = dataProcessor;
        _fileSummarizer = fileSummarizer;
        _logger = logger;
        _orchestrator = orchestrator;
    }

    /// <inheritdoc />
    public override string PipelineId => "data";

    /// <inheritdoc />
    public override string Name => "Data Pipeline";

    /// <inheritdoc />
    public override IReadOnlySet<string> SupportedExtensions => _dataProcessor.SupportedExtensions;

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<ContentChunk>> ProcessCoreAsync(
        string filePath,
        PipelineOptions options,
        IProgress<PipelineProgress>? progress,
        CancellationToken ct)
    {
        _logger.LogInformation("Processing data file: {FilePath}", filePath);

        // Extract universal file metadata first
        var fileMetadata = await _fileSummarizer.ExtractMetadataAsync(filePath, ct);
        var fileMetadataDict = fileMetadata.ToSearchMetadata();

        // Step 1: Run wave-based analysis (if orchestrator available)
        DynamicDataProfile? profile = null;
        if (_orchestrator != null)
        {
            progress?.Report(new PipelineProgress("Analyzing", "Running data analysis waves", 5));

            try
            {
                var dataFile = new DataFile(filePath);
                profile = await _orchestrator.AnalyzeAsync(dataFile, ct);

                _logger.LogInformation("Data analysis complete: {SignalCount} signals in {Time}ms",
                    profile.GetAllSignals().Count(), profile.ProfileTime.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Data analysis failed, continuing with basic processing");
            }
        }

        // Step 2: Process rows into chunks (legacy processor)
        progress?.Report(new PipelineProgress("Processing", "Reading data file", 30));

        var result = await _dataProcessor.ProcessAsync(filePath, ct);

        if (!result.Success) throw new InvalidOperationException(result.Error ?? "Data processing failed");

        progress?.Report(new PipelineProgress("Converting", "Converting to chunks", 80, result.Chunks.Count,
            result.Chunks.Count));

        // Step 3: Build metadata from analysis profile
        var analysisMetadata = profile?.ToMetadata() ?? new Dictionary<string, object?>();
        // Merge file metadata into analysis metadata
        foreach (var kvp in fileMetadataDict)
            analysisMetadata[kvp.Key] = kvp.Value;

        // Step 4: Convert DataChunk to ContentChunk with enriched metadata
        var chunks = result.Chunks.Select((chunk, i) =>
        {
            // Start with file metadata
            var metadata = new Dictionary<string, object?>(fileMetadataDict)
            {
                // Basic chunk info
                ["rowStart"] = chunk.RowStart,
                ["rowEnd"] = chunk.RowEnd,
                ["fileType"] = result.FileType,
                ["rowCount"] = result.RowCount,
                ["columnCount"] = result.ColumnCount,
                ["columnNames"] = result.ColumnNames
            };

            // Add analysis signals for first chunk only (to avoid duplication)
            if (i == 0 && profile != null)
            {
                // Add data domain and characterization
                var domain = profile.GetValue<string>("characterization.domain");
                if (domain != null)
                    metadata["data_domain"] = domain;

                var suggestedAnalytics = profile.GetValue<List<string>>("characterization.suggested_analytics");
                if (suggestedAnalytics != null)
                    metadata["suggested_analytics"] = suggestedAnalytics;

                // Add quality summary
                var duplicateRows = profile.GetValue<int>(DataSignalKeys.DuplicateRows);
                if (duplicateRows > 0)
                    metadata["duplicate_rows"] = duplicateRows;

                // Add profile summary text for RAG
                metadata["profile_summary"] = profile.ToSummaryText();
            }

            return new ContentChunk
            {
                Id = chunk.Id,
                Text = chunk.Text,
                ContentType = ContentType.StructuredData,
                SourcePath = filePath,
                Index = i,
                ContentHash = ComputeHash(chunk.Text),
                Metadata = metadata
            };
        }).ToList();

        // Step 5: Add a profile summary chunk if analysis was done
        if (profile != null)
        {
            var summaryText = profile.ToSummaryText();
            if (!string.IsNullOrWhiteSpace(summaryText))
                chunks.Insert(0, new ContentChunk
                {
                    Id = $"{Path.GetFileName(filePath)}:profile",
                    Text = summaryText,
                    ContentType = ContentType.StructuredData,
                    SourcePath = filePath,
                    Index = -1, // Special index for profile chunk
                    ContentHash = ComputeHash(summaryText),
                    Metadata = analysisMetadata
                });
        }

        _logger.LogInformation("Processed {ChunkCount} chunks from data file (file: {Size})",
            chunks.Count, fileMetadata.SizeFormatted);

        return chunks;
    }
}