using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Images.Orchestration;
using Mostlylucid.DocSummarizer.Images.Services.Analysis;
using Mostlylucid.Summarizer.Core.Pipeline;

namespace Mostlylucid.DocSummarizer.Images.Pipeline;

/// <summary>
/// Pipeline implementation for image files (GIF, PNG, JPG, WebP, etc.).
/// Wraps the WaveOrchestrator for OCR and vision model captioning.
/// </summary>
public class ImagePipeline : PipelineBase
{
    private readonly WaveOrchestrator _orchestrator;
    private readonly ILogger<ImagePipeline> _logger;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gif", ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tiff", ".tif"
    };

    public ImagePipeline(
        WaveOrchestrator orchestrator,
        ILogger<ImagePipeline> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    /// <inheritdoc />
    public override string PipelineId => "image";

    /// <inheritdoc />
    public override string Name => "Image Pipeline";

    /// <inheritdoc />
    public override IReadOnlySet<string> SupportedExtensions => ImageExtensions;

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<ContentChunk>> ProcessCoreAsync(
        string filePath,
        PipelineOptions options,
        IProgress<PipelineProgress>? progress,
        CancellationToken ct)
    {
        _logger.LogInformation("Processing image: {FilePath}", filePath);

        progress?.Report(new PipelineProgress("Analyzing", "Running analysis waves", 10));

        // Run the wave orchestrator to analyze the image
        var profile = await _orchestrator.AnalyzeAsync(filePath, ct);

        progress?.Report(new PipelineProgress("Extracting", "Building content chunks", 80));

        var chunks = new List<ContentChunk>();
        var chunkIndex = 0;

        // Extract OCR text if available
        var ocrText = profile.GetValue<string>(ImageSignalKeys.OcrText);
        if (!string.IsNullOrWhiteSpace(ocrText))
        {
            var ocrConfidence = profile.GetValue<double?>(ImageSignalKeys.OcrConfidence);
            chunks.Add(new ContentChunk
            {
                Id = GenerateChunkId(filePath, chunkIndex++),
                Text = ocrText,
                ContentType = ContentType.ImageOcr,
                SourcePath = filePath,
                Index = chunkIndex - 1,
                ContentHash = ComputeHash(ocrText),
                Confidence = ocrConfidence,
                Metadata = new Dictionary<string, object?>
                {
                    ["source"] = "ocr",
                    ["wordCount"] = profile.GetValue<int?>(ImageSignalKeys.OcrWordCount),
                    ["language"] = profile.GetValue<string>(ImageSignalKeys.OcrLanguage)
                }
            });
        }

        // Extract caption if available
        var caption = profile.GetValue<string>(ImageSignalKeys.Caption);
        if (!string.IsNullOrWhiteSpace(caption))
        {
            var visionConfidence = profile.GetValue<double?>(ImageSignalKeys.VisionConfidence);
            chunks.Add(new ContentChunk
            {
                Id = GenerateChunkId(filePath, chunkIndex++),
                Text = caption,
                ContentType = ContentType.ImageCaption,
                SourcePath = filePath,
                Index = chunkIndex - 1,
                ContentHash = ComputeHash(caption),
                Confidence = visionConfidence,
                Metadata = new Dictionary<string, object?>
                {
                    ["source"] = "vision",
                    ["objects"] = profile.GetValue<object>(ImageSignalKeys.Objects)
                }
            });
        }

        // Extract entities from multiple sources for rich graph building
        // 1. VisionLLM entities (vision.llm.entities) - high quality but slow
        // 2. Florence2 entities (florence2.entity.*) - fast local extraction
        // 3. Motion objects (motion.moving_objects) - what's moving in GIFs
        var allEntities = new List<string>();
        var entityMetadata = new Dictionary<string, object?>();

        // VisionLLM entities
        var visionEntities = profile.GetValue<List<Models.Dynamic.EntityDetection>>("vision.llm.entities");
        if (visionEntities != null)
        {
            foreach (var e in visionEntities.Where(e => !string.IsNullOrWhiteSpace(e.Label)))
            {
                allEntities.Add(e.Label!);
            }
            entityMetadata["vision_llm_count"] = visionEntities.Count;
        }

        // Florence2 entities (from florence2.entity.* signals)
        var florence2EntitySignals = profile.GetAllSignals()
            .Where(s => s.Key.StartsWith("florence2.entity.") && !s.Key.Equals("florence2.entity_types"))
            .Select(s => s.Value?.ToString())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();
        if (florence2EntitySignals.Count > 0)
        {
            foreach (var e in florence2EntitySignals)
            {
                if (!allEntities.Contains(e!, StringComparer.OrdinalIgnoreCase))
                    allEntities.Add(e!);
            }
            entityMetadata["florence2_count"] = florence2EntitySignals.Count;
        }

        // Motion entities (what's moving in animated GIFs)
        var motionObjects = profile.GetValue<List<string>>("motion.moving_objects");
        if (motionObjects != null)
        {
            foreach (var obj in motionObjects.Where(o => !string.IsNullOrWhiteSpace(o)))
            {
                if (!allEntities.Contains(obj, StringComparer.OrdinalIgnoreCase))
                    allEntities.Add(obj);
            }
            entityMetadata["motion_count"] = motionObjects.Count;
        }

        if (allEntities.Count > 0)
        {
            var entityText = string.Join(", ", allEntities.Distinct());
            chunks.Add(new ContentChunk
            {
                Id = GenerateChunkId(filePath, chunkIndex++),
                Text = $"Entities: {entityText}",
                ContentType = ContentType.Entity,
                SourcePath = filePath,
                Index = chunkIndex - 1,
                ContentHash = ComputeHash(entityText),
                Metadata = new Dictionary<string, object?>
                {
                    ["source"] = "combined_entities",
                    ["entityCount"] = allEntities.Count,
                    ["sources"] = entityMetadata
                }
            });
        }

        // Include salient signals for graph relationships
        // These enable cross-modal linking (images with similar colors, motion types, etc.)
        var dominantColors = profile.GetValue<List<Models.DominantColor>>("color.dominant_colors");
        var sceneType = profile.GetValue<string>("vision.llm.scene_type");
        var motionType = profile.GetValue<string>("motion.type");
        var isAnimated = profile.GetValue<bool>("identity.is_animated");

        // Store rich metadata on the caption chunk for graph building
        // Common signal names: content.entities, content.colors, content.motion, content.scene
        if (chunks.Count > 0)
        {
            var mainChunk = chunks.FirstOrDefault(c => c.ContentType == ContentType.ImageCaption)
                           ?? chunks.First();
            // content.* namespace for cross-modal consistency
            mainChunk.Metadata["content.colors"] = dominantColors?.Take(3).Select(c => c.Name).ToList();
            mainChunk.Metadata["content.scene"] = sceneType;
            mainChunk.Metadata["content.motion"] = motionType;
            mainChunk.Metadata["content.animated"] = isAnimated;
            mainChunk.Metadata["content.frames"] = profile.GetValue<int>("identity.frame_count");
            mainChunk.Metadata["content.entities"] = allEntities; // Standard entity list
        }

        // Add image metadata as a chunk if no text was extracted
        if (chunks.Count == 0)
        {
            var metadataText = BuildMetadataText(filePath, profile);
            chunks.Add(new ContentChunk
            {
                Id = GenerateChunkId(filePath, chunkIndex++),
                Text = metadataText,
                ContentType = ContentType.Summary,
                SourcePath = filePath,
                Index = chunkIndex - 1,
                ContentHash = ComputeHash(metadataText),
                Metadata = new Dictionary<string, object?>
                {
                    ["source"] = "metadata",
                    ["width"] = profile.GetValue<int?>(ImageSignalKeys.ImageWidth),
                    ["height"] = profile.GetValue<int?>(ImageSignalKeys.ImageHeight),
                    ["format"] = profile.GetValue<string>(ImageSignalKeys.ImageFormat)
                }
            });
        }

        _logger.LogInformation("Processed {ChunkCount} chunks from image", chunks.Count);

        return chunks;
    }

    private static string BuildMetadataText(string filePath, Models.Dynamic.DynamicImageProfile profile)
    {
        var width = profile.GetValue<int?>(ImageSignalKeys.ImageWidth);
        var height = profile.GetValue<int?>(ImageSignalKeys.ImageHeight);
        var format = profile.GetValue<string>(ImageSignalKeys.ImageFormat) ?? Path.GetExtension(filePath);

        return $"Image: {Path.GetFileName(filePath)}, Format: {format}, Dimensions: {width}x{height}";
    }
}
