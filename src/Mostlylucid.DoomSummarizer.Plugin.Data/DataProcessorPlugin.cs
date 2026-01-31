using DoomSummarizer.Models;
using DoomSummarizer.Plugins;
using Mostlylucid.DoomSummarizer.Plugin.Data.Commands;
using Spectre.Console.Cli;

namespace Mostlylucid.DoomSummarizer.Plugin.Data;

/// <summary>
/// Data analysis plugin — statistical profiling, schema detection, and constraint validation
/// for tabular data files (CSV, Excel, Parquet, JSON).
/// </summary>
public class DataProcessorPlugin : IProcessorPlugin, ICliPlugin
{
    public ProcessorPluginMetadata Metadata { get; } = new()
    {
        Name = "data",
        DisplayName = "Data Analyzer",
        Description = "Statistical profiling, schema detection, and constraint validation for tabular data",
        Version = "1.0.0",
        SupportedExtensions = [".csv", ".xlsx", ".xls", ".parquet", ".json", ".jsonl", ".tsv"],
        MinActivationWords = 0,
        DocumentTypes = ["data", "tabular"]
    };

    public PluginCliMetadata CliMetadata { get; } = new()
    {
        CommandName = "data",
        Description = "Data analysis: statistical profiling, schema detection, constraint validation",
        Examples = ["data profile dataset.csv", "data schema data.parquet"]
    };

    public void ConfigureCommands(IConfigurator config)
    {
        config.AddBranch("data", data =>
        {
            data.SetDescription(CliMetadata.Description);
            data.AddCommand<DataProfileCommand>("profile")
                .WithDescription("Run statistical profiling on a data file")
                .WithExample("data", "profile", "dataset.csv");
            data.AddCommand<DataSchemaCommand>("schema")
                .WithDescription("Detect and display schema of a data file")
                .WithExample("data", "schema", "data.parquet");
        });
    }

    public IReadOnlyList<IDocumentReader> Readers => [];
    public IReadOnlyList<IDocumentSplitter> Splitters => [];
    public IReadOnlyList<TemplateDefinition> Templates => [];

    public Task InitializeAsync(ProcessorPluginServices services, CancellationToken ct = default)
        => Task.CompletedTask;

    public bool CanProcess(string markdown, ProcessingContext context)
    {
        if (context.FileName == null) return false;
        var ext = Path.GetExtension(context.FileName);
        return Metadata.SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    public Task<ProcessorResult> ProcessAsync(string markdown, ProcessorOptions options, CancellationToken ct = default)
        => throw new NotSupportedException("Data processing uses IPipeline. Use CLI commands for direct data analysis.");
}
