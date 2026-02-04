#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace LucidRAG.Migrations.Tenant;

/// <inheritdoc />
public partial class InitialTenants : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            "public");

        migrationBuilder.CreateTable(
            "tenants",
            schema: "public",
            columns: table => new
            {
                Id = table.Column<Guid>("uuid", nullable: false),
                TenantId = table.Column<string>("character varying(64)", maxLength: 64, nullable: false),
                SchemaName = table.Column<string>("character varying(128)", maxLength: 128, nullable: false),
                QdrantCollection = table.Column<string>("character varying(128)", maxLength: 128, nullable: false),
                DisplayName = table.Column<string>("character varying(256)", maxLength: 256, nullable: true),
                ContactEmail = table.Column<string>("character varying(256)", maxLength: 256, nullable: true),
                IsActive = table.Column<bool>("boolean", nullable: false),
                IsProvisioned = table.Column<bool>("boolean", nullable: false),
                Settings = table.Column<string>("text", nullable: true),
                Plan = table.Column<string>("character varying(32)", maxLength: 32, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>("timestamp with time zone", nullable: false),
                ProvisionedAt = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true),
                LastAccessedAt = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true)
            },
            constraints: table => { table.PrimaryKey("PK_tenants", x => x.Id); });

        migrationBuilder.CreateIndex(
            "IX_tenants_IsActive",
            schema: "public",
            table: "tenants",
            column: "IsActive");

        migrationBuilder.CreateIndex(
            "IX_tenants_SchemaName",
            schema: "public",
            table: "tenants",
            column: "SchemaName",
            unique: true);

        migrationBuilder.CreateIndex(
            "IX_tenants_TenantId",
            schema: "public",
            table: "tenants",
            column: "TenantId",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            "tenants",
            "public");
    }
}