using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GestorArchivos.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class ExtendSyncItemsForFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "CarpetaRemotaId",
                table: "ElementosSync",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<Guid>(
                name: "ArchivoRemotoId",
                table: "ElementosSync",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Tipo",
                table: "ElementosSync",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_ElementosSync_ArchivoRemotoId",
                table: "ElementosSync",
                column: "ArchivoRemotoId");

            migrationBuilder.AddForeignKey(
                name: "FK_ElementosSync_Archivos_ArchivoRemotoId",
                table: "ElementosSync",
                column: "ArchivoRemotoId",
                principalTable: "Archivos",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ElementosSync_Archivos_ArchivoRemotoId",
                table: "ElementosSync");

            migrationBuilder.DropIndex(
                name: "IX_ElementosSync_ArchivoRemotoId",
                table: "ElementosSync");

            migrationBuilder.DropColumn(
                name: "ArchivoRemotoId",
                table: "ElementosSync");

            migrationBuilder.DropColumn(
                name: "Tipo",
                table: "ElementosSync");

            migrationBuilder.AlterColumn<Guid>(
                name: "CarpetaRemotaId",
                table: "ElementosSync",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
