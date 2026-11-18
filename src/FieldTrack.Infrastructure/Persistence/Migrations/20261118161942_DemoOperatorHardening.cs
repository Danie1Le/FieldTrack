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
                // The previous revision keeps writing tokens without a family while a deploy rolls out; each
                // gets a family of its own rather than one shared by every user (#135).
                defaultValueSql: "gen_random_uuid()");

            // Only the seeder turns a user's lockout off (Identity creates every user with lockout on), so an
            // Operator with lockout off is the Demo Operator, whatever its display name: one that no demo-mode
            // startup has renamed still has its email's local part. Risk: an Operator whose lockout was turned
            // off by hand in the database is marked too, and so locked out at the next startup that does not
            // name it (#135).
            migrationBuilder.Sql("""
                UPDATE users SET is_demo_operator = true
                WHERE NOT lockout_enabled
                  AND EXISTS (SELECT 1 FROM user_roles JOIN roles ON roles.id = user_roles.role_id
                              WHERE user_roles.user_id = users.id AND roles.name = 'Operator')
                """);
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
