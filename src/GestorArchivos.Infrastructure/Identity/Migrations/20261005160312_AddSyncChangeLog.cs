using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GestorArchivos.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncChangeLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CambiosSync",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ArchivoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CarpetaId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DispositivoOrigenId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TipoCambio = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    VersionArchivo = table.Column<long>(type: "bigint", nullable: true),
                    FechaUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CambiosSync", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CambiosSync_ArchivoId",
                table: "CambiosSync",
                column: "ArchivoId");

            migrationBuilder.CreateIndex(
                name: "IX_CambiosSync_CarpetaId",
                table: "CambiosSync",
                column: "CarpetaId");

            migrationBuilder.CreateIndex(
                name: "IX_CambiosSync_DispositivoOrigenId",
                table: "CambiosSync",
                column: "DispositivoOrigenId");

            migrationBuilder.CreateIndex(
                name: "IX_CambiosSync_FechaUtc",
                table: "CambiosSync",
                column: "FechaUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CambiosSync");
        }
    }
}
