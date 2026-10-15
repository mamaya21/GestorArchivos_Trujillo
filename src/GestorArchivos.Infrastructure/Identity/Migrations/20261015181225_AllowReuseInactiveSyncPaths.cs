using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GestorArchivos.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AllowReuseInactiveSyncPaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ElementosSync_DispositivoId_RutaLocal",
                table: "ElementosSync");

            migrationBuilder.CreateIndex(
                name: "IX_ElementosSync_DispositivoId_RutaLocal",
                table: "ElementosSync",
                columns: new[] { "DispositivoId", "RutaLocal" },
                unique: true,
                filter: "[Activo] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ElementosSync_DispositivoId_RutaLocal",
                table: "ElementosSync");

            migrationBuilder.CreateIndex(
                name: "IX_ElementosSync_DispositivoId_RutaLocal",
                table: "ElementosSync",
                columns: new[] { "DispositivoId", "RutaLocal" },
                unique: true);
        }
    }
}
