using DoomSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Services;

/// <summary>
///     Indexes conversation turns as searchable ContentItems using existing
///     storage infrastructure. Enables cross-session conversation memory.
/// </summary>
public sealed class ChatCorpusService
{
    private readonly IEmbeddingService _embedding;
    private readonly StorageService _storage;

    public ChatCorpusService(StorageService storage, IEmbeddingService embedding)
    {
        _storage = storage;
        _embedding = embedding;
    }

    /// <summary>
    ///     Index a Q+A turn as a ContentItem, making it searchable in future sessions.
    ///     Designed to be called fire-and-forget (non-blocking).
    /// </summary>
    public async Task IndexTurnAsync(
        string sessionId, int turnNumber,
        string question, string answer,
        List<string> sourceIds,
        CancellationToken ct)
    {
        var content = $"Q: {question}\nA: {answer}";

        var item = new ContentItem
        {
            Id = $"chat:{sessionId}:{turnNumber:D3}",
            Source = $"chat:{sessionId}",
            Title = question.Length > 80 ? question[..77] + "..." : question,
            Content = content,
            Summary = answer.Length > 300 ? answer[..297] + "..." : answer,
            CreatedAt = DateTimeOffset.UtcNow,
            FetchedAt = DateTimeOffset.UtcNow,
            Tags = ["chat", "conversation"],
            ParentDocumentId = $"chat:{sessionId}",
            ChunkSequence = turnNumber
        };

        // Step 1: Extract keywords
        var profile = DocumentProfileService.ExtractProfile(question, content);
        item = item with { Keywords = profile.KeywordsText };

        // Step 2: Embed the content
        try
        {
            var embedding = await _embedding.EmbedAsync(content, ct);
            item = item with { Embedding = embedding };
        }
        catch
        {
            // Embedding failure is non-fatal
        }

        // Step 3: Save to SQLite
        await _storage.SaveItemAsync(item);

        // Step 4: Index for FTS5
        var contentPreview = content.Length > 500 ? content[..500] : content;
        await _storage.IndexDocumentFtsAsync(item.Id, item.Title, profile.KeywordsText, contentPreview);
    }

    /// <summary>
    ///     Record implicit feedback for a conversation turn.
    ///     Continued-topic turns get a positive signal via the existing item_usage table.
    /// </summary>
    public async Task RecordFeedbackAsync(
        string sessionId, int turnNumber,
        bool continued, CancellationToken ct)
    {
        if (!continued) return; // Only record positive signals

        var itemId = $"chat:{sessionId}:{turnNumber:D3}";
        // Leverage existing LogQueryAsync which increments access_count in item_usage
        await _storage.LogQueryAsync(
            $"feedback:{sessionId}:{turnNumber}",
            null,
            null,
            [itemId]);
    }
}