using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GestorArchivos.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Carpetas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Nombre = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    CarpetaPadreId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FechaCreacion = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FechaModificacion = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EnPapelera = table.Column<bool>(type: "bit", nullable: false),
                    FechaEliminacion = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Carpetas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Carpetas_Carpetas_CarpetaPadreId",
                        column: x => x.CarpetaPadreId,
                        principalTable: "Carpetas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Archivos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Nombre = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    NombreFisico = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Extension = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    TipoContenido = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    TamanoBytes = table.Column<long>(type: "bigint", nullable: false),
                    HashSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CarpetaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FechaCreacion = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FechaModificacion = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Estado = table.Column<int>(type: "int", nullable: false),
                    FechaEliminacion = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Archivos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Archivos_Carpetas_CarpetaId",
                        column: x => x.CarpetaId,
                        principalTable: "Carpetas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Archivos_CarpetaId",
                table: "Archivos",
                column: "CarpetaId");

            migrationBuilder.CreateIndex(
                name: "IX_Archivos_CarpetaId_Nombre",
                table: "Archivos",
                columns: new[] { "CarpetaId", "Nombre" });

            migrationBuilder.CreateIndex(
                name: "IX_Archivos_CreadoPorUsuarioId",
                table: "Archivos",
                column: "CreadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_Archivos_HashSha256",
                table: "Archivos",
                column: "HashSha256");

            migrationBuilder.CreateIndex(
                name: "IX_Carpetas_CarpetaPadreId",
                table: "Carpetas",
                column: "CarpetaPadreId");

            migrationBuilder.CreateIndex(
                name: "IX_Carpetas_CarpetaPadreId_Nombre",
                table: "Carpetas",
                columns: new[] { "CarpetaPadreId", "Nombre" });

            migrationBuilder.CreateIndex(
                name: "IX_Carpetas_CreadoPorUsuarioId",
                table: "Carpetas",
                column: "CreadoPorUsuarioId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Archivos");

            migrationBuilder.DropTable(
                name: "Carpetas");
        }
    }
}
