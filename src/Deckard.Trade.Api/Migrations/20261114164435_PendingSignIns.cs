using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Deckard.Trade.Api.Migrations
{
    /// <inheritdoc />
    public partial class PendingSignIns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pending_sign_ins",
                columns: table => new
                {
                    Key = table.Column<string>(type: "text", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    Expires = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_sign_ins", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pending_sign_ins_Expires",
                table: "pending_sign_ins",
                column: "Expires");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_sign_ins");
        }
    }
}
