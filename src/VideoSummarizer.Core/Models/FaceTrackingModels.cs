namespace VideoSummarizer.Core.Models;

/// <summary>
///     Persistent face identity for tracking across videos.
///     Privacy-preserving: uses embeddings only, no face images stored.
/// </summary>
public record FaceIdentity
{
    public required Guid Id { get; init; }

    /// <summary>User-assigned label (optional, e.g., "Person A", "Speaker 1")</summary>
    public string? Label { get; set; }

    /// <summary>Known name if identified (from cast matching, user input, etc.)</summary>
    public string? KnownName { get; set; }

    /// <summary>Centroid embedding (average of all face embeddings for this person)</summary>
    public required float[] CentroidEmbedding { get; init; }

    /// <summary>Number of face samples contributing to the centroid</summary>
    public int SampleCount { get; init; } = 1;

    /// <summary>Confidence in the identity (based on sample variance)</summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>When this identity was first detected</summary>
    public DateTimeOffset FirstSeen { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When this identity was last seen</summary>
    public DateTimeOffset LastSeen { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Source of identification (auto-cluster, cast-match, user-input)</summary>
    public IdentitySource Source { get; set; } = IdentitySource.AutoCluster;

    /// <summary>Videos where this face has been detected</summary>
    public List<Guid> VideoIds { get; init; } = [];

    /// <summary>If matched to a cast member, their TMDB/IMDB ID</summary>
    public string? ExternalActorId { get; init; }
}

public enum IdentitySource
{
    AutoCluster,
    CastMatch,
    UserInput,
    SpeakerDiarization
}

/// <summary>
///     Face appearance in a specific video frame.
/// </summary>
public record FaceAppearance
{
    public required Guid Id { get; init; }
    public required Guid VideoId { get; init; }
    public required Guid FaceIdentityId { get; init; }

    /// <summary>Frame number where face appears</summary>
    public required int FrameIndex { get; init; }

    /// <summary>Time in seconds</summary>
    public required double Timestamp { get; init; }

    /// <summary>Bounding box in the frame</summary>
    public required FaceBoundingBox BoundingBox { get; init; }

    /// <summary>Face embedding for this specific appearance</summary>
    public required float[] Embedding { get; init; }

    /// <summary>Detection confidence</summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>Estimated head pose (yaw, pitch, roll)</summary>
    public HeadPose? HeadPose { get; init; }

    /// <summary>Whether face is partially occluded</summary>
    public bool IsOccluded { get; init; }

    /// <summary>Whether face is blurred</summary>
    public bool IsBlurred { get; init; }

    /// <summary>Related shot ID</summary>
    public Guid? ShotId { get; init; }

    /// <summary>If speaking during this frame (from diarization)</summary>
    public bool IsSpeaking { get; init; }
}

public record FaceBoundingBox(double X, double Y, double Width, double Height);

public record HeadPose(double Yaw, double Pitch, double Roll);

/// <summary>
///     Face track - continuous appearance of a face across frames.
/// </summary>
public record FaceTrack
{
    public required Guid Id { get; init; }
    public required Guid VideoId { get; init; }
    public required Guid FaceIdentityId { get; init; }

    /// <summary>Start time in seconds</summary>
    public required double StartTime { get; init; }

    /// <summary>End time in seconds</summary>
    public required double EndTime { get; init; }

    /// <summary>Start frame index</summary>
    public required int StartFrame { get; init; }

    /// <summary>End frame index</summary>
    public required int EndFrame { get; init; }

    /// <summary>Individual face appearances in this track</summary>
    public List<FaceAppearance> Appearances { get; init; } = [];

    /// <summary>Average confidence across the track</summary>
    public double AverageConfidence { get; init; } = 1.0;

    /// <summary>Track quality score (based on face sizes, angles, blur)</summary>
    public double QualityScore { get; init; } = 1.0;

    /// <summary>Best frame for this track (clearest face)</summary>
    public int BestFrameIndex { get; init; }

    /// <summary>Related shot IDs</summary>
    public List<Guid> ShotIds { get; init; } = [];

    /// <summary>Total speaking time if linked to diarization</summary>
    public double SpeakingTime { get; init; }
}

/// <summary>
///     Speaker-face linking for video meetings.
///     Links audio speaker diarization with visual face tracking.
/// </summary>
public record SpeakerFaceLink
{
    public required Guid Id { get; init; }
    public required Guid VideoId { get; init; }

    /// <summary>Speaker ID from audio diarization</summary>
    public required string SpeakerId { get; init; }

    /// <summary>Face identity ID from visual tracking</summary>
    public required Guid FaceIdentityId { get; init; }

    /// <summary>Confidence in the link (based on temporal correlation)</summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>Method used to establish the link</summary>
    public LinkingMethod Method { get; init; } = LinkingMethod.TemporalCorrelation;

    /// <summary>Evidence for the link (e.g., lip sync score)</summary>
    public Dictionary<string, double> Evidence { get; init; } = [];
}

public enum LinkingMethod
{
    TemporalCorrelation,
    LipSync,
    SpatialPosition,
    UserInput
}

/// <summary>
///     Face database for a collection/library.
/// </summary>
public record FaceDatabase
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }

    /// <summary>All known face identities</summary>
    public List<FaceIdentity> Identities { get; init; } = [];

    /// <summary>Similarity threshold for matching (0-1)</summary>
    public double SimilarityThreshold { get; init; } = 0.75;

    /// <summary>When database was last updated</summary>
    public DateTimeOffset LastUpdated { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Statistics</summary>
    public FaceDatabaseStats Stats { get; init; } = new();
}

public record FaceDatabaseStats
{
    public int TotalIdentities { get; init; }
    public int NamedIdentities { get; init; }
    public int TotalAppearances { get; init; }
    public int VideosProcessed { get; init; }
}

/// <summary>
///     Options for face matching.
/// </summary>
public record FaceMatchOptions
{
    /// <summary>Minimum cosine similarity to consider a match</summary>
    public double SimilarityThreshold { get; init; } = 0.75;

    /// <summary>Whether to create new identity if no match found</summary>
    public bool CreateNewIfNoMatch { get; init; } = true;

    /// <summary>Whether to update centroid on match</summary>
    public bool UpdateCentroidOnMatch { get; init; } = true;

    /// <summary>Maximum number of matches to return</summary>
    public int MaxMatches { get; init; } = 5;
}