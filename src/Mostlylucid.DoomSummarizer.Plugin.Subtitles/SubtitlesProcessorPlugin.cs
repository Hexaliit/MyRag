using DoomSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Plugins.Subtitles;

/// <summary>
///     Processor plugin for subtitle files (SRT, VTT, ASS, SSA).
///     Provides chapter-aware splitting using gap detection and marker analysis.
///     Also registers a document handler for the ingestion pipeline.
/// </summary>
public sealed class SubtitlesProcessorPlugin : IProcessorPlugin
{
    private readonly SubtitleDocumentHandler _handler = new();

    /// <summary>
    ///     The document handler for subtitle files. Can be registered with
    ///     <see cref="IDocumentHandlerRegistry" /> for ingestion pipeline support.
    /// </summary>
    public IDocumentHandler DocumentHandler => _handler;

    public ProcessorPluginMetadata Metadata { get; } = new()
    {
        Name = "subtitles",
        DisplayName = "Subtitle Processor",
        Description =
            "Parses SRT, VTT, ASS, and SSA subtitle files into chapter-aware markdown with gap-based chapter detection.",
        Version = "1.0.0",
        SupportedExtensions = [".srt", ".vtt", ".ass", ".ssa"],
        MinActivationWords = 50,
        DocumentTypes = ["subtitles", "transcript"]
    };

    public IReadOnlyList<IDocumentReader> Readers { get; } = [];
    public IReadOnlyList<IDocumentSplitter> Splitters { get; } = [];
    public IReadOnlyList<TemplateDefinition> Templates { get; } = [];

    public Task InitializeAsync(ProcessorPluginServices services, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public bool CanProcess(string markdown, ProcessingContext context)
    {
        // Activate for subtitle file extensions
        if (context.FileName != null)
        {
            var ext = Path.GetExtension(context.FileName).ToLowerInvariant();
            if (Metadata.SupportedExtensions.Contains(ext))
                return true;
        }

        // Activate if document type hint matches
        if (context.DocumentType != null &&
            Metadata.DocumentTypes.Contains(context.DocumentType, StringComparer.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public Task<ProcessorResult> ProcessAsync(
        string markdown, ProcessorOptions options, CancellationToken ct = default)
    {
        // The subtitle content has already been converted to markdown by SubtitleDocumentHandler.
        // Build a simple document tree from the markdown headings.
        var rootTitle = options.FilePath != null
            ? Path.GetFileNameWithoutExtension(options.FilePath)
            : "Subtitles";

        var children = new List<DocumentNode>();

        // Split on ## headings (chapters detected by the handler)
        var sections = markdown.Split("\n## ", StringSplitOptions.RemoveEmptyEntries);
        if (sections.Length > 1)
            // First section is the title (# heading), rest are chapters
            for (var i = 1; i < sections.Length; i++)
            {
                var section = sections[i];
                var newlineIdx = section.IndexOf('\n');
                var title = newlineIdx >= 0 ? section[..newlineIdx].Trim() : section.Trim();
                var content = newlineIdx >= 0 ? section[(newlineIdx + 1)..].Trim() : "";

                children.Add(new DocumentNode
                {
                    Title = title,
                    Sequence = i - 1,
                    Level = 1,
                    LevelLabel = "chapter",
                    Content = content
                });
            }
        else
            // No chapters — single leaf node
            children.Add(new DocumentNode
            {
                Title = rootTitle,
                Sequence = 0,
                Level = 1,
                LevelLabel = "section",
                Content = markdown
            });

        var root = new DocumentNode
        {
            Title = rootTitle,
            Sequence = 0,
            Level = 0,
            LevelLabel = "document",
            Content = "",
            Children = children
        };

        return Task.FromResult(new ProcessorResult
        {
            DocumentTree = root,
            DocumentType = "subtitles",
            StrategyUsed = "chapter-gap-detection"
        });
    }
}