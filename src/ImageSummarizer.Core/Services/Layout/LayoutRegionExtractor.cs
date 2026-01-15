using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

namespace Mostlylucid.DocSummarizer.Images.Services.Layout;

/// <summary>
/// Extracts detected layout regions from images for further processing.
/// Crops Table, Figure, and Text regions for routing to appropriate processors.
/// </summary>
public class LayoutRegionExtractor
{
    private readonly ILogger<LayoutRegionExtractor>? _logger;
    private readonly string _tempDirectory;

    public LayoutRegionExtractor(ILogger<LayoutRegionExtractor>? logger = null)
    {
        _logger = logger;
        _tempDirectory = Path.Combine(Path.GetTempPath(), "lucidrag", "layout_regions");
        Directory.CreateDirectory(_tempDirectory);
    }

    /// <summary>
    /// Extract all regions of specified types from an image.
    /// </summary>
    /// <param name="imagePath">Source image path</param>
    /// <param name="detections">Layout detections from LayoutDetectionWave</param>
    /// <param name="regionTypes">Types to extract (e.g., "Table", "Picture")</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of extracted regions with paths to cropped images</returns>
    public async Task<List<ExtractedRegion>> ExtractRegionsAsync(
        string imagePath,
        IEnumerable<LayoutDetection> detections,
        IEnumerable<string> regionTypes,
        CancellationToken ct = default)
    {
        var results = new List<ExtractedRegion>();
        var targetTypes = new HashSet<string>(regionTypes, StringComparer.OrdinalIgnoreCase);

        var relevantDetections = detections
            .Where(d => targetTypes.Contains(d.ClassName))
            .OrderBy(d => d.Y) // Top to bottom
            .ThenBy(d => d.X)  // Left to right
            .ToList();

        if (relevantDetections.Count == 0)
        {
            _logger?.LogDebug("No regions of types [{Types}] found in {Image}",
                string.Join(", ", regionTypes), imagePath);
            return results;
        }

        _logger?.LogInformation("Extracting {Count} regions of types [{Types}] from {Image}",
            relevantDetections.Count, string.Join(", ", regionTypes), imagePath);

        using var image = await Image.LoadAsync<Rgba32>(imagePath, ct);
        var baseName = Path.GetFileNameWithoutExtension(imagePath);

        for (int i = 0; i < relevantDetections.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var detection = relevantDetections[i];
            var extracted = await ExtractRegionAsync(image, detection, baseName, i, ct);
            if (extracted != null)
            {
                results.Add(extracted);
            }
        }

        _logger?.LogInformation("Successfully extracted {Count} regions", results.Count);
        return results;
    }

    /// <summary>
    /// Extract a single region from the image.
    /// </summary>
    private async Task<ExtractedRegion?> ExtractRegionAsync(
        Image<Rgba32> sourceImage,
        LayoutDetection detection,
        string baseName,
        int index,
        CancellationToken ct)
    {
        try
        {
            // Validate bounds
            var x = Math.Max(0, detection.X);
            var y = Math.Max(0, detection.Y);
            var width = Math.Min(detection.Width, sourceImage.Width - x);
            var height = Math.Min(detection.Height, sourceImage.Height - y);

            if (width < 10 || height < 10)
            {
                _logger?.LogDebug("Skipping region too small: {W}x{H}", width, height);
                return null;
            }

            // Add small padding (2%) to capture borders
            var padX = (int)(width * 0.02);
            var padY = (int)(height * 0.02);
            x = Math.Max(0, x - padX);
            y = Math.Max(0, y - padY);
            width = Math.Min(width + 2 * padX, sourceImage.Width - x);
            height = Math.Min(height + 2 * padY, sourceImage.Height - y);

            var cropRect = new Rectangle(x, y, width, height);

            // Clone and crop
            using var cropped = sourceImage.Clone(ctx => ctx.Crop(cropRect));

            // Generate output path
            var regionType = detection.ClassName.ToLowerInvariant().Replace("-", "_");
            var outputPath = Path.Combine(
                _tempDirectory,
                $"{baseName}_{regionType}_{index}_{Guid.NewGuid():N}.png");

            await cropped.SaveAsPngAsync(outputPath, ct);

            _logger?.LogDebug("Extracted {Type} region {Index}: {W}x{H} -> {Path}",
                detection.ClassName, index, width, height, outputPath);

            return new ExtractedRegion
            {
                ClassName = detection.ClassName,
                SourceImagePath = baseName,
                ExtractedImagePath = outputPath,
                OriginalBounds = new RegionBounds
                {
                    X = detection.X,
                    Y = detection.Y,
                    Width = detection.Width,
                    Height = detection.Height
                },
                Confidence = detection.Confidence,
                RegionIndex = index
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to extract region {Index} ({Type})",
                index, detection.ClassName);
            return null;
        }
    }

    /// <summary>
    /// Clean up extracted region files.
    /// </summary>
    public void CleanupRegions(IEnumerable<ExtractedRegion> regions)
    {
        foreach (var region in regions)
        {
            try
            {
                if (File.Exists(region.ExtractedImagePath))
                {
                    File.Delete(region.ExtractedImagePath);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to cleanup region file: {Path}", region.ExtractedImagePath);
            }
        }
    }
}

/// <summary>
/// Represents an extracted layout region.
/// </summary>
public record ExtractedRegion
{
    /// <summary>Region type (Table, Picture, Text, etc.)</summary>
    public required string ClassName { get; init; }

    /// <summary>Original source image name</summary>
    public required string SourceImagePath { get; init; }

    /// <summary>Path to the cropped region image</summary>
    public required string ExtractedImagePath { get; init; }

    /// <summary>Original bounding box in source image</summary>
    public required RegionBounds OriginalBounds { get; init; }

    /// <summary>Detection confidence</summary>
    public float Confidence { get; init; }

    /// <summary>Index of this region (0-based, ordered top-to-bottom, left-to-right)</summary>
    public int RegionIndex { get; init; }
}

/// <summary>
/// Bounding box coordinates.
/// </summary>
public record RegionBounds
{
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}
