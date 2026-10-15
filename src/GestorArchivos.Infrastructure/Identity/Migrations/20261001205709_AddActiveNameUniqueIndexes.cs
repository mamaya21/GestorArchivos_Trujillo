using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GestorArchivos.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddActiveNameUniqueIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Carpetas_CarpetaPadreId_Nombre",
                table: "Carpetas");

            migrationBuilder.DropIndex(
                name: "IX_Archivos_CarpetaId_Nombre",
                table: "Archivos");

            migrationBuilder.CreateIndex(
                name: "IX_Carpetas_CarpetaPadreId_Nombre",
                table: "Carpetas",
                columns: new[] { "CarpetaPadreId", "Nombre" },
                unique: true,
                filter: "[EnPapelera] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Archivos_CarpetaId_Nombre",
                table: "Archivos",
                columns: new[] { "CarpetaId", "Nombre" },
                unique: true,
                filter: "[Estado] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Carpetas_CarpetaPadreId_Nombre",
                table: "Carpetas");

            migrationBuilder.DropIndex(
                name: "IX_Archivos_CarpetaId_Nombre",
                table: "Archivos");

            migrationBuilder.CreateIndex(
                name: "IX_Carpetas_CarpetaPadreId_Nombre",
                table: "Carpetas",
                columns: new[] { "CarpetaPadreId", "Nombre" });

            migrationBuilder.CreateIndex(
                name: "IX_Archivos_CarpetaId_Nombre",
                table: "Archivos",
                columns: new[] { "CarpetaId", "Nombre" });
        }
    }
}
