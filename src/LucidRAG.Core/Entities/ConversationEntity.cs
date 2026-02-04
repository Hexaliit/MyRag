namespace LucidRAG.Entities;

public class ConversationEntity
{
    public Guid Id { get; set; }
    public Guid? CollectionId { get; set; }
    public string? Title { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    ///     JSON array of document IDs that were retrieved for this conversation's topic.
    ///     Used for follow-up questions that should search the same document set.
    /// </summary>
    public string? ActiveDocumentIds { get; set; }

    /// <summary>
    ///     Topic signature from the initial question (semantic embedding hash).
    ///     Used to detect if a follow-up is about the same topic.
    /// </summary>
    public string? TopicSignature { get; set; }

    /// <summary>
    ///     Last query that established the document set.
    ///     Used for coreference resolution in follow-ups.
    /// </summary>
    public string? LastTopicQuery { get; set; }

    // Navigation
    public CollectionEntity? Collection { get; set; }
    public ICollection<ConversationMessage> Messages { get; set; } = [];
}

public class ConversationMessage
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public required string Role { get; set; } // user, assistant, system
    public required string Content { get; set; }
    public string? Metadata { get; set; } // JSON - sources, entities referenced
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public ConversationEntity? Conversation { get; set; }
}