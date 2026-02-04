namespace VideoSummarizer.Core.Models;

/// <summary>
///     Represents a shot boundary in the video (hard cut, fade, etc.).
///     A shot is a continuous sequence of frames from a single camera.
/// </summary>
public record ShotSegment
{
    public required Guid Id { get; init; }
    public required Guid VideoId { get; init; }

    /// <summary>Start time in seconds</summary>
    public required double StartTime { get; init; }

    /// <summary>End time in seconds</summary>
    public required double EndTime { get; init; }

    /// <summary>Index of the keyframe that best represents this shot</summary>
    public int KeyframeIndex { get; init; }

    /// <summary>Path to the extracted keyframe image</summary>
    public string? KeyframePath { get; init; }

    /// <summary>Type of cut that started this shot</summary>
    public CutType CutType { get; init; } = CutType.HardCut;

    /// <summary>Average motion score during this shot (0-1)</summary>
    public double MotionScore { get; init; }

    /// <summary>Detected shot type (talking head, b-roll, slide, etc.)</summary>
    public ShotType ShotType { get; init; } = ShotType.Unknown;

    /// <summary>Confidence in the shot boundary detection (0-1)</summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>CLIP/SigLIP embedding of the keyframe</summary>
    public float[]? Embedding { get; init; }

    /// <summary>Perceptual hash of the keyframe for deduplication</summary>
    public string? PerceptualHash { get; init; }
}

/// <summary>
///     Type of transition that marks the start of a shot.
/// </summary>
public enum CutType
{
    HardCut,
    Fade,
    Dissolve,
    Wipe,
    Flash,
    Unknown
}

/// <summary>
///     Semantic type of shot content.
/// </summary>
public enum ShotType
{
    Unknown,
    TalkingHead,
    BRoll,
    Slide,
    ScreenShare,
    Diagram,
    UI,
    VideoCall,
    Intro,
    Outro,
    Animation
}

/// <summary>
///     Represents a scene - a semantic grouping of related shots.
/// </summary>
public record SceneSegment
{
    public required Guid Id { get; init; }
    public required Guid VideoId { get; init; }

    /// <summary>Start time in seconds</summary>
    public required double StartTime { get; init; }

    /// <summary>End time in seconds</summary>
    public required double EndTime { get; init; }

    /// <summary>Shot IDs that belong to this scene</summary>
    public List<Guid> ShotIds { get; init; } = [];

    /// <summary>Keyframe IDs representing this scene</summary>
    public List<int> KeyframeIndices { get; init; } = [];

    /// <summary>Centroid embedding (average of shot embeddings)</summary>
    public float[]? CentroidEmbedding { get; init; }

    /// <summary>Key terms extracted from transcript + OCR in this scene</summary>
    public List<string> KeyTerms { get; init; } = [];

    /// <summary>Speaker IDs active in this scene</summary>
    public List<string> SpeakerIds { get; init; } = [];

    /// <summary>Optional scene label (e.g., "Introduction", "Demo", "Pricing")</summary>
    public string? Label { get; init; }

    /// <summary>Confidence in the scene clustering (0-1)</summary>
    public double Confidence { get; init; } = 1.0;
}

/// <summary>
///     Represents tracked OCR text across frames.
///     Deduplicated text that appears in multiple frames is merged into a single track.
/// </summary>
public record TextTrack
{
    public required Guid Id { get; init; }
    public required Guid VideoId { get; init; }

    /// <summary>The extracted text content</summary>
    public required string Text { get; init; }

    /// <summary>Canonicalized text for deduplication (lowercased, normalized whitespace)</summary>
    public string? CanonicalText { get; init; }

    /// <summary>First appearance time in seconds</summary>
    public required double StartTime { get; init; }

    /// <summary>Last appearance time in seconds</summary>
    public required double EndTime { get; init; }

    /// <summary>Bounding box history over time: [(frame_idx, x, y, w, h), ...]</summary>
    public List<BoundingBoxFrame> BoundingBoxes { get; init; } = [];

    /// <summary>OCR confidence score (0-1)</summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>Type of text (slide title, lower third, UI element, etc.)</summary>
    public TextTrackType TextType { get; init; } = TextTrackType.Unknown;

    /// <summary>Related shot IDs where this text appears</summary>
    public List<Guid> ShotIds { get; init; } = [];
}

public record BoundingBoxFrame(int FrameIndex, double X, double Y, double Width, double Height);

public enum TextTrackType
{
    Unknown,
    SlideTitle,
    SlideContent,
    LowerThird,
    Subtitle,
    UIElement,
    Watermark,
    Timestamp,
    Credits,
    TitleCard
}

/// <summary>
///     Represents a speech utterance from transcription.
/// </summary>
public record Utterance
{
    public required Guid Id { get; init; }
    public required Guid VideoId { get; init; }

    /// <summary>Transcribed text</summary>
    public required string Text { get; init; }

    /// <summary>Start time in seconds</summary>
    public required double StartTime { get; init; }

    /// <summary>End time in seconds</summary>
    public required double EndTime { get; init; }

    /// <summary>Optional speaker ID from diarization</summary>
    public string? SpeakerId { get; init; }

    /// <summary>Transcription confidence (0-1)</summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>Language detected</summary>
    public string? Language { get; init; }

    /// <summary>Related shot IDs (by time overlap)</summary>
    public List<Guid> ShotIds { get; init; } = [];

    /// <summary>Related scene IDs (by time overlap)</summary>
    public List<Guid> SceneIds { get; init; } = [];
}

/// <summary>
///     Represents a detected speaker in the video.
/// </summary>
public record Speaker
{
    public required string Id { get; init; }
    public required Guid VideoId { get; init; }

    /// <summary>Speaker embedding for identification</summary>
    public float[]? Embedding { get; init; }

    /// <summary>Optional name if identified</summary>
    public string? Name { get; init; }

    /// <summary>Total speaking time in seconds</summary>
    public double TotalSpeakingTime { get; init; }

    /// <summary>Number of utterances by this speaker</summary>
    public int UtteranceCount { get; init; }

    /// <summary>Confidence in speaker identification (0-1)</summary>
    public double Confidence { get; init; } = 1.0;
}

/// <summary>
///     Video-level metadata and signals.
/// </summary>
public record VideoMetadata
{
    public required Guid Id { get; init; }

    /// <summary>Content hash for deduplication</summary>
    public required string ContentHash { get; init; }

    /// <summary>Duration in seconds</summary>
    public double Duration { get; init; }

    /// <summary>Frames per second</summary>
    public double Fps { get; init; }

    /// <summary>Video width in pixels</summary>
    public int Width { get; init; }

    /// <summary>Video height in pixels</summary>
    public int Height { get; init; }

    /// <summary>Video codec</summary>
    public string? VideoCodec { get; init; }

    /// <summary>Audio codec</summary>
    public string? AudioCodec { get; init; }

    /// <summary>Audio sample rate</summary>
    public int AudioSampleRate { get; init; }

    /// <summary>Number of audio channels</summary>
    public int AudioChannels { get; init; }

    /// <summary>Total number of extracted shots</summary>
    public int ShotCount { get; init; }

    /// <summary>Total number of detected scenes</summary>
    public int SceneCount { get; init; }

    /// <summary>Total number of text tracks</summary>
    public int TextTrackCount { get; init; }

    /// <summary>Total number of utterances</summary>
    public int UtteranceCount { get; init; }

    /// <summary>Processing status</summary>
    public VideoProcessingStatus Status { get; init; } = VideoProcessingStatus.Pending;

    /// <summary>When processing started</summary>
    public DateTimeOffset? ProcessingStarted { get; init; }

    /// <summary>When processing completed</summary>
    public DateTimeOffset? ProcessingCompleted { get; init; }
}

public enum VideoProcessingStatus
{
    Pending,
    Normalizing,
    DetectingShots,
    ExtractingKeyframes,
    ExtractingOcr,
    Transcribing,
    ClusteringScenes,
    Completed,
    Failed
}

/// <summary>
///     Evidence pointer for retrieval - points to a specific moment in the video.
/// </summary>
public record VideoEvidence
{
    public required Guid Id { get; init; }
    public required Guid VideoId { get; init; }

    /// <summary>Type of evidence</summary>
    public required VideoEvidenceType Type { get; init; }

    /// <summary>Start time in seconds</summary>
    public required double StartTime { get; init; }

    /// <summary>End time in seconds</summary>
    public required double EndTime { get; init; }

    /// <summary>Optional frame index for single-frame evidence</summary>
    public int? FrameIndex { get; init; }

    /// <summary>Path to keyframe image (if applicable)</summary>
    public string? KeyframePath { get; init; }

    /// <summary>Path to short video clip (if generated)</summary>
    public string? ClipPath { get; init; }

    /// <summary>Text content (for OCR/transcript evidence)</summary>
    public string? TextContent { get; init; }

    /// <summary>Relevance score (0-1) for retrieval ranking</summary>
    public double RelevanceScore { get; init; } = 1.0;

    /// <summary>Source of this evidence (model/version/params)</summary>
    public string? Provenance { get; init; }

    /// <summary>Embedding for semantic search</summary>
    public float[]? Embedding { get; init; }
}

public enum VideoEvidenceType
{
    Scene,
    Shot,
    Keyframe,
    TextTrack,
    Utterance,
    SpeakerSegment
}