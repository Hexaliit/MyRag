using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LucidRAG.Migrations
{
    /// <inheritdoc />
    public partial class AddPerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Document date-range filters + StagingCleanupService cutoff queries
            migrationBuilder.CreateIndex(
                name: "IX_documents_CreatedAt",
                table: "documents",
                column: "CreatedAt");

            // Admin pattern: list docs in collection sorted by date
            migrationBuilder.CreateIndex(
                name: "IX_documents_CollectionId_CreatedAt",
                table: "documents",
                columns: new[] { "CollectionId", "CreatedAt" });

            // ConversationService: messages loaded with OrderBy(CreatedAt) filtered by ConversationId
            migrationBuilder.CreateIndex(
                name: "IX_conversation_messages_ConversationId_CreatedAt",
                table: "conversation_messages",
                columns: new[] { "ConversationId", "CreatedAt" });

            // Backward graph traversal (GraphQL GetEntityConnections)
            migrationBuilder.CreateIndex(
                name: "IX_entity_relationships_TargetEntityId_RelationshipType",
                table: "entity_relationships",
                columns: new[] { "TargetEntityId", "RelationshipType" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_documents_CreatedAt",
                table: "documents");

            migrationBuilder.DropIndex(
                name: "IX_documents_CollectionId_CreatedAt",
                table: "documents");

            migrationBuilder.DropIndex(
                name: "IX_conversation_messages_ConversationId_CreatedAt",
                table: "conversation_messages");

            migrationBuilder.DropIndex(
                name: "IX_entity_relationships_TargetEntityId_RelationshipType",
                table: "entity_relationships");
        }
    }
}
