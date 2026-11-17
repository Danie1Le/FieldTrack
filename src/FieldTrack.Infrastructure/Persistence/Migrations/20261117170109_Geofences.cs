using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace FieldTrack.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Geofences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_alert_rules_kind",
                table: "alert_rules");

            migrationBuilder.AddColumn<Guid>(
                name: "geofence_id",
                table: "alert_rules",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "geofences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    area = table.Column<Polygon>(type: "geography (Polygon, 4326)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_geofences", x => x.id);
                    table.CheckConstraint("ck_geofences_kind", "kind IN ('Inclusion', 'Exclusion')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_alert_rules_geofence_id",
                table: "alert_rules",
                column: "geofence_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_alert_rules_geofence",
                table: "alert_rules",
                sql: "(kind IN ('GeofenceExit', 'GeofenceEnter')) = (geofence_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_alert_rules_kind",
                table: "alert_rules",
                sql: "kind IN ('Threshold', 'GeofenceExit', 'GeofenceEnter')");

            migrationBuilder.CreateIndex(
                name: "ix_geofences_area",
                table: "geofences",
                column: "area")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.AddForeignKey(
                name: "fk_alert_rules_geofences_geofence_id",
                table: "alert_rules",
                column: "geofence_id",
                principalTable: "geofences",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_alert_rules_geofences_geofence_id",
                table: "alert_rules");

            migrationBuilder.DropTable(
                name: "geofences");

            migrationBuilder.DropIndex(
                name: "ix_alert_rules_geofence_id",
                table: "alert_rules");

            migrationBuilder.DropCheckConstraint(
                name: "ck_alert_rules_geofence",
                table: "alert_rules");

            migrationBuilder.DropCheckConstraint(
                name: "ck_alert_rules_kind",
                table: "alert_rules");

            migrationBuilder.DropColumn(
                name: "geofence_id",
                table: "alert_rules");

            migrationBuilder.AddCheckConstraint(
                name: "ck_alert_rules_kind",
                table: "alert_rules",
                sql: "kind IN ('Threshold')");
        }
    }
}
