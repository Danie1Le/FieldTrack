using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldTrack.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DemoOperatorHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_demo_operator",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "family_id",
                table: "refresh_tokens",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Only the seeder names a user "Demo Operator" and turns its lockout off; Identity creates every
            // other user with lockout on (#135).
            migrationBuilder.Sql("UPDATE users SET is_demo_operator = true WHERE display_name = 'Demo Operator' AND NOT lockout_enabled");
            // One family per user for tokens issued before families existed, so replaying one revokes all of
            // the user's tokens, as before (ADR 0002).
            migrationBuilder.Sql("UPDATE refresh_tokens SET family_id = user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_demo_operator",
                table: "users");

            migrationBuilder.DropColumn(
                name: "family_id",
                table: "refresh_tokens");
        }
    }
}
