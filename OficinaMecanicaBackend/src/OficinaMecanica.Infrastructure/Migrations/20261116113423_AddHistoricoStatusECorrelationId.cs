using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OficinaMecanica.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHistoricoStatusECorrelationId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CorrelationId",
                table: "OrdensServico",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<DateTime>(
                name: "PecasDevolvidasEm",
                table: "OrdensServico",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HistoricoStatusOS",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    OrdemServicoId = table.Column<int>(type: "int", nullable: false),
                    StatusAnterior = table.Column<int>(type: "int", nullable: false),
                    StatusNovo = table.Column<int>(type: "int", nullable: false),
                    Origem = table.Column<int>(type: "int", nullable: false),
                    Motivo = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CorrelationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    OcorridoEm = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoricoStatusOS", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HistoricoStatusOS_OrdensServico_OrdemServicoId",
                        column: x => x.OrdemServicoId,
                        principalTable: "OrdensServico",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            // OS criadas antes desta migration ficariam todas com o Guid vazio, e o índice
            // único abaixo falharia. Cada uma ganha o seu id de saga antes do índice.
            migrationBuilder.Sql(
                "UPDATE OrdensServico SET CorrelationId = UUID() " +
                "WHERE CorrelationId = '00000000-0000-0000-0000-000000000000';");

            migrationBuilder.CreateIndex(
                name: "IX_OrdenServico_CorrelationId",
                table: "OrdensServico",
                column: "CorrelationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HistoricoStatus_OrdemServico",
                table: "HistoricoStatusOS",
                column: "OrdemServicoId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HistoricoStatusOS");

            migrationBuilder.DropIndex(
                name: "IX_OrdenServico_CorrelationId",
                table: "OrdensServico");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "OrdensServico");

            migrationBuilder.DropColumn(
                name: "PecasDevolvidasEm",
                table: "OrdensServico");
        }
    }
}
