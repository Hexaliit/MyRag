using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoSummarizer.Core.Services;

/// <summary>
/// Extracts rich metadata from videos using FFmpeg/FFprobe utilities.
/// Harvests: I-frames, scene changes, black frames, silence, loudness, frame types.
/// </summary>
public class FFmpegAnalysisService
{
    private readonly ILogger<FFmpegAnalysisService>? _logger;
    private readonly string _ffprobePath;
    private readonly string _ffmpegPath;

    public FFmpegAnalysisService(
        ILogger<FFmpegAnalysisService>? logger = null,
        string? ffprobePath = null,
        string? ffmpegPath = null)
    {
        _logger = logger;
        _ffprobePath = ffprobePath ?? FindExecutable("ffprobe");
        _ffmpegPath = ffmpegPath ?? FindExecutable("ffmpeg");
    }

    /// <summary>
    /// Extract codec-level keyframes (I-frames) from the video stream.
    /// Much faster than decoding every frame - uses existing compression structure.
    /// </summary>
    public async Task<List<IFrameInfo>> ExtractIFramesAsync(string videoPath, CancellationToken ct = default)
    {
        var iframes = new List<IFrameInfo>();

        // ffprobe command to get keyframe info
        // -select_streams v:0 - only video stream
        // -show_frames - show frame info
        // -show_entries frame=key_frame,pkt_pts_time,pict_type - limit output
        // -of json - JSON output
        var args = $"-v quiet -select_streams v:0 -show_frames -show_entries frame=key_frame,pkt_pts_time,pict_type,coded_picture_number -of json \"{videoPath}\"";

        _logger?.LogInformation("Extracting I-frames from video stream");

        var output = await RunFFprobeAsync(args, ct);
        if (string.IsNullOrEmpty(output)) return iframes;

        try
        {
            using var doc = JsonDocument.Parse(output);
            var frames = doc.RootElement.GetProperty("frames");

            foreach (var frame in frames.EnumerateArray())
            {
                var isKeyFrame = frame.TryGetProperty("key_frame", out var kf) && kf.GetInt32() == 1;
                if (!isKeyFrame) continue;

                var timestamp = 0.0;
                if (frame.TryGetProperty("pkt_pts_time", out var pts))
                {
                    double.TryParse(pts.GetString(), out timestamp);
                }

                var pictType = frame.TryGetProperty("pict_type", out var pt) ? pt.GetString() : "I";
                var frameNumber = frame.TryGetProperty("coded_picture_number", out var cpn) ? cpn.GetInt32() : -1;

                iframes.Add(new IFrameInfo
                {
                    Timestamp = timestamp,
                    FrameNumber = frameNumber,
                    PictureType = pictType ?? "I"
                });
            }

            _logger?.LogInformation("Found {Count} I-frames in video", iframes.Count);
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Failed to parse ffprobe I-frame output");
        }

        return iframes;
    }

    /// <summary>
    /// Detect scene changes using FFmpeg's scene detection filter.
    /// Returns timestamps where visual content changes significantly.
    /// </summary>
    public async Task<List<SceneChangeInfo>> DetectSceneChangesAsync(
        string videoPath,
        double threshold = 0.4,
        CancellationToken ct = default)
    {
        var scenes = new List<SceneChangeInfo>();

        // ffmpeg -i input -vf "select='gt(scene,0.4)',showinfo" -f null -
        // Selects frames where scene score > threshold and outputs info
        var args = $"-i \"{videoPath}\" -vf \"select='gt(scene,{threshold:F2})',showinfo\" -f null - 2>&1";

        _logger?.LogInformation("Detecting scene changes (threshold: {Threshold})", threshold);

        var output = await RunFFmpegAsync(args, ct);
        if (string.IsNullOrEmpty(output)) return scenes;

        // Parse showinfo output: [Parsed_showinfo_1 @ ...] n:0 pts:0 pts_time:0.000000 ...
        var regex = new Regex(@"pts_time:(\d+\.?\d*)", RegexOptions.Compiled);
        var matches = regex.Matches(output);

        foreach (Match match in matches)
        {
            if (double.TryParse(match.Groups[1].Value, out var timestamp))
            {
                scenes.Add(new SceneChangeInfo
                {
                    Timestamp = timestamp,
                    Confidence = threshold
                });
            }
        }

        _logger?.LogInformation("Detected {Count} scene changes", scenes.Count);
        return scenes;
    }

    /// <summary>
    /// Detect black frames in the video (useful for chapter/segment detection).
    /// </summary>
    public async Task<List<BlackFrameInfo>> DetectBlackFramesAsync(
        string videoPath,
        double minDuration = 0.5,
        double threshold = 0.1,
        CancellationToken ct = default)
    {
        var blackFrames = new List<BlackFrameInfo>();

        // blackdetect filter: d=minimum duration, pix_th=pixel threshold
        var args = $"-i \"{videoPath}\" -vf \"blackdetect=d={minDuration:F2}:pix_th={threshold:F2}\" -f null - 2>&1";

        _logger?.LogInformation("Detecting black frames (min duration: {Duration}s)", minDuration);

        var output = await RunFFmpegAsync(args, ct);
        if (string.IsNullOrEmpty(output)) return blackFrames;

        // Parse: [blackdetect @ ...] black_start:0 black_end:0.5 black_duration:0.5
        var regex = new Regex(@"black_start:(\d+\.?\d*)\s+black_end:(\d+\.?\d*)\s+black_duration:(\d+\.?\d*)", RegexOptions.Compiled);
        var matches = regex.Matches(output);

        foreach (Match match in matches)
        {
            blackFrames.Add(new BlackFrameInfo
            {
                StartTime = double.Parse(match.Groups[1].Value),
                EndTime = double.Parse(match.Groups[2].Value),
                Duration = double.Parse(match.Groups[3].Value)
            });
        }

        _logger?.LogInformation("Found {Count} black frame segments", blackFrames.Count);
        return blackFrames;
    }

    /// <summary>
    /// Detect silence in the audio track (useful for speech/non-speech detection).
    /// </summary>
    public async Task<List<SilenceInfo>> DetectSilenceAsync(
        string videoPath,
        double minDuration = 1.0,
        double noiseThreshold = -50,
        CancellationToken ct = default)
    {
        var silences = new List<SilenceInfo>();

        // silencedetect filter: n=noise threshold in dB, d=minimum duration
        var args = $"-i \"{videoPath}\" -af \"silencedetect=n={noiseThreshold}dB:d={minDuration:F2}\" -f null - 2>&1";

        _logger?.LogInformation("Detecting silence (threshold: {Threshold}dB, min: {Duration}s)", noiseThreshold, minDuration);

        var output = await RunFFmpegAsync(args, ct);
        if (string.IsNullOrEmpty(output)) return silences;

        // Parse: [silencedetect @ ...] silence_start: 10.5
        //        [silencedetect @ ...] silence_end: 15.2 | silence_duration: 4.7
        var startRegex = new Regex(@"silence_start:\s*(\d+\.?\d*)", RegexOptions.Compiled);
        var endRegex = new Regex(@"silence_end:\s*(\d+\.?\d*)\s*\|\s*silence_duration:\s*(\d+\.?\d*)", RegexOptions.Compiled);

        var startMatches = startRegex.Matches(output);
        var endMatches = endRegex.Matches(output);

        for (int i = 0; i < Math.Min(startMatches.Count, endMatches.Count); i++)
        {
            silences.Add(new SilenceInfo
            {
                StartTime = double.Parse(startMatches[i].Groups[1].Value),
                EndTime = double.Parse(endMatches[i].Groups[1].Value),
                Duration = double.Parse(endMatches[i].Groups[2].Value)
            });
        }

        _logger?.LogInformation("Found {Count} silence segments", silences.Count);
        return silences;
    }

    /// <summary>
    /// Analyze audio loudness using EBU R128 standard.
    /// Returns integrated loudness (LUFS), loudness range, true peak.
    /// </summary>
    public async Task<LoudnessInfo?> AnalyzeLoudnessAsync(string videoPath, CancellationToken ct = default)
    {
        // loudnorm filter in print_format mode outputs EBU R128 measurements
        var args = $"-i \"{videoPath}\" -af \"loudnorm=print_format=json\" -f null - 2>&1";

        _logger?.LogInformation("Analyzing audio loudness (EBU R128)");

        var output = await RunFFmpegAsync(args, ct);
        if (string.IsNullOrEmpty(output)) return null;

        try
        {
            // Find JSON block in output
            var jsonStart = output.IndexOf('{');
            var jsonEnd = output.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd < 0) return null;

            var json = output.Substring(jsonStart, jsonEnd - jsonStart + 1);
            using var doc = JsonDocument.Parse(json);

            var root = doc.RootElement;
            return new LoudnessInfo
            {
                IntegratedLoudness = ParseDouble(root, "input_i"),
                TruePeak = ParseDouble(root, "input_tp"),
                LoudnessRange = ParseDouble(root, "input_lra"),
                Threshold = ParseDouble(root, "input_thresh"),
                TargetOffset = ParseDouble(root, "target_offset")
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse loudness info");
            return null;
        }
    }

    /// <summary>
    /// Get frame type statistics (I/P/B frame distribution).
    /// </summary>
    public async Task<FrameTypeStats> GetFrameTypeStatsAsync(string videoPath, CancellationToken ct = default)
    {
        var stats = new FrameTypeStats();

        var args = $"-v quiet -select_streams v:0 -show_entries frame=pict_type -of csv \"{videoPath}\"";

        _logger?.LogInformation("Analyzing frame type distribution");

        var output = await RunFFprobeAsync(args, ct);
        if (string.IsNullOrEmpty(output)) return stats;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(',');
            if (parts.Length >= 2)
            {
                switch (parts[1].Trim())
                {
                    case "I": stats.IFrameCount++; break;
                    case "P": stats.PFrameCount++; break;
                    case "B": stats.BFrameCount++; break;
                }
            }
        }

        stats.TotalFrames = stats.IFrameCount + stats.PFrameCount + stats.BFrameCount;
        _logger?.LogInformation("Frame types: I={I}, P={P}, B={B}",
            stats.IFrameCount, stats.PFrameCount, stats.BFrameCount);

        return stats;
    }

    /// <summary>
    /// Detect interlacing in the video.
    /// </summary>
    public async Task<InterlaceInfo?> DetectInterlaceAsync(string videoPath, CancellationToken ct = default)
    {
        // idet filter detects interlacing
        var args = $"-i \"{videoPath}\" -vf \"idet\" -frames:v 500 -f null - 2>&1";

        _logger?.LogInformation("Detecting interlacing");

        var output = await RunFFmpegAsync(args, ct);
        if (string.IsNullOrEmpty(output)) return null;

        // Parse: [Parsed_idet_0 @ ...] Repeated Fields: Neither: xxx Top: xxx Bottom: xxx
        // Multi frame detection: TFF: x BFF: x Progressive: x Undetermined: x
        var multiRegex = new Regex(@"Multi frame detection:\s*TFF:\s*(\d+)\s*BFF:\s*(\d+)\s*Progressive:\s*(\d+)", RegexOptions.Compiled);
        var match = multiRegex.Match(output);

        if (match.Success)
        {
            var tff = int.Parse(match.Groups[1].Value);
            var bff = int.Parse(match.Groups[2].Value);
            var progressive = int.Parse(match.Groups[3].Value);
            var total = tff + bff + progressive;

            return new InterlaceInfo
            {
                IsInterlaced = (tff + bff) > progressive,
                TffFrames = tff,
                BffFrames = bff,
                ProgressiveFrames = progressive,
                InterlaceRatio = total > 0 ? (double)(tff + bff) / total : 0
            };
        }

        return null;
    }

    /// <summary>
    /// Extract a single frame at a specific timestamp.
    /// </summary>
    /// <param name="videoPath">Path to the video file</param>
    /// <param name="timestamp">Timestamp in seconds</param>
    /// <param name="outputDir">Directory to save the frame</param>
    /// <param name="ct">Cancellation token</param>
    /// <param name="prefix">Filename prefix (default: "frame_")</param>
    /// <param name="width">Optional width for scaling (preserves aspect ratio)</param>
    public async Task<string?> ExtractFrameAsync(
        string videoPath,
        double timestamp,
        string outputDir,
        CancellationToken ct = default,
        string prefix = "frame_",
        int? width = null)
    {
        var outputPath = Path.Combine(outputDir, $"{prefix}{timestamp:F3}.jpg");

        // Build scale filter if width specified
        var scaleFilter = width.HasValue
            ? $"-vf \"scale={width}:-1\""
            : "";

        // -ss before -i for fast seeking, -frames:v 1 for single frame
        var args = $"-ss {timestamp:F3} -i \"{videoPath}\" {scaleFilter} -frames:v 1 -q:v 2 \"{outputPath}\" -y";

        await RunFFmpegAsync(args, ct);

        return File.Exists(outputPath) ? outputPath : null;
    }

    /// <summary>
    /// Extract frames at specific timestamps efficiently.
    /// </summary>
    /// <param name="videoPath">Path to the video file</param>
    /// <param name="timestamps">Timestamps in seconds</param>
    /// <param name="outputDir">Directory to save the frames</param>
    /// <param name="ct">Cancellation token</param>
    /// <param name="prefix">Filename prefix (default: "frame_")</param>
    /// <param name="width">Optional width for scaling (preserves aspect ratio)</param>
    public async Task<Dictionary<double, string>> ExtractFramesAtTimestampsAsync(
        string videoPath,
        IEnumerable<double> timestamps,
        string outputDir,
        CancellationToken ct = default,
        string prefix = "frame_",
        int? width = null)
    {
        var results = new Dictionary<double, string>();
        Directory.CreateDirectory(outputDir);

        // Sort timestamps for sequential seeking
        var sortedTimestamps = timestamps.OrderBy(t => t).ToList();

        foreach (var timestamp in sortedTimestamps)
        {
            ct.ThrowIfCancellationRequested();

            var framePath = await ExtractFrameAsync(videoPath, timestamp, outputDir, ct, prefix, width);
            if (framePath != null)
            {
                results[timestamp] = framePath;
            }
        }

        _logger?.LogInformation("Extracted {Count} frames", results.Count);
        return results;
    }

    /// <summary>
    /// Extract chapter marks from the video container.
    /// Many videos have embedded chapters for navigation.
    /// </summary>
    public async Task<List<ChapterInfo>> ExtractChaptersAsync(string videoPath, CancellationToken ct = default)
    {
        var chapters = new List<ChapterInfo>();

        var args = $"-v quiet -print_format json -show_chapters \"{videoPath}\"";

        _logger?.LogInformation("Extracting chapter marks");

        var output = await RunFFprobeAsync(args, ct);
        if (string.IsNullOrEmpty(output)) return chapters;

        try
        {
            using var doc = JsonDocument.Parse(output);
            if (!doc.RootElement.TryGetProperty("chapters", out var chaptersArray))
                return chapters;

            var index = 0;
            foreach (var chapter in chaptersArray.EnumerateArray())
            {
                var startTime = chapter.TryGetProperty("start_time", out var st)
                    ? double.Parse(st.GetString() ?? "0") : 0;
                var endTime = chapter.TryGetProperty("end_time", out var et)
                    ? double.Parse(et.GetString() ?? "0") : 0;

                string? title = null;
                if (chapter.TryGetProperty("tags", out var tags) &&
                    tags.TryGetProperty("title", out var titleProp))
                {
                    title = titleProp.GetString();
                }

                chapters.Add(new ChapterInfo
                {
                    Index = index++,
                    StartTime = startTime,
                    EndTime = endTime,
                    Title = title,
                    Duration = endTime - startTime
                });
            }

            _logger?.LogInformation("Found {Count} chapters", chapters.Count);
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Failed to parse chapter info");
        }

        return chapters;
    }

    /// <summary>
    /// Extract embedded subtitles from the video.
    /// Returns list of subtitle streams with their language and format.
    /// </summary>
    public async Task<List<SubtitleStreamInfo>> GetSubtitleStreamsAsync(string videoPath, CancellationToken ct = default)
    {
        var streams = new List<SubtitleStreamInfo>();

        var args = $"-v quiet -print_format json -show_streams -select_streams s \"{videoPath}\"";

        _logger?.LogInformation("Detecting subtitle streams");

        var output = await RunFFprobeAsync(args, ct);
        if (string.IsNullOrEmpty(output)) return streams;

        try
        {
            using var doc = JsonDocument.Parse(output);
            if (!doc.RootElement.TryGetProperty("streams", out var streamsArray))
                return streams;

            foreach (var stream in streamsArray.EnumerateArray())
            {
                var index = stream.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;
                var codec = stream.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;

                string? language = null;
                string? title = null;
                if (stream.TryGetProperty("tags", out var tags))
                {
                    language = tags.TryGetProperty("language", out var lang) ? lang.GetString() : null;
                    title = tags.TryGetProperty("title", out var t) ? t.GetString() : null;
                }

                streams.Add(new SubtitleStreamInfo
                {
                    StreamIndex = index,
                    Codec = codec,
                    Language = language,
                    Title = title
                });
            }

            _logger?.LogInformation("Found {Count} subtitle streams", streams.Count);
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Failed to parse subtitle streams");
        }

        return streams;
    }

    /// <summary>
    /// Extract subtitles to SRT format for ingestion.
    /// </summary>
    public async Task<string?> ExtractSubtitlesToSrtAsync(
        string videoPath,
        int streamIndex,
        string outputDir,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, $"subtitles_{streamIndex}.srt");

        // Extract subtitle stream to SRT format
        var args = $"-i \"{videoPath}\" -map 0:{streamIndex} -c:s srt \"{outputPath}\" -y";

        _logger?.LogInformation("Extracting subtitle stream {Index} to SRT", streamIndex);

        await RunFFmpegAsync(args, ct);

        if (File.Exists(outputPath))
        {
            _logger?.LogInformation("Subtitles extracted to: {Path}", outputPath);
            return outputPath;
        }

        return null;
    }

    /// <summary>
    /// Parse an external SRT file into timed text entries.
    /// </summary>
    public List<SubtitleEntry> ParseSrtFile(string srtPath)
    {
        var entries = new List<SubtitleEntry>();

        if (!File.Exists(srtPath))
        {
            _logger?.LogWarning("SRT file not found: {Path}", srtPath);
            return entries;
        }

        var content = File.ReadAllText(srtPath);
        var blocks = content.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);

        // SRT format:
        // 1
        // 00:00:01,000 --> 00:00:04,000
        // First subtitle line
        // Second line (optional)

        var timeRegex = new Regex(@"(\d{2}):(\d{2}):(\d{2}),(\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2}),(\d{3})", RegexOptions.Compiled);

        foreach (var block in blocks)
        {
            var lines = block.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2) continue;

            // Find the timestamp line
            var timeMatch = timeRegex.Match(block);
            if (!timeMatch.Success) continue;

            var startTime = ParseSrtTimestamp(
                int.Parse(timeMatch.Groups[1].Value),
                int.Parse(timeMatch.Groups[2].Value),
                int.Parse(timeMatch.Groups[3].Value),
                int.Parse(timeMatch.Groups[4].Value));

            var endTime = ParseSrtTimestamp(
                int.Parse(timeMatch.Groups[5].Value),
                int.Parse(timeMatch.Groups[6].Value),
                int.Parse(timeMatch.Groups[7].Value),
                int.Parse(timeMatch.Groups[8].Value));

            // Extract text (everything after the timestamp line)
            var timeLineIndex = Array.FindIndex(lines, l => timeRegex.IsMatch(l));
            if (timeLineIndex < 0 || timeLineIndex >= lines.Length - 1) continue;

            var text = string.Join(" ", lines.Skip(timeLineIndex + 1)).Trim();

            entries.Add(new SubtitleEntry
            {
                Index = entries.Count + 1,
                StartTime = startTime,
                EndTime = endTime,
                Text = text
            });
        }

        _logger?.LogInformation("Parsed {Count} subtitle entries from SRT", entries.Count);
        return entries;
    }

    private static double ParseSrtTimestamp(int hours, int minutes, int seconds, int millis)
    {
        return hours * 3600 + minutes * 60 + seconds + millis / 1000.0;
    }

    /// <summary>
    /// Get comprehensive stream info from the video.
    /// </summary>
    public async Task<StreamInfo?> GetStreamInfoAsync(string videoPath, CancellationToken ct = default)
    {
        var args = $"-v quiet -print_format json -show_format -show_streams \"{videoPath}\"";

        var output = await RunFFprobeAsync(args, ct);
        if (string.IsNullOrEmpty(output)) return null;

        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;

            var info = new StreamInfo();

            // Parse format info
            if (root.TryGetProperty("format", out var format))
            {
                info.FormatName = format.TryGetProperty("format_name", out var fn) ? fn.GetString() : null;
                info.Duration = format.TryGetProperty("duration", out var d) ? double.Parse(d.GetString() ?? "0") : 0;
                info.BitRate = format.TryGetProperty("bit_rate", out var br) ? long.Parse(br.GetString() ?? "0") : 0;
                info.FileSize = format.TryGetProperty("size", out var sz) ? long.Parse(sz.GetString() ?? "0") : 0;
            }

            // Parse streams
            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var codecType = stream.TryGetProperty("codec_type", out var ct2) ? ct2.GetString() : null;

                    if (codecType == "video" && info.VideoCodec == null)
                    {
                        info.VideoCodec = stream.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
                        info.Width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                        info.Height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0;

                        if (stream.TryGetProperty("r_frame_rate", out var fps))
                        {
                            var fpsStr = fps.GetString() ?? "0/1";
                            var parts = fpsStr.Split('/');
                            if (parts.Length == 2 && double.TryParse(parts[0], out var num) && double.TryParse(parts[1], out var den) && den > 0)
                            {
                                info.FrameRate = num / den;
                            }
                        }

                        info.PixelFormat = stream.TryGetProperty("pix_fmt", out var pf) ? pf.GetString() : null;
                        info.VideoProfile = stream.TryGetProperty("profile", out var vp) ? vp.GetString() : null;
                    }
                    else if (codecType == "audio" && info.AudioCodec == null)
                    {
                        info.AudioCodec = stream.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
                        info.SampleRate = stream.TryGetProperty("sample_rate", out var sr) ? int.Parse(sr.GetString() ?? "0") : 0;
                        info.Channels = stream.TryGetProperty("channels", out var ch) ? ch.GetInt32() : 0;
                        info.ChannelLayout = stream.TryGetProperty("channel_layout", out var cl) ? cl.GetString() : null;
                    }
                }
            }

            return info;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse stream info");
            return null;
        }
    }

    private async Task<string> RunFFprobeAsync(string args, CancellationToken ct)
    {
        return await RunProcessAsync(_ffprobePath, args, ct);
    }

    private async Task<string> RunFFmpegAsync(string args, CancellationToken ct)
    {
        return await RunProcessAsync(_ffmpegPath, args, ct);
    }

    private async Task<string> RunProcessAsync(string executable, string args, CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            // Read both stdout and stderr
            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);

            var output = await outputTask;
            var error = await errorTask;

            // Return combined output (many FFmpeg utilities write to stderr)
            return string.IsNullOrEmpty(output) ? error : output;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to run {Executable}", executable);
            return string.Empty;
        }
    }

    private static string? _ffmpegBinPath;
    private static readonly object _ffmpegLock = new();

    private static string FindExecutable(string name)
    {
        var exeName = OperatingSystem.IsWindows() ? $"{name}.exe" : name;

        // First check if FFmpeg bin directory is already cached
        if (_ffmpegBinPath != null)
        {
            var cachedPath = Path.Combine(_ffmpegBinPath, exeName);
            if (File.Exists(cachedPath)) return cachedPath;
        }

        // Check if it's in PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var fullPath = Path.Combine(dir, exeName);
            if (File.Exists(fullPath))
            {
                lock (_ffmpegLock) { _ffmpegBinPath = dir; }
                return fullPath;
            }
        }

        // Common FFmpeg installation locations
        var searchPaths = new List<string>
        {
            // Winget installation (Windows)
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Packages"),
            // Chocolatey
            @"C:\ProgramData\chocolatey\lib\ffmpeg\tools\ffmpeg\bin",
            // Scoop
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "scoop", "apps", "ffmpeg", "current", "bin"),
            // Manual installs (Windows)
            @"C:\ffmpeg\bin",
            @"C:\Program Files\ffmpeg\bin",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin"),
            // Linux/macOS
            "/usr/bin",
            "/usr/local/bin",
            "/opt/homebrew/bin"
        };

        foreach (var basePath in searchPaths)
        {
            if (!Directory.Exists(basePath)) continue;

            // For winget, search recursively for the executable
            if (basePath.Contains("WinGet") || basePath.Contains("winget"))
            {
                try
                {
                    var foundExe = Directory.GetFiles(basePath, exeName, SearchOption.AllDirectories)
                        .FirstOrDefault();
                    if (foundExe != null)
                    {
                        var binDir = Path.GetDirectoryName(foundExe)!;
                        lock (_ffmpegLock) { _ffmpegBinPath = binDir; }
                        return foundExe;
                    }
                }
                catch
                {
                    // Ignore search errors
                }
            }
            else
            {
                var fullPath = Path.Combine(basePath, exeName);
                if (File.Exists(fullPath))
                {
                    lock (_ffmpegLock) { _ffmpegBinPath = basePath; }
                    return fullPath;
                }
            }
        }

        // Fall back to just the name and hope it's in PATH
        return name;
    }

    private static double ParseDouble(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var prop))
        {
            var str = prop.GetString();
            if (double.TryParse(str, out var val)) return val;
        }
        return 0;
    }
}

/// <summary>
/// Information about a codec-level I-frame (keyframe).
/// </summary>
public record IFrameInfo
{
    public double Timestamp { get; init; }
    public int FrameNumber { get; init; }
    public string PictureType { get; init; } = "I";
}

/// <summary>
/// Information about a detected scene change.
/// </summary>
public record SceneChangeInfo
{
    public double Timestamp { get; init; }
    public double Confidence { get; init; }
}

/// <summary>
/// Information about a black frame segment.
/// </summary>
public record BlackFrameInfo
{
    public double StartTime { get; init; }
    public double EndTime { get; init; }
    public double Duration { get; init; }
}

/// <summary>
/// Information about a silence segment.
/// </summary>
public record SilenceInfo
{
    public double StartTime { get; init; }
    public double EndTime { get; init; }
    public double Duration { get; init; }
}

/// <summary>
/// Audio loudness measurements (EBU R128).
/// </summary>
public record LoudnessInfo
{
    /// <summary>Integrated loudness in LUFS</summary>
    public double IntegratedLoudness { get; init; }

    /// <summary>True peak in dBFS</summary>
    public double TruePeak { get; init; }

    /// <summary>Loudness range in LU</summary>
    public double LoudnessRange { get; init; }

    /// <summary>Threshold in LUFS</summary>
    public double Threshold { get; init; }

    /// <summary>Target offset in LU</summary>
    public double TargetOffset { get; init; }
}

/// <summary>
/// Frame type statistics.
/// </summary>
public record FrameTypeStats
{
    public int IFrameCount { get; set; }
    public int PFrameCount { get; set; }
    public int BFrameCount { get; set; }
    public int TotalFrames { get; set; }

    public double IFrameRatio => TotalFrames > 0 ? (double)IFrameCount / TotalFrames : 0;
    public double PFrameRatio => TotalFrames > 0 ? (double)PFrameCount / TotalFrames : 0;
    public double BFrameRatio => TotalFrames > 0 ? (double)BFrameCount / TotalFrames : 0;
}

/// <summary>
/// Interlacing detection results.
/// </summary>
public record InterlaceInfo
{
    public bool IsInterlaced { get; init; }
    public int TffFrames { get; init; }  // Top field first
    public int BffFrames { get; init; }  // Bottom field first
    public int ProgressiveFrames { get; init; }
    public double InterlaceRatio { get; init; }
}

/// <summary>
/// Comprehensive stream information.
/// </summary>
public record StreamInfo
{
    // Format
    public string? FormatName { get; set; }
    public double Duration { get; set; }
    public long BitRate { get; set; }
    public long FileSize { get; set; }

    // Video
    public string? VideoCodec { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double FrameRate { get; set; }
    public string? PixelFormat { get; set; }
    public string? VideoProfile { get; set; }

    // Audio
    public string? AudioCodec { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public string? ChannelLayout { get; set; }
}

/// <summary>
/// Chapter mark from video container.
/// </summary>
public record ChapterInfo
{
    public int Index { get; init; }
    public double StartTime { get; init; }
    public double EndTime { get; init; }
    public double Duration { get; init; }
    public string? Title { get; init; }
}

/// <summary>
/// Embedded subtitle stream information.
/// </summary>
public record SubtitleStreamInfo
{
    public int StreamIndex { get; init; }
    public string? Codec { get; init; }
    public string? Language { get; init; }
    public string? Title { get; init; }
}

/// <summary>
/// Single subtitle entry from an SRT file.
/// </summary>
public record SubtitleEntry
{
    public int Index { get; init; }
    public double StartTime { get; init; }
    public double EndTime { get; init; }
    public string Text { get; init; } = "";
    public double Duration => EndTime - StartTime;
}
