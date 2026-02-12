using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LucidRAG.Migrations;

/// <summary>
///     Adds BYOK (Bring Your Own Key) and response quality columns to api_keys.
///     - CustomLlmApiKey: encrypted user-supplied LLM API key
///     - CustomLlmProvider: provider name (anthropic, openai)
///     - PreferredResponseLength: concise | standard | detailed
/// </summary>
public partial class AddLlmConfigToApiKey : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "CustomLlmApiKey",
            table: "api_keys",
            type: "character varying(500)",
            maxLength: 500,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CustomLlmProvider",
            table: "api_keys",
            type: "character varying(50)",
            maxLength: 50,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PreferredResponseLength",
            table: "api_keys",
            type: "character varying(20)",
            maxLength: 20,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "CustomLlmApiKey", table: "api_keys");
        migrationBuilder.DropColumn(name: "CustomLlmProvider", table: "api_keys");
        migrationBuilder.DropColumn(name: "PreferredResponseLength", table: "api_keys");
    }
}
