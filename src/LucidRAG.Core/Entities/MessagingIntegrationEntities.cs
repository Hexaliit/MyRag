namespace LucidRAG.Entities;

public class MessagingTenantConfigEntity
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Platform { get; set; } = "slack";
    public bool Enabled { get; set; }
    public string WorkspaceId { get; set; } = "";
    public string? WorkspaceName { get; set; }
    public string SigningSecret { get; set; } = "";
    public string? BotToken { get; set; }
    public bool AllowSearch { get; set; } = true;
    public bool AllowChat { get; set; } = true;
    public bool AllowCommands { get; set; } = true;
    public bool AllowMentions { get; set; } = true;
    public bool EnableSentimentLearning { get; set; } = true;
    public Guid? DefaultCollectionId { get; set; }
    public string AllowedCollectionIdsJson { get; set; } = "[]";
    public string AllowedChannelIdsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class MessagingContextStateEntity
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Platform { get; set; } = "slack";
    public string WorkspaceId { get; set; } = "";
    public string ScopeType { get; set; } = "thread"; // thread | room | user
    public string ScopeKey { get; set; } = "";
    public Guid? ConversationId { get; set; }
    public Guid? CollectionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class MessagingInteractionFeedbackEntity
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Platform { get; set; } = "slack";
    public string WorkspaceId { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string? UserId { get; set; }
    public string? ThreadId { get; set; }
    public string? RoomId { get; set; }
    public string? MessageId { get; set; }
    public Guid? CollectionId { get; set; }
    public string Mode { get; set; } = "chat";
    public string FeedbackType { get; set; } = "implicit";
    public string QueryHash { get; set; } = "";
    public double QuerySentiment { get; set; }
    public double ReplySentiment { get; set; }
    public double? ReactionSentiment { get; set; }
    public double CompositeSentiment { get; set; }
    public string EmojiSignalsJson { get; set; } = "[]";
    public string? ReactionSignalsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
