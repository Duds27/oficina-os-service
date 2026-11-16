using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OficinaMecanica.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSagaOrdemServico : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SagaOrdemServico",
                columns: table => new
                {
                    CorrelationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CurrentState = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    OsId = table.Column<int>(type: "int", nullable: false),
                    NumeroOS = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    OrcamentoId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    PagamentoId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    LinkPagamento = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PrazoExpiraEm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    MotivoCancelamento = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CriadoEm = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    AtualizadoEm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    RowVersion = table.Column<DateTime>(type: "datetime(6)", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SagaOrdemServico", x => x.CorrelationId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_SagaOrdemServico_OsId",
                table: "SagaOrdemServico",
                column: "OsId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SagaOrdemServico_PrazoExpiraEm",
                table: "SagaOrdemServico",
                column: "PrazoExpiraEm");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SagaOrdemServico");
        }
    }
}
