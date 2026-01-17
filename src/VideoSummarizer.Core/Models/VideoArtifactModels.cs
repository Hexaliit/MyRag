namespace VideoSummarizer.Core.Models;

/// <summary>
/// Represents a physical artifact file generated during video processing.
/// Artifacts include scene frames, text exports, audio tracks, and clips.
/// </summary>
public record VideoArtifact
{
    public required Guid Id { get; init; }
    public required Guid VideoId { get; init; }

    /// <summary>Type of artifact</summary>
    public required VideoArtifactType Type { get; init; }

    /// <summary>Path to the artifact file</summary>
    public required string FilePath { get; init; }

    /// <summary>File size in bytes</summary>
    public long FileSize { get; init; }

    /// <summary>MIME type of the artifact</summary>
    public string? MimeType { get; init; }

    /// <summary>Content hash for deduplication</summary>
    public string? ContentHash { get; init; }

    /// <summary>When this artifact was created</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Start time in video (if time-bound)</summary>
    public double? StartTime { get; init; }

    /// <summary>End time in video (if time-bound)</summary>
    public double? EndTime { get; init; }

    /// <summary>Frame index (if single frame)</summary>
    public int? FrameIndex { get; init; }

    /// <summary>Related shot/scene/track ID</summary>
    public Guid? RelatedEntityId { get; init; }

    /// <summary>Language code (for text/subtitle artifacts)</summary>
    public string? Language { get; init; }

    /// <summary>Resolution for image artifacts</summary>
    public ArtifactResolution? Resolution { get; init; }

    /// <summary>Processing quality level</summary>
    public ArtifactQuality Quality { get; init; } = ArtifactQuality.Standard;

    /// <summary>Additional metadata</summary>
    public Dictionary<string, object?> Metadata { get; init; } = [];
}

/// <summary>
/// Types of artifacts generated during video processing.
/// </summary>
public enum VideoArtifactType
{
    // Image artifacts
    /// <summary>Low-res scene frame thumbnail</summary>
    SceneFrame,
    /// <summary>Keyframe from shot boundary</summary>
    Keyframe,
    /// <summary>Filmstrip/contact sheet of multiple frames</summary>
    Filmstrip,
    /// <summary>Face thumbnail crop</summary>
    FaceThumbnail,
    /// <summary>Motion heatmap overlay</summary>
    MotionHeatmap,

    // Audio artifacts
    /// <summary>Extracted audio track (original)</summary>
    AudioTrackOriginal,
    /// <summary>Normalized/processed audio track</summary>
    AudioTrackProcessed,
    /// <summary>Speech segment audio (for transcription)</summary>
    SpeechSegment,

    // Text artifacts
    /// <summary>Subtitle file (SRT)</summary>
    SubtitleSrt,
    /// <summary>Subtitle file (VTT)</summary>
    SubtitleVtt,
    /// <summary>Plain text transcript</summary>
    TranscriptTxt,
    /// <summary>Transcript with timestamps (JSON)</summary>
    TranscriptJson,
    /// <summary>OCR extracted text</summary>
    OcrText,
    /// <summary>Chapter markers</summary>
    ChaptersTxt,
    /// <summary>Markdown document (for DocSummarizer)</summary>
    MarkdownDocument,

    // Video artifacts
    /// <summary>Short video clip</summary>
    VideoClip,
    /// <summary>Scene preview clip</summary>
    ScenePreview,
    /// <summary>Highlight reel (auto-generated)</summary>
    HighlightReel,

    // Analysis artifacts
    /// <summary>Waveform visualization</summary>
    AudioWaveform,
    /// <summary>FFprobe/mediainfo output</summary>
    MediaInfoJson,
    /// <summary>Shot boundary data</summary>
    ShotBoundaryJson,
    /// <summary>Face embeddings store</summary>
    FaceEmbeddingsJson,
    /// <summary>Speaker diarization result</summary>
    DiarizationJson
}

/// <summary>
/// Resolution info for image artifacts.
/// </summary>
public record ArtifactResolution(int Width, int Height)
{
    public static ArtifactResolution Thumbnail => new(160, 90);
    public static ArtifactResolution Small => new(320, 180);
    public static ArtifactResolution Medium => new(640, 360);
    public static ArtifactResolution Large => new(1280, 720);
    public static ArtifactResolution Original => new(0, 0); // Preserve original
}

/// <summary>
/// Quality level for artifact generation.
/// </summary>
public enum ArtifactQuality
{
    /// <summary>Lowest quality, smallest file size</summary>
    Minimal,
    /// <summary>Standard quality for storage</summary>
    Standard,
    /// <summary>High quality for viewing</summary>
    High,
    /// <summary>Original quality (no compression)</summary>
    Original
}

/// <summary>
/// Collection of artifacts for a video, organized by type.
/// </summary>
public class VideoArtifactCollection
{
    public Guid VideoId { get; init; }
    public string BasePath { get; init; } = "";
    public List<VideoArtifact> Artifacts { get; init; } = [];

    /// <summary>Get all artifacts of a specific type</summary>
    public IEnumerable<VideoArtifact> OfType(VideoArtifactType type) =>
        Artifacts.Where(a => a.Type == type);

    /// <summary>Get the primary artifact of a type (e.g., main transcript)</summary>
    public VideoArtifact? GetPrimary(VideoArtifactType type) =>
        Artifacts.FirstOrDefault(a => a.Type == type);

    /// <summary>Get all text-based artifacts for DocSummarizer</summary>
    public IEnumerable<VideoArtifact> GetTextArtifacts() =>
        Artifacts.Where(a => a.Type is
            VideoArtifactType.SubtitleSrt or
            VideoArtifactType.SubtitleVtt or
            VideoArtifactType.TranscriptTxt or
            VideoArtifactType.TranscriptJson or
            VideoArtifactType.OcrText or
            VideoArtifactType.MarkdownDocument or
            VideoArtifactType.ChaptersTxt);

    /// <summary>Get all image artifacts for display/analysis</summary>
    public IEnumerable<VideoArtifact> GetImageArtifacts() =>
        Artifacts.Where(a => a.Type is
            VideoArtifactType.SceneFrame or
            VideoArtifactType.Keyframe or
            VideoArtifactType.Filmstrip or
            VideoArtifactType.FaceThumbnail or
            VideoArtifactType.MotionHeatmap);

    /// <summary>Get all audio artifacts for AudioSummarizer</summary>
    public IEnumerable<VideoArtifact> GetAudioArtifacts() =>
        Artifacts.Where(a => a.Type is
            VideoArtifactType.AudioTrackOriginal or
            VideoArtifactType.AudioTrackProcessed or
            VideoArtifactType.SpeechSegment);

    /// <summary>Add a new artifact</summary>
    public void Add(VideoArtifact artifact) => Artifacts.Add(artifact);

    /// <summary>Total storage used by all artifacts</summary>
    public long TotalSize => Artifacts.Sum(a => a.FileSize);
}

/// <summary>
/// Options for artifact generation.
/// </summary>
public record ArtifactGenerationOptions
{
    /// <summary>Enable scene frame extraction</summary>
    public bool ExtractSceneFrames { get; init; } = true;

    /// <summary>Resolution for scene frames</summary>
    public ArtifactResolution SceneFrameResolution { get; init; } = ArtifactResolution.Small;

    /// <summary>Enable keyframe extraction</summary>
    public bool ExtractKeyframes { get; init; } = true;

    /// <summary>Resolution for keyframes</summary>
    public ArtifactResolution KeyframeResolution { get; init; } = ArtifactResolution.Medium;

    /// <summary>Enable filmstrip generation</summary>
    public bool GenerateFilmstrip { get; init; } = false;

    /// <summary>Number of frames per filmstrip row</summary>
    public int FilmstripColumns { get; init; } = 5;

    /// <summary>Enable audio extraction</summary>
    public bool ExtractAudio { get; init; } = true;

    /// <summary>Audio format (mp3, wav, flac)</summary>
    public string AudioFormat { get; init; } = "wav";

    /// <summary>Audio sample rate</summary>
    public int AudioSampleRate { get; init; } = 16000;

    /// <summary>Enable transcript text export</summary>
    public bool ExportTranscript { get; init; } = true;

    /// <summary>Enable SRT subtitle generation</summary>
    public bool GenerateSubtitles { get; init; } = true;

    /// <summary>Enable OCR text export</summary>
    public bool ExportOcr { get; init; } = true;

    /// <summary>Enable markdown document generation (for DocSummarizer)</summary>
    public bool GenerateMarkdown { get; init; } = true;

    /// <summary>Enable scene clip generation</summary>
    public bool GenerateSceneClips { get; init; } = false;

    /// <summary>Max clip duration in seconds</summary>
    public double MaxClipDuration { get; init; } = 30.0;

    /// <summary>Base output directory</summary>
    public string OutputDirectory { get; init; } = "";

    /// <summary>Cleanup temporary files after processing</summary>
    public bool CleanupTempFiles { get; init; } = true;
}

/// <summary>
/// Markdown document template for DocSummarizer integration.
/// Combines all video evidence into a structured document.
/// </summary>
public record VideoMarkdownDocument
{
    public required Guid VideoId { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public TimeSpan Duration { get; init; }

    /// <summary>Video metadata section</summary>
    public VideoMetadataSection Metadata { get; init; } = new();

    /// <summary>Scene-by-scene breakdown</summary>
    public List<SceneSection> Scenes { get; init; } = [];

    /// <summary>Full transcript section</summary>
    public TranscriptSection? Transcript { get; set; }

    /// <summary>Detected entities (people, topics)</summary>
    public List<EntitySection> Entities { get; init; } = [];

    /// <summary>Generate markdown content</summary>
    public string ToMarkdown()
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"# {Title}");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(Description))
        {
            sb.AppendLine(Description);
            sb.AppendLine();
        }

        // Metadata
        sb.AppendLine("## Video Information");
        sb.AppendLine();
        sb.AppendLine($"- **Duration**: {Duration:hh\\:mm\\:ss}");
        if (!string.IsNullOrEmpty(Metadata.Format))
            sb.AppendLine($"- **Format**: {Metadata.Format}");
        if (!string.IsNullOrEmpty(Metadata.Resolution))
            sb.AppendLine($"- **Resolution**: {Metadata.Resolution}");
        if (Metadata.Speakers?.Count > 0)
            sb.AppendLine($"- **Speakers**: {string.Join(", ", Metadata.Speakers)}");
        sb.AppendLine();

        // Scenes
        if (Scenes.Count > 0)
        {
            sb.AppendLine("## Scenes");
            sb.AppendLine();

            foreach (var scene in Scenes)
            {
                sb.AppendLine($"### Scene {scene.Index + 1}: {scene.Label ?? "Untitled"}");
                sb.AppendLine();
                sb.AppendLine($"*{FormatTime(scene.StartTime)} - {FormatTime(scene.EndTime)}*");
                sb.AppendLine();

                if (!string.IsNullOrEmpty(scene.Summary))
                {
                    sb.AppendLine(scene.Summary);
                    sb.AppendLine();
                }

                if (scene.OcrText?.Count > 0)
                {
                    sb.AppendLine("**On-screen text:**");
                    foreach (var text in scene.OcrText.Take(5))
                        sb.AppendLine($"- {text}");
                    sb.AppendLine();
                }

                if (!string.IsNullOrEmpty(scene.TranscriptExcerpt))
                {
                    sb.AppendLine("**Transcript:**");
                    sb.AppendLine($"> {scene.TranscriptExcerpt}");
                    sb.AppendLine();
                }
            }
        }

        // Full transcript
        if (Transcript != null && !string.IsNullOrEmpty(Transcript.FullText))
        {
            sb.AppendLine("## Full Transcript");
            sb.AppendLine();

            if (Transcript.SpeakerSegments?.Count > 0)
            {
                foreach (var segment in Transcript.SpeakerSegments)
                {
                    sb.AppendLine($"**{segment.Speaker}** ({FormatTime(segment.StartTime)}):");
                    sb.AppendLine(segment.Text);
                    sb.AppendLine();
                }
            }
            else
            {
                sb.AppendLine(Transcript.FullText);
                sb.AppendLine();
            }
        }

        // Entities
        if (Entities.Count > 0)
        {
            sb.AppendLine("## Detected Entities");
            sb.AppendLine();

            var people = Entities.Where(e => e.Type == "person").ToList();
            if (people.Count > 0)
            {
                sb.AppendLine("### People");
                foreach (var person in people)
                    sb.AppendLine($"- **{person.Name}**: {person.Description}");
                sb.AppendLine();
            }

            var topics = Entities.Where(e => e.Type == "topic").ToList();
            if (topics.Count > 0)
            {
                sb.AppendLine("### Topics");
                foreach (var topic in topics)
                    sb.AppendLine($"- **{topic.Name}**: {topic.Description}");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
    }
}

public record VideoMetadataSection
{
    public string? Format { get; init; }
    public string? Resolution { get; init; }
    public List<string>? Speakers { get; init; }
    public string? Language { get; init; }
    public DateTimeOffset? RecordedDate { get; init; }
}

public record SceneSection
{
    public int Index { get; init; }
    public string? Label { get; init; }
    public double StartTime { get; init; }
    public double EndTime { get; init; }
    public string? Summary { get; init; }
    public string? TranscriptExcerpt { get; init; }
    public List<string>? OcrText { get; init; }
    public string? KeyframePath { get; init; }
}

public record TranscriptSection
{
    public string? FullText { get; init; }
    public string? Language { get; init; }
    public List<SpeakerSegmentSection>? SpeakerSegments { get; init; }
}

public record SpeakerSegmentSection
{
    public string Speaker { get; init; } = "";
    public double StartTime { get; init; }
    public string Text { get; init; } = "";
}

public record EntitySection
{
    public string Type { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public List<double>? AppearanceTimes { get; init; }
}
