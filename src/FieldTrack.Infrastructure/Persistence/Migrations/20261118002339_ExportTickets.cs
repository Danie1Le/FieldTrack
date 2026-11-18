using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldTrack.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExportTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "export_tickets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    range_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    range_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_export_tickets", x => x.id);
                    table.ForeignKey(
                        name: "fk_export_tickets_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_export_tickets_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_export_tickets_asset_id",
                table: "export_tickets",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_export_tickets_token_hash",
                table: "export_tickets",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_export_tickets_user_id",
                table: "export_tickets",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "export_tickets");
        }
    }
}
