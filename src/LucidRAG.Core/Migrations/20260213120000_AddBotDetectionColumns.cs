using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LucidRAG.Migrations
{
    /// <inheritdoc />
    public partial class AddBotDetectionColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "BotConfidence",
                table: "saas_query_logs",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BotDetected",
                table: "saas_query_logs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "BotType",
                table: "saas_query_logs",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientIpHash",
                table: "saas_query_logs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_saas_query_logs_BotDetected_CreatedAt",
                table: "saas_query_logs",
                columns: new[] { "BotDetected", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_saas_query_logs_BotDetected_CreatedAt",
                table: "saas_query_logs");

            migrationBuilder.DropColumn(
                name: "BotConfidence",
                table: "saas_query_logs");

            migrationBuilder.DropColumn(
                name: "BotDetected",
                table: "saas_query_logs");

            migrationBuilder.DropColumn(
                name: "BotType",
                table: "saas_query_logs");

            migrationBuilder.DropColumn(
                name: "ClientIpHash",
                table: "saas_query_logs");
        }
    }
}
