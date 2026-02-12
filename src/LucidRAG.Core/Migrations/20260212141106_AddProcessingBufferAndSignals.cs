using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace LucidRAG.Migrations
{
    /// <inheritdoc />
    public partial class AddProcessingBufferAndSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_communities_collections_CollectionId",
                table: "communities");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.AddColumn<string>(
                name: "StagingPath",
                table: "documents",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActiveDocumentIds",
                table: "conversations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastTopicQuery",
                table: "conversations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TopicSignature",
                table: "conversations",
                type: "text",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "CollectionId",
                table: "communities",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<bool>(
                name: "HasCompletedOnboarding",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    KeyPrefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    KeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    NormalizedOwnerEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    Plan = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
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
                    SigningSecret = table.Column<string>(type: "text", nullable: true),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Slug = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
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
                name: "processing_signals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SignalType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    StagingPath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_processing_signals", x => x.Id);
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
                    SourceType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SourceValue = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    MaxDocuments = table.Column<int>(type: "integer", nullable: false),
                    DocumentCount = table.Column<int>(type: "integer", nullable: false),
                    LastCrawledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CrawlStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextScheduledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TriggerCrawlNow = table.Column<bool>(type: "boolean", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    ETag = table.Column<string>(type: "text", nullable: true),
                    LastModifiedHeader = table.Column<string>(type: "text", nullable: true)
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
                    Domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
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
                    Domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    IsVerified = table.Column<bool>(type: "boolean", nullable: false),
                    VerificationToken = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
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
                    Theme = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    AccentColor = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    BorderRadius = table.Column<int>(type: "integer", nullable: false),
                    FontFamily = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CustomCss = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    LogoUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ShowBranding = table.Column<bool>(type: "boolean", nullable: false),
                    Position = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Mode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Placeholder = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MaxResults = table.Column<int>(type: "integer", nullable: false),
                    CorpusStyle = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    PageTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PageDescription = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FaviconUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    WelcomeMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
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

            migrationBuilder.CreateIndex(
                name: "ix_documents_source_path_collection",
                table: "documents",
                columns: new[] { "SourcePath", "CollectionId" },
                unique: true,
                filter: "source_path IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_api_key_collection_links_ApiKeyId_CollectionId",
                table: "api_key_collection_links",
                columns: new[] { "ApiKeyId", "CollectionId" },
                unique: true);

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
                name: "IX_api_key_read_domains_ApiKeyId_Domain",
                table: "api_key_read_domains",
                columns: new[] { "ApiKeyId", "Domain" },
                unique: true);

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
                column: "KeyPrefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_NormalizedOwnerEmail",
                table: "api_keys",
                column: "NormalizedOwnerEmail");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_Slug",
                table: "api_keys",
                column: "Slug",
                unique: true,
                filter: "\"Slug\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_UserId",
                table: "api_keys",
                column: "UserId");

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
                name: "IX_processing_signals_CollectionId",
                table: "processing_signals",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_processing_signals_CorrelationId",
                table: "processing_signals",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_processing_signals_CreatedAt",
                table: "processing_signals",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_processing_signals_DocumentId_CreatedAt",
                table: "processing_signals",
                columns: new[] { "DocumentId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_processing_signals_SignalType_CreatedAt",
                table: "processing_signals",
                columns: new[] { "SignalType", "CreatedAt" });

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

            migrationBuilder.AddForeignKey(
                name: "FK_communities_collections_CollectionId",
                table: "communities",
                column: "CollectionId",
                principalTable: "collections",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_communities_collections_CollectionId",
                table: "communities");

            migrationBuilder.DropTable(
                name: "api_key_collection_links");

            migrationBuilder.DropTable(
                name: "api_key_indexing_sources");

            migrationBuilder.DropTable(
                name: "api_key_read_domains");

            migrationBuilder.DropTable(
                name: "custom_domains");

            migrationBuilder.DropTable(
                name: "feature_embeddings");

            migrationBuilder.DropTable(
                name: "processing_signals");

            migrationBuilder.DropTable(
                name: "saas_query_logs");

            migrationBuilder.DropTable(
                name: "saas_usage_rollups");

            migrationBuilder.DropTable(
                name: "segment_links");

            migrationBuilder.DropTable(
                name: "widget_configs");

            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropIndex(
                name: "ix_documents_source_path_collection",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "StagingPath",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "ActiveDocumentIds",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "LastTopicQuery",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "TopicSignature",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "HasCompletedOnboarding",
                table: "AspNetUsers");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.AlterColumn<Guid>(
                name: "CollectionId",
                table: "communities",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_communities_collections_CollectionId",
                table: "communities",
                column: "CollectionId",
                principalTable: "collections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
