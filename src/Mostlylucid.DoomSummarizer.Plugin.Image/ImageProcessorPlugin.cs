using DoomSummarizer.Models;
using DoomSummarizer.Plugins;
using Mostlylucid.DoomSummarizer.Plugin.Image.Commands;
using Spectre.Console.Cli;

namespace Mostlylucid.DoomSummarizer.Plugin.Image;

public class ImageProcessorPlugin : IProcessorPlugin, ICliPlugin
{
    public PluginCliMetadata CliMetadata { get; } = new()
    {
        CommandName = "image",
        Description = "Image analysis: OCR, captioning, multi-wave ML pipeline",
        Examples = ["image analyze photo.jpg", "image ocr document.png", "image caption photo.jpg"]
    };

    public void ConfigureCommands(IConfigurator config)
    {
        config.AddBranch("image", image =>
        {
            image.SetDescription(CliMetadata.Description);
            image.AddCommand<ImageAnalyzeCommand>("analyze")
                .WithDescription("Run full 22-wave image analysis pipeline")
                .WithExample("image", "analyze", "photo.jpg");
            image.AddCommand<ImageOcrCommand>("ocr")
                .WithDescription("Extract text via OCR")
                .WithExample("image", "ocr", "document.png");
            image.AddCommand<ImageCaptionCommand>("caption")
                .WithDescription("Generate vision model caption")
                .WithExample("image", "caption", "photo.jpg");
        });
    }

    public ProcessorPluginMetadata Metadata { get; } = new()
    {
        Name = "image",
        DisplayName = "Image Analyzer",
        Description = "OCR, captioning, and multi-wave ML analysis for image files",
        Version = "1.0.0",
        SupportedExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tiff"],
        MinActivationWords = 0,
        DocumentTypes = ["image"]
    };

    public IReadOnlyList<IDocumentReader> Readers => [];
    public IReadOnlyList<IDocumentSplitter> Splitters => [];
    public IReadOnlyList<TemplateDefinition> Templates => [];

    public Task InitializeAsync(ProcessorPluginServices services, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public bool CanProcess(string markdown, ProcessingContext context)
    {
        if (context.FileName == null) return false;
        var ext = Path.GetExtension(context.FileName);
        return Metadata.SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    public Task<ProcessorResult> ProcessAsync(string markdown, ProcessorOptions options, CancellationToken ct = default)
    {
        throw new NotSupportedException("Image processing uses IPipeline. Use CLI commands for direct image analysis.");
    }
}