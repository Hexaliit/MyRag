using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Pgvector;

#nullable disable

namespace LucidRAG.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    AvatarUrl = table.Column<string>(type: "text", nullable: true),
                    TenantId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "text", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "collections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Settings = table.Column<string>(type: "jsonb", nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_collections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "entities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CanonicalName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Aliases = table.Column<string[]>(type: "text[]", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "messaging_context_states",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Platform = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    WorkspaceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ScopeType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ScopeKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messaging_context_states", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "messaging_interaction_feedback",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Platform = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    WorkspaceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ChannelId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    UserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ThreadId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RoomId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    MessageId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FeedbackType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    QueryHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    QuerySentiment = table.Column<double>(type: "double precision", nullable: false),
                    ReplySentiment = table.Column<double>(type: "double precision", nullable: false),
                    ReactionSentiment = table.Column<double>(type: "double precision", nullable: true),
                    CompositeSentiment = table.Column<double>(type: "double precision", nullable: false),
                    EmojiSignalsJson = table.Column<string>(type: "jsonb", nullable: false),
                    ReactionSignalsJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messaging_interaction_feedback", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "messaging_tenant_configs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Platform = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    WorkspaceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    WorkspaceName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SigningSecret = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    BotToken = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    AllowSearch = table.Column<bool>(type: "boolean", nullable: false),
                    AllowChat = table.Column<bool>(type: "boolean", nullable: false),
                    AllowCommands = table.Column<bool>(type: "boolean", nullable: false),
                    AllowMentions = table.Column<bool>(type: "boolean", nullable: false),
                    EnableSentimentLearning = table.Column<bool>(type: "boolean", nullable: false),
                    DefaultCollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    AllowedCollectionIdsJson = table.Column<string>(type: "jsonb", nullable: false),
                    AllowedChannelIdsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messaging_tenant_configs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<string>(type: "text", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    RoleId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    KeyPrefix = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    KeyHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    NormalizedOwnerEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Plan = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    AllowChat = table.Column<bool>(type: "boolean", nullable: false),
                    AllowSearch = table.Column<bool>(type: "boolean", nullable: false),
                    RateLimitPerMinute = table.Column<int>(type: "integer", nullable: false),
                    RateLimitPerDay = table.Column<int>(type: "integer", nullable: false),
                    TotalRequests = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SigningSecret = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Slug = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CustomLlmApiKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CustomLlmProvider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PreferredResponseLength = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_keys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_api_keys_collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "collection_salient_terms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Term = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NormalizedTerm = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Score = table.Column<double>(type: "double precision", nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DocumentFrequency = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_collection_salient_terms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_collection_salient_terms_collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "communities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: true),
                    Features = table.Column<string>(type: "jsonb", nullable: true),
                    Algorithm = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    ParentCommunityId = table.Column<Guid>(type: "uuid", nullable: true),
                    EntityCount = table.Column<int>(type: "integer", nullable: false),
                    Cohesion = table.Column<float>(type: "real", nullable: false),
                    Embedding = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_communities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_communities_collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_communities_communities_ParentCommunityId",
                        column: x => x.ParentCommunityId,
                        principalTable: "communities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActiveDocumentIds = table.Column<string>(type: "text", nullable: true),
                    TopicSignature = table.Column<string>(type: "text", nullable: true),
                    LastTopicQuery = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_conversations_collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "feature_embeddings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    FeatureText = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    NormalizedText = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    FeatureType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Embedding = table.Column<Vector>(type: "vector(384)", nullable: true),
                    EmbeddingModel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DocumentCount = table.Column<int>(type: "integer", nullable: false),
                    OccurrenceCount = table.Column<int>(type: "integer", nullable: false),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feature_embeddings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_feature_embeddings_collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "folders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentFolderId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_folders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_folders_collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_folders_folders_ParentFolderId",
                        column: x => x.ParentFolderId,
                        principalTable: "folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ingestion_sources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Location = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    FilePattern = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Recursive = table.Column<bool>(type: "boolean", nullable: false),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Options = table.Column<string>(type: "jsonb", nullable: true),
                    Credentials = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSyncAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TotalItemsIngested = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingestion_sources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ingestion_sources_collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "retrieval_entities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Source = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Summary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    TextContent = table.Column<string>(type: "text", nullable: true),
                    EmbeddingModel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    QualityScore = table.Column<double>(type: "double precision", nullable: false),
                    ContentConfidence = table.Column<double>(type: "double precision", nullable: false),
                    NeedsReview = table.Column<bool>(type: "boolean", nullable: false),
                    ReviewReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Tags = table.Column<string>(type: "jsonb", nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    CustomMetadata = table.Column<string>(type: "jsonb", nullable: true),
                    Signals = table.Column<string>(type: "jsonb", nullable: true),
                    ExtractedEntities = table.Column<string>(type: "jsonb", nullable: true),
                    Relationships = table.Column<string>(type: "jsonb", nullable: true),
                    SourceModalities = table.Column<string>(type: "jsonb", nullable: true),
                    ProcessingState = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_retrieval_entities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_retrieval_entities_collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "scanned_page_groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    GroupName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    GroupingStrategy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FilenamePattern = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DirectoryPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scanned_page_groups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_scanned_page_groups_collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "entity_relationships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceEntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetEntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelationshipType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Strength = table.Column<float>(type: "real", nullable: false),
                    SourceDocuments = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entity_relationships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_entity_relationships_entities_SourceEntityId",
                        column: x => x.SourceEntityId,
                        principalTable: "entities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_entity_relationships_entities_TargetEntityId",
                        column: x => x.TargetEntityId,
                        principalTable: "entities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "api_key_collection_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApiKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    LinkedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_key_collection_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_api_key_collection_links_api_keys_ApiKeyId",
                        column: x => x.ApiKeyId,
                        principalTable: "api_keys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_api_key_collection_links_collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "api_key_indexing_sources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApiKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceType = table.Column<int>(type: "integer", nullable: false),
                    SourceValue = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    MaxDocuments = table.Column<int>(type: "integer", nullable: false),
                    DocumentCount = table.Column<int>(type: "integer", nullable: false),
                    LastCrawledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CrawlStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextScheduledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TriggerCrawlNow = table.Column<bool>(type: "boolean", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ETag = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LastModifiedHeader = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_key_indexing_sources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_api_key_indexing_sources_api_keys_ApiKeyId",
                        column: x => x.ApiKeyId,
                        principalTable: "api_keys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "api_key_read_domains",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApiKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Domain = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_key_read_domains", x => x.Id);
                    table.ForeignKey(
                        name: "FK_api_key_read_domains_api_keys_ApiKeyId",
                        column: x => x.ApiKeyId,
                        principalTable: "api_keys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "custom_domains",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApiKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Domain = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    IsVerified = table.Column<bool>(type: "boolean", nullable: false),
                    VerificationToken = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_domains", x => x.Id);
                    table.ForeignKey(
                        name: "FK_custom_domains_api_keys_ApiKeyId",
                        column: x => x.ApiKeyId,
                        principalTable: "api_keys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "saas_query_logs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApiKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                    QueryText = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    QueryType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SearchMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ResultCount = table.Column<int>(type: "integer", nullable: false),
                    TotalTimeMs = table.Column<int>(type: "integer", nullable: false),
                    RetrievalTimeMs = table.Column<int>(type: "integer", nullable: true),
                    LlmTimeMs = table.Column<int>(type: "integer", nullable: true),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RequestDomain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    CountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saas_query_logs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_saas_query_logs_api_keys_ApiKeyId",
                        column: x => x.ApiKeyId,
                        principalTable: "api_keys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "saas_usage_rollups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApiKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    SearchCount = table.Column<long>(type: "bigint", nullable: false),
                    ChatCount = table.Column<long>(type: "bigint", nullable: false),
                    AutocompleteCount = table.Column<long>(type: "bigint", nullable: false),
                    FailedCount = table.Column<long>(type: "bigint", nullable: false),
                    AvgResponseTimeMs = table.Column<int>(type: "integer", nullable: false),
                    P95ResponseTimeMs = table.Column<int>(type: "integer", nullable: false),
                    P99ResponseTimeMs = table.Column<int>(type: "integer", nullable: false),
                    AggregatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saas_usage_rollups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_saas_usage_rollups_api_keys_ApiKeyId",
                        column: x => x.ApiKeyId,
                        principalTable: "api_keys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "widget_configs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApiKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Theme = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    AccentColor = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    BorderRadius = table.Column<int>(type: "integer", nullable: false),
                    FontFamily = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CustomCss = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    LogoUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    ShowBranding = table.Column<bool>(type: "boolean", nullable: false),
                    Position = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Placeholder = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    MaxResults = table.Column<int>(type: "integer", nullable: false),
                    CorpusStyle = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PageTitle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PageDescription = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    FaviconUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    WelcomeMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_widget_configs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_widget_configs_api_keys_ApiKeyId",
                        column: x => x.ApiKeyId,
                        principalTable: "api_keys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "community_memberships",
                columns: table => new
                {
                    CommunityId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Centrality = table.Column<float>(type: "real", nullable: false),
                    IsRepresentative = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_community_memberships", x => new { x.CommunityId, x.EntityId });
                    table.ForeignKey(
                        name: "FK_community_memberships_communities_CommunityId",
                        column: x => x.CommunityId,
                        principalTable: "communities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_community_memberships_entities_EntityId",
                        column: x => x.EntityId,
                        principalTable: "entities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "conversation_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_conversation_messages_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    OriginalFilename = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FilePath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    MimeType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    StatusMessage = table.Column<string>(type: "text", nullable: true),
                    ProcessingProgress = table.Column<float>(type: "real", nullable: false),
                    SegmentCount = table.Column<int>(type: "integer", nullable: false),
                    EntityCount = table.Column<int>(type: "integer", nullable: false),
                    TableCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    SourceUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SourcePath = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SourceCreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SourceModifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    VectorStoreDocId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    FolderId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_documents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_documents_collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_documents_folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ingestion_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ItemsDiscovered = table.Column<int>(type: "integer", nullable: false),
                    ItemsProcessed = table.Column<int>(type: "integer", nullable: false),
                    ItemsFailed = table.Column<int>(type: "integer", nullable: false),
                    ItemsSkipped = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    Errors = table.Column<string>(type: "jsonb", nullable: true),
                    IncrementalSync = table.Column<bool>(type: "boolean", nullable: false),
                    MaxItems = table.Column<int>(type: "integer", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingestion_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ingestion_jobs_ingestion_sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "ingestion_sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "entity_embeddings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Dimension = table.Column<int>(type: "integer", nullable: false),
                    Vector = table.Column<string>(type: "jsonb", nullable: true),
                    VectorBinary = table.Column<byte[]>(type: "bytea", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entity_embeddings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_entity_embeddings_retrieval_entities_EntityId",
                        column: x => x.EntityId,
                        principalTable: "retrieval_entities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "evidence_artifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MimeType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    StorageBackend = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StoragePath = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SegmentHash = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Content = table.Column<string>(type: "text", nullable: true),
                    ProducerSource = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ProducerVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evidence_artifacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_evidence_artifacts_retrieval_entities_EntityId",
                        column: x => x.EntityId,
                        principalTable: "retrieval_entities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scanned_page_memberships",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    PageNumber = table.Column<int>(type: "integer", nullable: false),
                    OriginalFilename = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scanned_page_memberships", x => new { x.GroupId, x.EntityId });
                    table.ForeignKey(
                        name: "FK_scanned_page_memberships_retrieval_entities_EntityId",
                        column: x => x.EntityId,
                        principalTable: "retrieval_entities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_scanned_page_memberships_scanned_page_groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "scanned_page_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_entities",
                columns: table => new
                {
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    MentionCount = table.Column<int>(type: "integer", nullable: false),
                    SegmentIds = table.Column<string[]>(type: "text[]", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_entities", x => new { x.DocumentId, x.EntityId });
                    table.ForeignKey(
                        name: "FK_document_entities_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_document_entities_entities_EntityId",
                        column: x => x.EntityId,
                        principalTable: "entities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "processing_signals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SignalType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    StagingPath = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_processing_signals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_processing_signals_collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_processing_signals_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "segment_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceSegmentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetSegmentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LinkType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Weight = table.Column<double>(type: "double precision", nullable: false),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_segment_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_segment_links_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_key_collection_links_ApiKeyId",
                table: "api_key_collection_links",
                column: "ApiKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_api_key_collection_links_CollectionId",
                table: "api_key_collection_links",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_api_key_indexing_sources_ApiKeyId",
                table: "api_key_indexing_sources",
                column: "ApiKeyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_key_read_domains_ApiKeyId",
                table: "api_key_read_domains",
                column: "ApiKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_CollectionId",
                table: "api_keys",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_KeyHash",
                table: "api_keys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_KeyPrefix",
                table: "api_keys",
                column: "KeyPrefix");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_NormalizedOwnerEmail",
                table: "api_keys",
                column: "NormalizedOwnerEmail");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_Slug",
                table: "api_keys",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_UserId",
                table: "api_keys",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_collection_salient_terms_CollectionId_NormalizedTerm",
                table: "collection_salient_terms",
                columns: new[] { "CollectionId", "NormalizedTerm" });

            migrationBuilder.CreateIndex(
                name: "IX_collection_salient_terms_CollectionId_Score",
                table: "collection_salient_terms",
                columns: new[] { "CollectionId", "Score" });

            migrationBuilder.CreateIndex(
                name: "IX_collection_salient_terms_UpdatedAt",
                table: "collection_salient_terms",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_collections_Name",
                table: "collections",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_communities_CollectionId",
                table: "communities",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_communities_CollectionId_Name",
                table: "communities",
                columns: new[] { "CollectionId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_communities_EntityCount",
                table: "communities",
                column: "EntityCount");

            migrationBuilder.CreateIndex(
                name: "IX_communities_Level",
                table: "communities",
                column: "Level");

            migrationBuilder.CreateIndex(
                name: "IX_communities_ParentCommunityId",
                table: "communities",
                column: "ParentCommunityId");

            migrationBuilder.CreateIndex(
                name: "IX_community_memberships_Centrality",
                table: "community_memberships",
                column: "Centrality");

            migrationBuilder.CreateIndex(
                name: "IX_community_memberships_EntityId",
                table: "community_memberships",
                column: "EntityId");

            migrationBuilder.CreateIndex(
                name: "IX_conversation_messages_ConversationId",
                table: "conversation_messages",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_CollectionId",
                table: "conversations",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_custom_domains_ApiKeyId",
                table: "custom_domains",
                column: "ApiKeyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_custom_domains_Domain",
                table: "custom_domains",
                column: "Domain",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_entities_EntityId",
                table: "document_entities",
                column: "EntityId");

            migrationBuilder.CreateIndex(
                name: "IX_documents_CollectionId",
                table: "documents",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_documents_ContentHash",
                table: "documents",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_documents_FolderId",
                table: "documents",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_documents_SourceUrl",
                table: "documents",
                column: "SourceUrl");

            migrationBuilder.CreateIndex(
                name: "IX_documents_Status",
                table: "documents",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_entities_CanonicalName_EntityType",
                table: "entities",
                columns: new[] { "CanonicalName", "EntityType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_entities_EntityType",
                table: "entities",
                column: "EntityType");

            migrationBuilder.CreateIndex(
                name: "IX_entity_embeddings_EntityId",
                table: "entity_embeddings",
                column: "EntityId");

            migrationBuilder.CreateIndex(
                name: "IX_entity_embeddings_EntityId_Name",
                table: "entity_embeddings",
                columns: new[] { "EntityId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_entity_relationships_RelationshipType",
                table: "entity_relationships",
                column: "RelationshipType");

            migrationBuilder.CreateIndex(
                name: "IX_entity_relationships_SourceEntityId",
                table: "entity_relationships",
                column: "SourceEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_entity_relationships_SourceEntityId_RelationshipType",
                table: "entity_relationships",
                columns: new[] { "SourceEntityId", "RelationshipType" });

            migrationBuilder.CreateIndex(
                name: "IX_entity_relationships_SourceEntityId_TargetEntityId_Relation~",
                table: "entity_relationships",
                columns: new[] { "SourceEntityId", "TargetEntityId", "RelationshipType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_entity_relationships_TargetEntityId",
                table: "entity_relationships",
                column: "TargetEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_evidence_artifacts_ArtifactType",
                table: "evidence_artifacts",
                column: "ArtifactType");

            migrationBuilder.CreateIndex(
                name: "IX_evidence_artifacts_ContentHash",
                table: "evidence_artifacts",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_evidence_artifacts_EntityId",
                table: "evidence_artifacts",
                column: "EntityId");

            migrationBuilder.CreateIndex(
                name: "IX_evidence_artifacts_EntityId_ArtifactType",
                table: "evidence_artifacts",
                columns: new[] { "EntityId", "ArtifactType" });

            migrationBuilder.CreateIndex(
                name: "IX_evidence_artifacts_Metadata",
                table: "evidence_artifacts",
                column: "Metadata")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_evidence_artifacts_SegmentHash",
                table: "evidence_artifacts",
                column: "SegmentHash");

            migrationBuilder.CreateIndex(
                name: "IX_feature_embeddings_CollectionId_FeatureType",
                table: "feature_embeddings",
                columns: new[] { "CollectionId", "FeatureType" });

            migrationBuilder.CreateIndex(
                name: "IX_feature_embeddings_CollectionId_NormalizedText_FeatureType",
                table: "feature_embeddings",
                columns: new[] { "CollectionId", "NormalizedText", "FeatureType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_feature_embeddings_UpdatedAt",
                table: "feature_embeddings",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_folders_CollectionId",
                table: "folders",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_folders_CollectionId_ParentFolderId_Name",
                table: "folders",
                columns: new[] { "CollectionId", "ParentFolderId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_folders_ParentFolderId",
                table: "folders",
                column: "ParentFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_jobs_CreatedAt",
                table: "ingestion_jobs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_jobs_SourceId",
                table: "ingestion_jobs",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_jobs_Status",
                table: "ingestion_jobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_sources_CollectionId",
                table: "ingestion_sources",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_sources_IsEnabled",
                table: "ingestion_sources",
                column: "IsEnabled");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_sources_Name",
                table: "ingestion_sources",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_ingestion_sources_SourceType",
                table: "ingestion_sources",
                column: "SourceType");

            migrationBuilder.CreateIndex(
                name: "IX_messaging_context_states_CollectionId",
                table: "messaging_context_states",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_messaging_context_states_ConversationId",
                table: "messaging_context_states",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_messaging_context_states_TenantId_Platform_WorkspaceId_Scop~",
                table: "messaging_context_states",
                columns: new[] { "TenantId", "Platform", "WorkspaceId", "ScopeType", "ScopeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_messaging_interaction_feedback_CreatedAt",
                table: "messaging_interaction_feedback",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_messaging_interaction_feedback_QueryHash_CreatedAt",
                table: "messaging_interaction_feedback",
                columns: new[] { "QueryHash", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_messaging_interaction_feedback_TenantId_Platform_WorkspaceI~",
                table: "messaging_interaction_feedback",
                columns: new[] { "TenantId", "Platform", "WorkspaceId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_messaging_tenant_configs_Platform_WorkspaceId",
                table: "messaging_tenant_configs",
                columns: new[] { "Platform", "WorkspaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_messaging_tenant_configs_TenantId_Platform",
                table: "messaging_tenant_configs",
                columns: new[] { "TenantId", "Platform" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_processing_signals_CollectionId",
                table: "processing_signals",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_processing_signals_CollectionId_SignalType_CreatedAt",
                table: "processing_signals",
                columns: new[] { "CollectionId", "SignalType", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_processing_signals_CorrelationId",
                table: "processing_signals",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_processing_signals_CreatedAt",
                table: "processing_signals",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_processing_signals_DocumentId",
                table: "processing_signals",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_processing_signals_SignalType",
                table: "processing_signals",
                column: "SignalType");

            migrationBuilder.CreateIndex(
                name: "IX_retrieval_entities_CollectionId",
                table: "retrieval_entities",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_retrieval_entities_CollectionId_ContentType",
                table: "retrieval_entities",
                columns: new[] { "CollectionId", "ContentType" });

            migrationBuilder.CreateIndex(
                name: "IX_retrieval_entities_ContentHash",
                table: "retrieval_entities",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_retrieval_entities_ContentType",
                table: "retrieval_entities",
                column: "ContentType");

            migrationBuilder.CreateIndex(
                name: "IX_retrieval_entities_CreatedAt",
                table: "retrieval_entities",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_retrieval_entities_NeedsReview",
                table: "retrieval_entities",
                column: "NeedsReview");

            migrationBuilder.CreateIndex(
                name: "IX_saas_query_logs_ApiKeyId_CreatedAt",
                table: "saas_query_logs",
                columns: new[] { "ApiKeyId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_saas_query_logs_ApiKeyId_Success_CreatedAt",
                table: "saas_query_logs",
                columns: new[] { "ApiKeyId", "Success", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_saas_query_logs_CountryCode",
                table: "saas_query_logs",
                column: "CountryCode");

            migrationBuilder.CreateIndex(
                name: "IX_saas_query_logs_CreatedAt",
                table: "saas_query_logs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_saas_query_logs_QueryType",
                table: "saas_query_logs",
                column: "QueryType");

            migrationBuilder.CreateIndex(
                name: "IX_saas_usage_rollups_ApiKeyId_Date",
                table: "saas_usage_rollups",
                columns: new[] { "ApiKeyId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_scanned_page_groups_CollectionId",
                table: "scanned_page_groups",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_scanned_page_groups_GroupingStrategy",
                table: "scanned_page_groups",
                column: "GroupingStrategy");

            migrationBuilder.CreateIndex(
                name: "IX_scanned_page_memberships_EntityId",
                table: "scanned_page_memberships",
                column: "EntityId");

            migrationBuilder.CreateIndex(
                name: "IX_segment_links_DocumentId",
                table: "segment_links",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_segment_links_SourceSegmentHash",
                table: "segment_links",
                column: "SourceSegmentHash");

            migrationBuilder.CreateIndex(
                name: "IX_segment_links_SourceSegmentHash_TargetSegmentHash",
                table: "segment_links",
                columns: new[] { "SourceSegmentHash", "TargetSegmentHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_segment_links_TargetSegmentHash",
                table: "segment_links",
                column: "TargetSegmentHash");

            migrationBuilder.CreateIndex(
                name: "IX_widget_configs_ApiKeyId",
                table: "widget_configs",
                column: "ApiKeyId",
                unique: true);

            // Full-text search: tsvector generated column + GIN index on evidence text
            migrationBuilder.Sql("""
                ALTER TABLE evidence_artifacts
                    ADD COLUMN IF NOT EXISTS search_vector tsvector
                    GENERATED ALWAYS AS (to_tsvector('english', COALESCE("Content", ''))) STORED;

                CREATE INDEX IF NOT EXISTS "IX_evidence_artifacts_search_vector"
                    ON evidence_artifacts USING gin (search_vector);
                """);

            // HNSW index for pgvector cosine similarity search on feature embeddings
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_feature_embeddings_embedding_hnsw"
                    ON feature_embeddings USING hnsw ("Embedding" vector_cosine_ops);
                """);

            // ── Graph functions ──────────────────────────────────────────────

            // Function: N-hop entity traversal from a starting node
            // Return columns match GraphWalkResult entity (PascalCase quoted)
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION graph_walk(
                    start_entity_id UUID,
                    max_depth INT DEFAULT 3,
                    max_results INT DEFAULT 100
                )
                RETURNS TABLE (
                    "EntityId" UUID,
                    "CanonicalName" TEXT,
                    "EntityType" TEXT,
                    "Depth" INT,
                    "RelationshipType" TEXT
                ) AS $$
                BEGIN
                    RETURN QUERY
                    WITH RECURSIVE walk AS (
                        SELECT
                            e."Id" AS eid,
                            e."CanonicalName"::TEXT AS cname,
                            e."EntityType"::TEXT AS etype,
                            0 AS d,
                            ARRAY[e."Id"] AS path,
                            'start'::TEXT AS rel
                        FROM entities e
                        WHERE e."Id" = start_entity_id

                        UNION ALL

                        SELECT
                            next_e."Id",
                            next_e."CanonicalName"::TEXT,
                            next_e."EntityType"::TEXT,
                            w.d + 1,
                            w.path || next_e."Id",
                            er."RelationshipType"::TEXT
                        FROM walk w
                        JOIN entity_relationships er
                            ON er."SourceEntityId" = w.eid
                            OR er."TargetEntityId" = w.eid
                        JOIN entities next_e
                            ON next_e."Id" = CASE
                                WHEN er."SourceEntityId" = w.eid THEN er."TargetEntityId"
                                ELSE er."SourceEntityId"
                            END
                        WHERE w.d < max_depth
                          AND NOT next_e."Id" = ANY(w.path)
                    )
                    SELECT DISTINCT ON (walk.eid)
                        walk.eid, walk.cname, walk.etype,
                        walk.d, walk.rel
                    FROM walk
                    ORDER BY walk.eid, walk.d
                    LIMIT max_results;
                END;
                $$ LANGUAGE plpgsql STABLE;
                """);

            // Function: Find documents related to a given document through shared entities
            // Return columns match DocumentSimilarityResult entity (PascalCase quoted)
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION document_similarity(
                    source_doc_id UUID,
                    result_limit INT DEFAULT 20
                )
                RETURNS TABLE (
                    "DocumentId" UUID,
                    "DocumentName" TEXT,
                    "SharedEntityCount" BIGINT,
                    "SharedEntityNames" TEXT
                ) AS $$
                BEGIN
                    RETURN QUERY
                    WITH source_entities AS (
                        SELECT de."EntityId" AS entity_id, de."MentionCount", e."CanonicalName"
                        FROM document_entities de
                        JOIN entities e ON e."Id" = de."EntityId"
                        WHERE de."DocumentId" = source_doc_id
                    )
                    SELECT
                        de2."DocumentId",
                        d."Name"::TEXT,
                        COUNT(DISTINCT de2."EntityId")::BIGINT,
                        STRING_AGG(DISTINCT se."CanonicalName"::TEXT, ', ' ORDER BY se."CanonicalName"::TEXT)
                            FILTER (WHERE se."CanonicalName" IS NOT NULL)
                    FROM source_entities se
                    JOIN document_entities de2 ON de2."EntityId" = se.entity_id
                    JOIN documents d ON d."Id" = de2."DocumentId"
                    WHERE de2."DocumentId" != source_doc_id
                    GROUP BY de2."DocumentId", d."Name"
                    ORDER BY COUNT(DISTINCT de2."EntityId") DESC
                    LIMIT result_limit;
                END;
                $$ LANGUAGE plpgsql STABLE;
                """);

            // Function: Shortest path between two entities (BFS in SQL)
            // Returns ALL steps in the shortest path, not just the destination.
            // Return columns match ShortestPathStep entity (PascalCase quoted)
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION find_shortest_path(
                    from_id UUID,
                    to_id UUID,
                    max_depth INT DEFAULT 5
                )
                RETURNS TABLE (
                    "EntityId" UUID,
                    "EntityName" TEXT,
                    "RelationshipType" TEXT,
                    "Depth" INT
                ) AS $$
                BEGIN
                    RETURN QUERY
                    WITH RECURSIVE bfs AS (
                        SELECT
                            e."Id" AS eid,
                            e."CanonicalName"::TEXT AS ename,
                            'start'::TEXT AS rel_type,
                            0 AS d,
                            ARRAY[e."Id"] AS path,
                            ARRAY['start'::TEXT] AS rel_path
                        FROM entities e
                        WHERE e."Id" = from_id

                        UNION ALL

                        SELECT
                            next_e."Id",
                            next_e."CanonicalName"::TEXT,
                            er."RelationshipType"::TEXT,
                            b.d + 1,
                            b.path || next_e."Id",
                            b.rel_path || er."RelationshipType"::TEXT
                        FROM bfs b
                        JOIN entity_relationships er
                            ON er."SourceEntityId" = b.eid
                            OR er."TargetEntityId" = b.eid
                        JOIN entities next_e
                            ON next_e."Id" = CASE
                                WHEN er."SourceEntityId" = b.eid THEN er."TargetEntityId"
                                ELSE er."SourceEntityId"
                            END
                        WHERE b.d < max_depth
                          AND NOT next_e."Id" = ANY(b.path)
                    ),
                    shortest AS (
                        SELECT bfs.path, bfs.rel_path
                        FROM bfs
                        WHERE bfs.eid = to_id
                        ORDER BY bfs.d
                        LIMIT 1
                    )
                    SELECT
                        p.node_id,
                        e."CanonicalName"::TEXT,
                        p.rel,
                        (p.ord - 1)::INT
                    FROM shortest s,
                         LATERAL unnest(s.path, s.rel_path) WITH ORDINALITY AS p(node_id, rel, ord)
                    JOIN entities e ON e."Id" = p.node_id
                    ORDER BY p.ord;
                END;
                $$ LANGUAGE plpgsql STABLE;
                """);

            // ── Materialized views ───────────────────────────────────────────

            // Precomputed document-to-document relationships via shared entities
            // Columns match DocumentGraphEdge entity: SourceDocumentId, TargetDocumentId, SharedEntityCount (BIGINT), SharedEntityNames (TEXT)
            migrationBuilder.Sql("""
                CREATE MATERIALIZED VIEW IF NOT EXISTS mv_document_graph AS
                SELECT
                    de1."DocumentId" AS "SourceDocumentId",
                    de2."DocumentId" AS "TargetDocumentId",
                    COUNT(DISTINCT de1."EntityId")::BIGINT AS "SharedEntityCount",
                    STRING_AGG(DISTINCT e."CanonicalName"::TEXT, ', ' ORDER BY e."CanonicalName"::TEXT)
                        FILTER (WHERE e."CanonicalName" IS NOT NULL) AS "SharedEntityNames"
                FROM document_entities de1
                JOIN document_entities de2 ON de1."EntityId" = de2."EntityId"
                    AND de1."DocumentId" < de2."DocumentId"
                JOIN entities e ON e."Id" = de1."EntityId"
                GROUP BY de1."DocumentId", de2."DocumentId"
                HAVING COUNT(DISTINCT de1."EntityId") >= 2;

                CREATE INDEX IF NOT EXISTS "IX_mv_doc_graph_source"
                    ON mv_document_graph ("SourceDocumentId");
                CREATE INDEX IF NOT EXISTS "IX_mv_doc_graph_target"
                    ON mv_document_graph ("TargetDocumentId");
                """);

            // Precomputed entity importance (degree centrality)
            // Columns match EntityCentrality entity: EntityId, CanonicalName, EntityType, OutDegree, InDegree, TotalDegree, DocumentCount (all BIGINT)
            migrationBuilder.Sql("""
                CREATE MATERIALIZED VIEW IF NOT EXISTS mv_entity_centrality AS
                SELECT
                    e."Id" AS "EntityId",
                    e."CanonicalName"::TEXT AS "CanonicalName",
                    e."EntityType"::TEXT AS "EntityType",
                    COUNT(DISTINCT er_out."Id")::BIGINT AS "OutDegree",
                    COUNT(DISTINCT er_in."Id")::BIGINT AS "InDegree",
                    (COUNT(DISTINCT er_out."Id") + COUNT(DISTINCT er_in."Id"))::BIGINT AS "TotalDegree",
                    COUNT(DISTINCT de."DocumentId")::BIGINT AS "DocumentCount"
                FROM entities e
                LEFT JOIN entity_relationships er_out ON er_out."SourceEntityId" = e."Id"
                LEFT JOIN entity_relationships er_in ON er_in."TargetEntityId" = e."Id"
                LEFT JOIN document_entities de ON de."EntityId" = e."Id"
                GROUP BY e."Id", e."CanonicalName", e."EntityType";

                CREATE UNIQUE INDEX IF NOT EXISTS "IX_mv_centrality_id"
                    ON mv_entity_centrality ("EntityId");
                CREATE INDEX IF NOT EXISTS "IX_mv_centrality_degree"
                    ON mv_entity_centrality ("TotalDegree" DESC);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop materialized views and functions before tables they depend on
            migrationBuilder.Sql("DROP MATERIALIZED VIEW IF EXISTS mv_entity_centrality;");
            migrationBuilder.Sql("DROP MATERIALIZED VIEW IF EXISTS mv_document_graph;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS find_shortest_path(UUID, UUID, INT);");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS document_similarity(UUID, INT);");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS graph_walk(UUID, INT);");

            migrationBuilder.DropTable(
                name: "api_key_collection_links");

            migrationBuilder.DropTable(
                name: "api_key_indexing_sources");

            migrationBuilder.DropTable(
                name: "api_key_read_domains");

            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "collection_salient_terms");

            migrationBuilder.DropTable(
                name: "community_memberships");

            migrationBuilder.DropTable(
                name: "conversation_messages");

            migrationBuilder.DropTable(
                name: "custom_domains");

            migrationBuilder.DropTable(
                name: "document_entities");

            migrationBuilder.DropTable(
                name: "entity_embeddings");

            migrationBuilder.DropTable(
                name: "entity_relationships");

            migrationBuilder.DropTable(
                name: "evidence_artifacts");

            migrationBuilder.DropTable(
                name: "feature_embeddings");

            migrationBuilder.DropTable(
                name: "ingestion_jobs");

            migrationBuilder.DropTable(
                name: "messaging_context_states");

            migrationBuilder.DropTable(
                name: "messaging_interaction_feedback");

            migrationBuilder.DropTable(
                name: "messaging_tenant_configs");

            migrationBuilder.DropTable(
                name: "processing_signals");

            migrationBuilder.DropTable(
                name: "saas_query_logs");

            migrationBuilder.DropTable(
                name: "saas_usage_rollups");

            migrationBuilder.DropTable(
                name: "scanned_page_memberships");

            migrationBuilder.DropTable(
                name: "segment_links");

            migrationBuilder.DropTable(
                name: "widget_configs");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "communities");

            migrationBuilder.DropTable(
                name: "conversations");

            migrationBuilder.DropTable(
                name: "entities");

            migrationBuilder.DropTable(
                name: "ingestion_sources");

            migrationBuilder.DropTable(
                name: "retrieval_entities");

            migrationBuilder.DropTable(
                name: "scanned_page_groups");

            migrationBuilder.DropTable(
                name: "documents");

            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropTable(
                name: "folders");

            migrationBuilder.DropTable(
                name: "collections");
        }
    }
}
