using LucidRAG.Entities;

namespace LucidRAG.Services;

public interface IConversationService
{
    Task<ConversationEntity> CreateConversationAsync(Guid? collectionId = null, string? title = null,
        CancellationToken ct = default);

    Task<ConversationEntity?> GetConversationAsync(Guid conversationId, CancellationToken ct = default);
    Task<List<ConversationEntity>> GetConversationsAsync(Guid? collectionId = null, CancellationToken ct = default);

    Task<ConversationMessage> AddMessageAsync(Guid conversationId, string role, string content, string? metadata = null,
        CancellationToken ct = default);

    Task<string> BuildContextAsync(Guid conversationId, int maxMessages = 10, CancellationToken ct = default);
    Task DeleteConversationAsync(Guid conversationId, CancellationToken ct = default);

    /// <summary>
    ///     Updates the active document set for follow-up questions.
    ///     Called when a new topic-establishing query is made.
    /// </summary>
    Task SetActiveDocumentsAsync(Guid conversationId, Guid[] documentIds, string topicQuery,
        string? topicSignature = null, CancellationToken ct = default);

    /// <summary>
    ///     Gets the active document IDs for follow-up questions.
    ///     Returns null if no active document set or if conversation doesn't exist.
    /// </summary>
    Task<Guid[]?> GetActiveDocumentsAsync(Guid conversationId, CancellationToken ct = default);

    /// <summary>
    ///     Gets the last topic query for coreference resolution.
    /// </summary>
    Task<string?> GetLastTopicQueryAsync(Guid conversationId, CancellationToken ct = default);

    /// <summary>
    ///     Gets the topic signature for semantic similarity comparison.
    /// </summary>
    Task<string?> GetTopicSignatureAsync(Guid conversationId, CancellationToken ct = default);
}