using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GestorArchivos.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncChangeRootFolder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CambiosSync_FechaUtc",
                table: "CambiosSync");

            migrationBuilder.AddColumn<Guid>(
                name: "CarpetaRaizId",
                table: "CambiosSync",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_CambiosSync_CarpetaRaizId_Id",
                table: "CambiosSync",
                columns: new[] { "CarpetaRaizId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CambiosSync_CarpetaRaizId_Id",
                table: "CambiosSync");

            migrationBuilder.DropColumn(
                name: "CarpetaRaizId",
                table: "CambiosSync");

            migrationBuilder.CreateIndex(
                name: "IX_CambiosSync_FechaUtc",
                table: "CambiosSync",
                column: "FechaUtc");
        }
    }
}
