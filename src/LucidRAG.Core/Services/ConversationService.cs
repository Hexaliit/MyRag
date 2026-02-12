using System.Text;
using System.Text.Json;
using LucidRAG.Data;
using LucidRAG.Entities;

namespace LucidRAG.Services;

public class ConversationService(
    RagDocumentsDbContext db,
    ILogger<ConversationService> logger) : IConversationService
{
    public async Task<ConversationEntity> CreateConversationAsync(Guid? collectionId = null, string? title = null,
        CancellationToken ct = default)
    {
        var conversation = new ConversationEntity
        {
            Id = Guid.NewGuid(),
            CollectionId = collectionId,
            Title = title // Keep null if not provided - will be set from first message
        };

        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Created conversation {ConversationId}", conversation.Id);
        return conversation;
    }

    public async Task<ConversationEntity?> GetConversationAsync(Guid conversationId, CancellationToken ct = default)
    {
        return await db.Conversations
            .AsNoTracking()
            .Include(c => c.Messages.OrderBy(m => m.CreatedAt))
            .Include(c => c.Collection)
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct);
    }

    public async Task<List<ConversationEntity>> GetConversationsAsync(Guid? collectionId = null,
        CancellationToken ct = default)
    {
        var query = db.Conversations.AsNoTracking().Include(c => c.Collection).AsQueryable();

        if (collectionId.HasValue) query = query.Where(c => c.CollectionId == collectionId);

        return await query.OrderByDescending(c => c.UpdatedAt).ToListAsync(ct);
    }

    public async Task<ConversationMessage> AddMessageAsync(Guid conversationId, string role, string content,
        string? metadata = null, CancellationToken ct = default)
    {
        var conversation = await db.Conversations.FindAsync([conversationId], ct)
                           ?? throw new InvalidOperationException($"Conversation {conversationId} not found");

        // Ensure stable ordering even on providers that store DateTimeOffset with millisecond precision (e.g. SQLite).
        // When messages are inserted in a tight loop, multiple rows can share the same timestamp, making "last N" queries
        // nondeterministic. Enforce a strictly-increasing CreatedAt per conversation.
        var now = DateTimeOffset.UtcNow;
        var lastCreatedAt = await db.ConversationMessages
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => m.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (lastCreatedAt != default)
        {
            var nowMs = now.ToUnixTimeMilliseconds();
            var lastMs = lastCreatedAt.ToUnixTimeMilliseconds();
            if (nowMs <= lastMs) now = DateTimeOffset.FromUnixTimeMilliseconds(lastMs + 1);
        }

        var message = new ConversationMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Role = role,
            Content = content,
            Metadata = metadata,
            CreatedAt = now
        };

        db.ConversationMessages.Add(message);

        conversation.UpdatedAt = now;
        if (role == "user" && string.IsNullOrEmpty(conversation.Title))
            // Use first user message as title
            conversation.Title = content.Length > 50 ? content[..47] + "..." : content;

        await db.SaveChangesAsync(ct);
        return message;
    }

    public async Task<string> BuildContextAsync(Guid conversationId, int maxMessages = 10,
        CancellationToken ct = default)
    {
        var messages = await db.ConversationMessages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(maxMessages)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

        if (messages.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine("Previous conversation:");
        foreach (var msg in messages) sb.AppendLine($"{msg.Role}: {msg.Content}");

        return sb.ToString();
    }

    public async Task DeleteConversationAsync(Guid conversationId, CancellationToken ct = default)
    {
        var conversation = await db.Conversations.FindAsync([conversationId], ct);
        if (conversation is null) return;

        db.Conversations.Remove(conversation);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Deleted conversation {ConversationId}", conversationId);
    }

    public async Task SetActiveDocumentsAsync(
        Guid conversationId,
        Guid[] documentIds,
        string topicQuery,
        string? topicSignature = null,
        CancellationToken ct = default)
    {
        var conversation = await db.Conversations.FindAsync([conversationId], ct);
        if (conversation is null)
        {
            logger.LogWarning("Cannot set active documents: conversation {ConversationId} not found", conversationId);
            return;
        }

        conversation.ActiveDocumentIds = JsonSerializer.Serialize(documentIds);
        conversation.LastTopicQuery = topicQuery;
        conversation.TopicSignature = topicSignature;
        conversation.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        logger.LogDebug("Set {Count} active documents for conversation {ConversationId}", documentIds.Length,
            conversationId);
    }

    public async Task<Guid[]?> GetActiveDocumentsAsync(Guid conversationId, CancellationToken ct = default)
    {
        var conversation = await db.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct);

        if (conversation?.ActiveDocumentIds is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<Guid[]>(conversation.ActiveDocumentIds);
        }
        catch (JsonException)
        {
            logger.LogWarning("Failed to deserialize active document IDs for conversation {ConversationId}",
                conversationId);
            return null;
        }
    }

    public async Task<string?> GetLastTopicQueryAsync(Guid conversationId, CancellationToken ct = default)
    {
        var conversation = await db.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct);

        return conversation?.LastTopicQuery;
    }

    public async Task<string?> GetTopicSignatureAsync(Guid conversationId, CancellationToken ct = default)
    {
        var conversation = await db.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct);

        return conversation?.TopicSignature;
    }
}