using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GestorArchivos.Infrastructure.Identity.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261023070000_AddArchivoVersionHistory")]
public sealed class AddArchivoVersionHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ArchivoVersiones",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ArchivoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Numero = table.Column<long>(type: "bigint", nullable: false),
                NombreFisico = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                HashSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                TamanoBytes = table.Column<long>(type: "bigint", nullable: false),
                TipoContenido = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                FechaUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                UsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ArchivoVersiones", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_ArchivoVersiones_ArchivoId_Numero",
            table: "ArchivoVersiones",
            columns: new[] { "ArchivoId", "Numero" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ArchivoVersiones");
    }
}
