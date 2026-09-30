using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Acentra.Infrastructure.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddTenantFileIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_tenant_files_tenant_uploaded",
                table: "TenantFiles",
                columns: new[] { "TenantId", "UploadedUtc" });

            migrationBuilder.CreateIndex(
                name: "ux_tenant_files_tenant_key",
                table: "TenantFiles",
                columns: new[] { "TenantId", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_tenant_files_tenant_uploaded",
                table: "TenantFiles");

            migrationBuilder.DropIndex(
                name: "ux_tenant_files_tenant_key",
                table: "TenantFiles");
        }
    }
}
