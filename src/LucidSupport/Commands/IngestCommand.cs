using System.ComponentModel;
using LucidSupport.Services.Knowledge;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Services.Onnx;
using Spectre.Console;
using Spectre.Console.Cli;

namespace LucidSupport.Commands;

/// <summary>
///     CLI command: lucidsupport ingest [path]
///     Ingests markdown/text files into knowledge chunks with embeddings.
/// </summary>
internal sealed class IngestCommand : AsyncCommand<IngestCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<path>")]
        [Description("File or directory to ingest (*.md, *.txt)")]
        public string Path { get; init; } = "";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var path = System.IO.Path.GetFullPath(settings.Path);

        // Collect files
        List<string> files;
        if (Directory.Exists(path))
        {
            files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        else if (File.Exists(path))
        {
            files = [path];
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Path not found:[/] {path}");
            return 1;
        }

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No .md or .txt files found.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[blue]Ingesting {files.Count} file(s)...[/]");

        // Initialize ONNX embedding service
        var onnxConfig = new OnnxConfig();
        var embeddingService = new OnnxEmbeddingService(onnxConfig, verbose: false);
        await embeddingService.InitializeAsync(ct);

        var chunker = new ManualChunker();
        var totalChunks = 0;

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                foreach (var file in files)
                {
                    var fileName = System.IO.Path.GetFileName(file);
                    var task = ctx.AddTask($"[cyan]{fileName}[/]");

                    var text = await File.ReadAllTextAsync(file, ct);
                    var chunks = chunker.Chunk(text).ToList();
                    task.MaxValue = chunks.Count;

                    var manualChunks = new List<ManualChunk>();
                    var chunkIndex = 0;

                    foreach (var (chunkText, section) in chunks)
                    {
                        var embedding = await embeddingService.EmbedAsync(chunkText, ct);

                        manualChunks.Add(new ManualChunk
                        {
                            Id = $"{System.IO.Path.GetFileNameWithoutExtension(file)}-{chunkIndex:D4}",
                            Text = chunkText,
                            SourceFile = fileName,
                            Section = section,
                            Embedding = embedding
                        });

                        chunkIndex++;
                        task.Increment(1);
                    }

                    // Save to knowledge.json
                    var outputPath = System.IO.Path.ChangeExtension(file, ".knowledge.json");
                    ManualKnowledgeStore.SaveToFile(manualChunks, outputPath);

                    totalChunks += manualChunks.Count;
                    AnsiConsole.MarkupLine(
                        $"  [green]✓[/] {fileName} → {manualChunks.Count} chunks → [cyan]{System.IO.Path.GetFileName(outputPath)}[/]");

                    task.StopTask();
                }
            });

        AnsiConsole.MarkupLine($"\n[green]Done![/] {totalChunks} chunks from {files.Count} file(s).");
        return 0;
    }
}
