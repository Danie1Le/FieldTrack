using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldTrack.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AlertActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "acknowledged_at",
                table: "alerts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "acknowledged_by",
                table: "alerts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "latched",
                table: "alerts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "note",
                table: "alerts",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "resolved_by",
                table: "alerts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_alerts_acknowledged_by",
                table: "alerts",
                column: "acknowledged_by");

            migrationBuilder.CreateIndex(
                name: "ix_alerts_resolved_by",
                table: "alerts",
                column: "resolved_by");

            migrationBuilder.AddCheckConstraint(
                name: "ck_alerts_resolved_by",
                table: "alerts",
                sql: "(resolved_by IS NOT NULL) = (resolve_reason IS NOT DISTINCT FROM 'Manual') AND (resolved_by IS NOT NULL OR (note IS NULL AND NOT latched))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_alerts_status_acknowledged",
                table: "alerts",
                sql: "(acknowledged_by IS NULL) = (acknowledged_at IS NULL) AND (status <> 'Open' OR acknowledged_at IS NULL) AND (status <> 'Acknowledged' OR acknowledged_at IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "fk_alerts_users_acknowledged_by",
                table: "alerts",
                column: "acknowledged_by",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_alerts_users_resolved_by",
                table: "alerts",
                column: "resolved_by",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_alerts_users_acknowledged_by",
                table: "alerts");

            migrationBuilder.DropForeignKey(
                name: "fk_alerts_users_resolved_by",
                table: "alerts");

            migrationBuilder.DropIndex(
                name: "ix_alerts_acknowledged_by",
                table: "alerts");

            migrationBuilder.DropIndex(
                name: "ix_alerts_resolved_by",
                table: "alerts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_alerts_resolved_by",
                table: "alerts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_alerts_status_acknowledged",
                table: "alerts");

            migrationBuilder.DropColumn(
                name: "acknowledged_at",
                table: "alerts");

            migrationBuilder.DropColumn(
                name: "acknowledged_by",
                table: "alerts");

            migrationBuilder.DropColumn(
                name: "latched",
                table: "alerts");

            migrationBuilder.DropColumn(
                name: "note",
                table: "alerts");

            migrationBuilder.DropColumn(
                name: "resolved_by",
                table: "alerts");
        }
    }
}
