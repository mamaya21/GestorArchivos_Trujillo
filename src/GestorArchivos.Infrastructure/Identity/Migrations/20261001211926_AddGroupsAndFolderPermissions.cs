using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GestorArchivos.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupsAndFolderPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Grupos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Nombre = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Descripcion = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Activo = table.Column<bool>(type: "bit", nullable: false),
                    FechaCreacion = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Grupos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PermisosCarpetasUsuarios",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CarpetaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Permisos = table.Column<int>(type: "int", nullable: false),
                    HeredarSubcarpetas = table.Column<bool>(type: "bit", nullable: false),
                    FechaCreacion = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermisosCarpetasUsuarios", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PermisosCarpetasUsuarios_Carpetas_CarpetaId",
                        column: x => x.CarpetaId,
                        principalTable: "Carpetas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GruposUsuarios",
                columns: table => new
                {
                    GrupoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FechaAsignacion = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GruposUsuarios", x => new { x.GrupoId, x.UsuarioId });
                    table.ForeignKey(
                        name: "FK_GruposUsuarios_Grupos_GrupoId",
                        column: x => x.GrupoId,
                        principalTable: "Grupos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PermisosCarpetasGrupos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CarpetaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GrupoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Permisos = table.Column<int>(type: "int", nullable: false),
                    HeredarSubcarpetas = table.Column<bool>(type: "bit", nullable: false),
                    FechaCreacion = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermisosCarpetasGrupos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PermisosCarpetasGrupos_Carpetas_CarpetaId",
                        column: x => x.CarpetaId,
                        principalTable: "Carpetas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PermisosCarpetasGrupos_Grupos_GrupoId",
                        column: x => x.GrupoId,
                        principalTable: "Grupos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Grupos_Nombre",
                table: "Grupos",
                column: "Nombre",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GruposUsuarios_UsuarioId",
                table: "GruposUsuarios",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_PermisosCarpetasGrupos_CarpetaId_GrupoId",
                table: "PermisosCarpetasGrupos",
                columns: new[] { "CarpetaId", "GrupoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PermisosCarpetasGrupos_GrupoId",
                table: "PermisosCarpetasGrupos",
                column: "GrupoId");

            migrationBuilder.CreateIndex(
                name: "IX_PermisosCarpetasUsuarios_CarpetaId_UsuarioId",
                table: "PermisosCarpetasUsuarios",
                columns: new[] { "CarpetaId", "UsuarioId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PermisosCarpetasUsuarios_UsuarioId",
                table: "PermisosCarpetasUsuarios",
                column: "UsuarioId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GruposUsuarios");

            migrationBuilder.DropTable(
                name: "PermisosCarpetasGrupos");

            migrationBuilder.DropTable(
                name: "PermisosCarpetasUsuarios");

            migrationBuilder.DropTable(
                name: "Grupos");
        }
    }
}
