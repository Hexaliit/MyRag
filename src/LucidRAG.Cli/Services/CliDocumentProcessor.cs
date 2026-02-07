using System.Security.Cryptography;
using System.Threading.Channels;
using LucidRAG.Data;
using LucidRAG.Entities;
using Microsoft.EntityFrameworkCore;
using Mostlylucid.DocSummarizer;
using Mostlylucid.DocSummarizer.Services;
using Spectre.Console;

namespace LucidRAG.Cli.Services;

/// <summary>
///     Synchronous document processor for CLI usage with Spectre progress bars
/// </summary>
public class CliDocumentProcessor(
    RagDocumentsDbContext db,
    IDocumentSummarizer summarizer,
    IVectorStore vectorStore,
    CliConfig config)
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".md", ".txt", ".html"
    };

    public async Task<IndexResult> IndexFileAsync(string filePath, Guid? collectionId, CancellationToken ct = default)
    {
        var filename = Path.GetFileName(filePath);
        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        if (!AllowedExtensions.Contains(extension))
            return new IndexResult(false, $"Unsupported file type: {extension}");

        if (!File.Exists(filePath)) return new IndexResult(false, "File not found");

        // Compute hash
        await using var fileStream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        var hashBytes = await sha.ComputeHashAsync(fileStream, ct);
        var contentHash = Convert.ToHexString(hashBytes[..16]).ToLowerInvariant();
        fileStream.Position = 0;

        // Check for duplicate
        var existing = await db.Documents
            .FirstOrDefaultAsync(d => d.ContentHash == contentHash && d.CollectionId == collectionId, ct);
        if (existing != null) return new IndexResult(true, "Already indexed", existing.Id, existing.SegmentCount);

        // Create document entity
        var documentId = Guid.NewGuid();
        var uploadDir = Path.Combine(config.DataDirectory, "uploads", documentId.ToString());
        Directory.CreateDirectory(uploadDir);
        var savedPath = Path.Combine(uploadDir, filename);
        File.Copy(filePath, savedPath, true);

        var document = new DocumentEntity
        {
            Id = documentId,
            CollectionId = collectionId,
            Name = Path.GetFileNameWithoutExtension(filename),
            OriginalFilename = filename,
            ContentHash = contentHash,
            FilePath = savedPath,
            FileSizeBytes = new FileInfo(savedPath).Length,
            MimeType = GetMimeType(extension),
            Status = DocumentStatus.Processing
        };

        db.Documents.Add(document);
        await db.SaveChangesAsync(ct);

        // Create progress channel for DocSummarizer
        var progressChannel = Channel.CreateUnbounded<ProgressUpdate>();

        // Process with progress display
        var segmentCount = 0;
        try
        {
            await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask($"[cyan]Processing {filename}[/]");
                    task.MaxValue = 100;

                    // Start processing in background
                    var processTask = summarizer.SummarizeFileAsync(
                        savedPath,
                        progressChannel.Writer,
                        cancellationToken: ct);

                    // Read progress updates
                    await foreach (var update in progressChannel.Reader.ReadAllAsync(ct))
                    {
                        task.Description = $"[cyan]{update.Stage}: {update.Message}[/]";
                        task.Value = update.PercentComplete;

                        if (update.Type == ProgressType.Completed)
                            break;
                        if (update.Type == ProgressType.Error)
                            throw new Exception(update.Message);
                    }

                    var result = await processTask;
                    segmentCount = result.Trace.TotalChunks;
                    task.Value = 100;
                });

            // Update document status
            document.Status = DocumentStatus.Completed;
            document.SegmentCount = segmentCount;
            document.ProcessedAt = DateTimeOffset.UtcNow;

            // Propagate YAML frontmatter metadata (venue quality, etc.)
            PropagateFileMetadata(document, savedPath);

            await db.SaveChangesAsync(ct);

            return new IndexResult(true, "Indexed successfully", documentId, segmentCount);
        }
        catch (Exception ex)
        {
            document.Status = DocumentStatus.Failed;
            document.StatusMessage = ex.Message;
            await db.SaveChangesAsync(ct);
            return new IndexResult(false, ex.Message);
        }
    }

    public async Task<List<IndexResult>> IndexDirectoryAsync(
        string directory,
        Guid? collectionId,
        bool recursive,
        CancellationToken ct = default)
    {
        var results = new List<IndexResult>();
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        var files = AllowedExtensions
            .SelectMany(ext => Directory.GetFiles(directory, $"*{ext}", searchOption))
            .ToList();

        AnsiConsole.MarkupLine($"[cyan]Found {files.Count} files to index[/]");

        foreach (var file in files)
        {
            AnsiConsole.MarkupLine($"\n[dim]{Path.GetRelativePath(directory, file)}[/]");
            var result = await IndexFileAsync(file, collectionId, ct);
            results.Add(result with { FilePath = file });

            if (result.Success)
                AnsiConsole.MarkupLine($"  [green]✓[/] {result.SegmentCount} segments");
            else
                AnsiConsole.MarkupLine($"  [red]✗[/] {result.Message}");
        }

        return results;
    }

    private static string GetMimeType(string extension)
    {
        return extension switch
        {
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".md" => "text/markdown",
            ".txt" => "text/plain",
            ".html" => "text/html",
            _ => "application/octet-stream"
        };
    }

    /// <summary>
    ///     Read YAML frontmatter from a markdown file and propagate selected metadata
    ///     to DocumentEntity.Metadata as JSON. Used for venue quality scoring in RRF.
    /// </summary>
    private static void PropagateFileMetadata(DocumentEntity document, string filePath)
    {
        try
        {
            if (!Path.GetExtension(filePath).Equals(".md", StringComparison.OrdinalIgnoreCase))
                return;

            var content = File.ReadAllText(filePath);
            if (!content.StartsWith("---"))
                return;

            var endIdx = content.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (endIdx < 0)
                return;

            var yamlBlock = content[3..endIdx];
            var frontmatter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in yamlBlock.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var colonIdx = line.IndexOf(':');
                if (colonIdx <= 0) continue;

                var key = line[..colonIdx].Trim().ToLowerInvariant();
                var value = line[(colonIdx + 1)..].Trim();

                if (value.Length >= 2 &&
                    ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                    value = value[1..^1];

                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                    frontmatter[key] = value;
            }

            if (frontmatter.Count == 0)
                return;

            var metadataDict = new Dictionary<string, object>();

            if (frontmatter.TryGetValue("venue_quality", out var vq) && double.TryParse(vq, out var venueQuality))
                metadataDict["venue_quality"] = venueQuality;

            if (frontmatter.TryGetValue("citation_count", out var cc) && int.TryParse(cc, out var citationCount))
                metadataDict["citation_count"] = citationCount;

            if (frontmatter.TryGetValue("influential_citations", out var ic) && int.TryParse(ic, out var influential))
                metadataDict["influential_citations"] = influential;

            if (frontmatter.TryGetValue("venue", out var venue) && !string.IsNullOrEmpty(venue))
                metadataDict["venue"] = venue;

            if (frontmatter.TryGetValue("year", out var yr) && int.TryParse(yr, out var year))
                metadataDict["year"] = year;

            if (metadataDict.Count > 0)
                document.Metadata = System.Text.Json.JsonSerializer.Serialize(metadataDict);
        }
        catch
        {
            // Metadata propagation failure shouldn't affect document processing
        }
    }
}

public record IndexResult(
    bool Success,
    string Message,
    Guid? DocumentId = null,
    int SegmentCount = 0,
    string? FilePath = null);