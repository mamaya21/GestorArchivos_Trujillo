using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GestorArchivos.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddArchivoFisicoPendienteEliminacion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArchivosFisicosPendientesEliminacion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RutaFisica = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    FechaRegistroUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EliminarDespuesUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Intentos = table.Column<int>(type: "int", nullable: false),
                    UltimoError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchivosFisicosPendientesEliminacion", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArchivosFisicosPendientesEliminacion_EliminarDespuesUtc",
                table: "ArchivosFisicosPendientesEliminacion",
                column: "EliminarDespuesUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ArchivosFisicosPendientesEliminacion_RutaFisica",
                table: "ArchivosFisicosPendientesEliminacion",
                column: "RutaFisica",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArchivosFisicosPendientesEliminacion");
        }
    }
}
