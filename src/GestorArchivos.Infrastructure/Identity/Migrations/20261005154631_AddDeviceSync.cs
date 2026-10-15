using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GestorArchivos.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DispositivosSync",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Nombre = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    SistemaOperativo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CarpetaRaizId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Activo = table.Column<bool>(type: "bit", nullable: false),
                    FechaRegistro = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UltimaConexionUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DispositivosSync", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DispositivosSync_Carpetas_CarpetaRaizId",
                        column: x => x.CarpetaRaizId,
                        principalTable: "Carpetas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BloqueosArchivos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArchivoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DispositivoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FechaBloqueoUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiraUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Activo = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BloqueosArchivos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BloqueosArchivos_Archivos_ArchivoId",
                        column: x => x.ArchivoId,
                        principalTable: "Archivos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BloqueosArchivos_DispositivosSync_DispositivoId",
                        column: x => x.DispositivoId,
                        principalTable: "DispositivosSync",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CredencialesDispositivosSync",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DispositivoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FechaCreacionUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UltimoUsoUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FechaRevocacionUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Revocada = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CredencialesDispositivosSync", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CredencialesDispositivosSync_DispositivosSync_DispositivoId",
                        column: x => x.DispositivoId,
                        principalTable: "DispositivosSync",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ElementosSync",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DispositivoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RutaLocal = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    CarpetaRemotaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Activo = table.Column<bool>(type: "bit", nullable: false),
                    FechaCreacion = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UltimaSincronizacionUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ElementosSync", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ElementosSync_Carpetas_CarpetaRemotaId",
                        column: x => x.CarpetaRemotaId,
                        principalTable: "Carpetas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ElementosSync_DispositivosSync_DispositivoId",
                        column: x => x.DispositivoId,
                        principalTable: "DispositivosSync",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BloqueosArchivos_ArchivoId",
                table: "BloqueosArchivos",
                column: "ArchivoId");

            migrationBuilder.CreateIndex(
                name: "IX_BloqueosArchivos_DispositivoId",
                table: "BloqueosArchivos",
                column: "DispositivoId");

            migrationBuilder.CreateIndex(
                name: "IX_BloqueosArchivos_UsuarioId",
                table: "BloqueosArchivos",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_CredencialesDispositivosSync_DispositivoId",
                table: "CredencialesDispositivosSync",
                column: "DispositivoId");

            migrationBuilder.CreateIndex(
                name: "IX_CredencialesDispositivosSync_TokenHash",
                table: "CredencialesDispositivosSync",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DispositivosSync_CarpetaRaizId",
                table: "DispositivosSync",
                column: "CarpetaRaizId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DispositivosSync_UsuarioId",
                table: "DispositivosSync",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_ElementosSync_CarpetaRemotaId",
                table: "ElementosSync",
                column: "CarpetaRemotaId");

            migrationBuilder.CreateIndex(
                name: "IX_ElementosSync_DispositivoId",
                table: "ElementosSync",
                column: "DispositivoId");

            migrationBuilder.CreateIndex(
                name: "IX_ElementosSync_DispositivoId_RutaLocal",
                table: "ElementosSync",
                columns: new[] { "DispositivoId", "RutaLocal" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BloqueosArchivos");

            migrationBuilder.DropTable(
                name: "CredencialesDispositivosSync");

            migrationBuilder.DropTable(
                name: "ElementosSync");

            migrationBuilder.DropTable(
                name: "DispositivosSync");
        }
    }
}
