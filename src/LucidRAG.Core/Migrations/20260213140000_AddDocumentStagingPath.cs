using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LucidRAG.Migrations;

/// <summary>
///     Adds StagingPath column to documents table (was already on processing_signals but missing from documents).
/// </summary>
public partial class AddDocumentStagingPath : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "StagingPath",
            table: "documents",
            type: "character varying(2048)",
            maxLength: 2048,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "StagingPath",
            table: "documents");
    }
}
