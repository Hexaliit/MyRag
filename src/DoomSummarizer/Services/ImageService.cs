using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using ConsoleImage.Core;
using DoomSummarizer.Models;
using Spectre.Console;

namespace DoomSummarizer.Services;

public partial class ImageService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _cacheDir;
    private bool _disposed;

    public ImageService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _cacheDir = Path.Combine(Path.GetTempPath(), "doomsummarizer", "images");
        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>
    /// Determines if an item is "important" enough to show an image.
    /// Rules:
    /// - High score (top 20% or score > 100)
    /// - Has engagement (comments > 20)
    /// - Certain topics (ai, security, breakthrough)
    /// </summary>
    public bool IsImportant(ContentItem item, IReadOnlyList<ContentItem> allItems)
    {
        // No image? Not important for display purposes
        if (string.IsNullOrEmpty(item.ImageUrl))
            return false;

        // High score threshold
        var avgScore = allItems.Average(i => i.Score);
        if (item.Score >= avgScore * 1.5 || item.Score > 100)
            return true;

        // High engagement
        if (item.CommentCount > 20)
            return true;

        // Important topics
        var topic = item.DetectedTopic?.ToLowerInvariant() ?? "";
        var importantTopics = new[] { "ai", "security", "breakthrough", "launch", "release" };
        if (importantTopics.Any(t => topic.Contains(t)))
            return true;

        return false;
    }

    /// <summary>
    /// Fetches og:image from a URL if the item doesn't have an image already.
    /// </summary>
    public async Task<string?> FetchOgImageAsync(string url, CancellationToken ct = default)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", HttpClientFactory.UserAgent);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode)
                return null;

            // Only parse HTML
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType != null && !contentType.Contains("html"))
                return null;

            var html = await response.Content.ReadAsStringAsync(cts.Token);

            // Quick parse for og:image
            var parser = new HtmlParser();
            var doc = await parser.ParseDocumentAsync(html, cts.Token);

            // Try og:image first
            var ogImage = doc.QuerySelector("meta[property='og:image']")?.GetAttribute("content");
            if (!string.IsNullOrEmpty(ogImage))
                return ogImage;

            // Fallback to twitter:image
            var twitterImage = doc.QuerySelector("meta[name='twitter:image']")?.GetAttribute("content");
            if (!string.IsNullOrEmpty(twitterImage))
                return twitterImage;

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads an image to the cache and returns the local path.
    /// </summary>
    public async Task<string?> DownloadImageAsync(string imageUrl, string itemId, CancellationToken ct = default)
    {
        try
        {
            // Sanitize ID for filename
            var safeId = string.Concat(itemId.Where(c => char.IsLetterOrDigit(c) || c == '_'));
            var extension = GetImageExtension(imageUrl);
            var localPath = Path.Combine(_cacheDir, $"{safeId}{extension}");

            // Use cached version if exists
            if (File.Exists(localPath))
                return localPath;

            var request = new HttpRequestMessage(HttpMethod.Get, imageUrl);
            request.Headers.Add("User-Agent", HttpClientFactory.UserAgent);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            var response = await _httpClient.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
                return null;

            // Verify it's actually an image
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType != null && !contentType.StartsWith("image/"))
                return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);

            // Sanity check - don't cache tiny or huge images
            if (bytes.Length < 1000 || bytes.Length > 5_000_000)
                return null;

            await File.WriteAllBytesAsync(localPath, bytes, ct);
            return localPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Displays an inline image in the console using ConsoleImage's ColorBlockRenderer.
    /// </summary>
    public void DisplayImage(string localPath, int maxWidth = 40)
    {
        try
        {
            var options = new RenderOptions { MaxWidth = maxWidth, UseColor = true };
            using var renderer = new ColorBlockRenderer(options);
            var output = renderer.RenderFile(localPath);
            Console.Write(output);
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[grey][[Image display failed: {Markup.Escape(ex.Message)}]][/]");
        }
    }

    /// <summary>
    /// Renders an item with its image if available and important.
    /// </summary>
    public async Task DisplayItemWithImageAsync(
        ContentItem item,
        IReadOnlyList<ContentItem> allItems,
        bool forceShow = false,
        CancellationToken ct = default)
    {
        if (!forceShow && !IsImportant(item, allItems))
            return;

        var imageUrl = item.ImageUrl;

        // Try to fetch og:image if we don't have one
        if (string.IsNullOrEmpty(imageUrl) && !string.IsNullOrEmpty(item.Url))
        {
            imageUrl = await FetchOgImageAsync(item.Url, ct);
        }

        if (string.IsNullOrEmpty(imageUrl))
            return;

        var localPath = await DownloadImageAsync(imageUrl, item.Id, ct);
        if (localPath != null)
        {
            DisplayImage(localPath);
        }
    }

    private static readonly string[] LocalImageExtensions = [".gif", ".jpg", ".jpeg", ".png", ".webp"];

    /// <summary>
    /// Returns the local file path if the URL is a file:// URI pointing to an image.
    /// </summary>
    public static string? GetLocalImagePath(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (!url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return null;
        var path = url["file://".Length..];
        if (!File.Exists(path)) return null;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return LocalImageExtensions.Contains(ext) ? path : null;
    }

    /// <summary>
    /// Renders a local image file as a color-block thumbnail string.
    /// </summary>
    public static string? RenderThumbnail(string localPath, int maxWidth = 25)
    {
        try
        {
            var options = new RenderOptions { MaxWidth = maxWidth, UseColor = true };
            using var renderer = new ColorBlockRenderer(options);
            return renderer.RenderFile(localPath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Merges multiple rendered thumbnail strings side-by-side with a gap between them.
    /// </summary>
    public static string MergeHorizontal(IReadOnlyList<string> renders, int gap = 2)
    {
        if (renders.Count == 0) return "";
        if (renders.Count == 1) return renders[0];

        var splitLines = renders.Select(r => r.TrimEnd('\n', '\r').Split('\n')).ToArray();
        var maxRows = splitLines.Max(l => l.Length);
        var widths = splitLines.Select(lines =>
            lines.Length > 0 ? lines.Max(StripAnsiLength) : 0).ToArray();

        var gapStr = new string(' ', gap);
        var sb = new System.Text.StringBuilder();

        for (var row = 0; row < maxRows; row++)
        {
            for (var col = 0; col < splitLines.Length; col++)
            {
                if (col > 0) sb.Append(gapStr);
                var lines = splitLines[col];
                if (row < lines.Length)
                {
                    sb.Append(lines[row]);
                    // Pad to width for alignment (only if not last column)
                    if (col < splitLines.Length - 1)
                    {
                        var lineVisualLen = StripAnsiLength(lines[row]);
                        if (lineVisualLen < widths[col])
                            sb.Append(' ', widths[col] - lineVisualLen);
                    }
                }
                else if (col < splitLines.Length - 1)
                {
                    sb.Append(' ', widths[col]);
                }
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Plays a single loop of a GIF file as color-block ASCII art.
    /// Falls back to rendering a single static frame if playback is unavailable.
    /// </summary>
    public static Task PlayGifAsync(string localPath, int maxWidth, CancellationToken ct)
    {
        // GIF animation playback not available in current ConsoleImage package;
        // render a single static frame instead.
        var rendered = RenderThumbnail(localPath, maxWidth);
        if (rendered != null)
        {
            Console.Write(rendered);
            Console.WriteLine();
        }
        return Task.CompletedTask;
    }

    [GeneratedRegex(@"\x1B\[[0-9;]*m")]
    private static partial Regex AnsiEscapeRegex();

    private static int StripAnsiLength(string s)
    {
        var stripped = AnsiEscapeRegex().Replace(s, "");
        return stripped.Length;
    }

    private static string GetImageExtension(string url)
    {
        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath.ToLowerInvariant();

            if (path.EndsWith(".png")) return ".png";
            if (path.EndsWith(".gif")) return ".gif";
            if (path.EndsWith(".webp")) return ".webp";

            return ".jpg"; // Default
        }
        catch
        {
            return ".jpg";
        }
    }

    public void CleanupCache()
    {
        try
        {
            if (Directory.Exists(_cacheDir))
            {
                // Delete files older than 1 hour
                var cutoff = DateTime.UtcNow.AddHours(-1);
                foreach (var file in Directory.GetFiles(_cacheDir))
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CleanupCache();
    }
}
