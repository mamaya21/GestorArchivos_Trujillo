using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GestorArchivos.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueActiveFileLock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BloqueosArchivos_ArchivoId",
                table: "BloqueosArchivos");

            migrationBuilder.CreateIndex(
                name: "IX_BloqueosArchivos_ArchivoId",
                table: "BloqueosArchivos",
                column: "ArchivoId",
                unique: true,
                filter: "[Activo] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BloqueosArchivos_ArchivoId",
                table: "BloqueosArchivos");

            migrationBuilder.CreateIndex(
                name: "IX_BloqueosArchivos_ArchivoId",
                table: "BloqueosArchivos",
                column: "ArchivoId");
        }
    }
}
