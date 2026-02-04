namespace VideoSummarizer.Core.Models;

/// <summary>
///     Unified identity that can link faces, speakers, and named entities across modalities.
///     Designed for cross-referencing people detected in documents, audio, and video.
/// </summary>
public record UnifiedIdentity
{
    public required Guid Id { get; init; }

    /// <summary>Display name (user-assigned or best guess)</summary>
    public string? DisplayName { get; set; }

    /// <summary>Aliases / alternative names</summary>
    public List<string> Aliases { get; init; } = [];

    /// <summary>Source that provided the canonical name</summary>
    public NameSource NameSource { get; init; } = NameSource.Unknown;

    /// <summary>Linked face identities (visual)</summary>
    public List<Guid> FaceIdentityIds { get; init; } = [];

    /// <summary>Linked speaker IDs (audio diarization)</summary>
    public List<string> SpeakerIds { get; init; } = [];

    /// <summary>Named entity mentions from documents</summary>
    public List<NamedEntityMention> DocumentMentions { get; init; } = [];

    /// <summary>Confidence in the identity linkage</summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>External IDs (IMDB actor ID, LinkedIn, etc.)</summary>
    public Dictionary<string, string> ExternalIds { get; init; } = [];

    /// <summary>User-selected representative face embedding</summary>
    public float[]? RepresentativeFaceEmbedding { get; set; }

    /// <summary>User-selected thumbnail/avatar path</summary>
    public string? ThumbnailPath { get; set; }

    /// <summary>Notes/description added by user</summary>
    public string? Notes { get; set; }

    /// <summary>When identity was created</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When identity was last updated</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Whether this identity has been verified by a user</summary>
    public bool IsVerified { get; set; }

    /// <summary>Collection/library this identity belongs to</summary>
    public Guid? CollectionId { get; init; }
}

public enum NameSource
{
    Unknown,
    UserInput,
    DocumentNER,
    AudioTranscript,
    CastMetadata,
    SpeakerDiarization,
    ExternalDatabase
}

/// <summary>
///     Named entity mention in a document.
/// </summary>
public record NamedEntityMention
{
    public required Guid DocumentId { get; init; }
    public required string EntityText { get; init; }
    public required string EntityType { get; init; } // PERSON, ORG, etc.
    public int StartOffset { get; init; }
    public int EndOffset { get; init; }
    public double Confidence { get; init; } = 1.0;
    public string? Context { get; init; } // Surrounding text
}

/// <summary>
///     Link between a unified identity and a specific appearance.
/// </summary>
public record IdentityAppearance
{
    public required Guid Id { get; init; }
    public required Guid UnifiedIdentityId { get; init; }
    public required AppearanceType Type { get; init; }

    /// <summary>Source document/video/audio ID</summary>
    public required Guid SourceId { get; init; }

    /// <summary>Time range (for video/audio)</summary>
    public double? StartTime { get; init; }

    public double? EndTime { get; init; }

    /// <summary>Text offset (for documents)</summary>
    public int? StartOffset { get; init; }

    public int? EndOffset { get; init; }

    /// <summary>Reference to face track (for video)</summary>
    public Guid? FaceTrackId { get; init; }

    /// <summary>Reference to utterance (for audio)</summary>
    public Guid? UtteranceId { get; init; }

    /// <summary>The text content (name as mentioned)</summary>
    public string? MentionText { get; init; }

    /// <summary>Confidence in this appearance</summary>
    public double Confidence { get; init; } = 1.0;
}

public enum AppearanceType
{
    FaceInVideo,
    SpeakerInAudio,
    NameInDocument,
    NameInTranscript,
    CreditInVideo
}

/// <summary>
///     Request to merge multiple identities into one.
/// </summary>
public record MergeIdentitiesRequest
{
    public required Guid PrimaryIdentityId { get; init; }
    public required List<Guid> SecondaryIdentityIds { get; init; }
    public string? NewDisplayName { get; init; }
}

/// <summary>
///     Request to split an identity into multiple.
/// </summary>
public record SplitIdentityRequest
{
    public required Guid IdentityId { get; init; }
    public required List<Guid> FaceTrackIdsToSplit { get; init; }
    public string? NewIdentityName { get; init; }
}

/// <summary>
///     Face signature selection for identity assignment (Immich-style).
/// </summary>
public record FaceSignatureSelection
{
    public required Guid FaceIdentityId { get; init; }

    /// <summary>Selected face appearance to use as representative</summary>
    public required Guid RepresentativeAppearanceId { get; init; }

    /// <summary>Frame path for the selected face</summary>
    public string? FramePath { get; init; }

    /// <summary>Cropped face image path (if generated)</summary>
    public string? CroppedFacePath { get; init; }

    /// <summary>Quality score of this selection</summary>
    public double QualityScore { get; init; } = 1.0;
}

/// <summary>
///     Suggestion for identity assignment.
/// </summary>
public record IdentitySuggestion
{
    public required Guid FaceIdentityId { get; init; }
    public required string SuggestedName { get; init; }
    public required SuggestionSource Source { get; init; }
    public double Confidence { get; init; } = 1.0;

    /// <summary>Evidence for the suggestion</summary>
    public string? Evidence { get; init; }

    /// <summary>External ID if from cast metadata</summary>
    public string? ExternalId { get; init; }
}

public enum SuggestionSource
{
    CastMetadata,
    DocumentNER,
    TranscriptMention,
    Credits,
    SimilarFace,
    UserHistory
}

/// <summary>
///     Statistics for identity tracking.
/// </summary>
public record IdentityTrackingStats
{
    public int TotalUnifiedIdentities { get; init; }
    public int NamedIdentities { get; init; }
    public int UnnamedIdentities { get; init; }
    public int VerifiedIdentities { get; init; }

    public int TotalFaceIdentities { get; init; }
    public int LinkedFaceIdentities { get; init; }

    public int TotalSpeakers { get; init; }
    public int LinkedSpeakers { get; init; }

    public int TotalDocumentMentions { get; init; }
    public int LinkedDocumentMentions { get; init; }
}