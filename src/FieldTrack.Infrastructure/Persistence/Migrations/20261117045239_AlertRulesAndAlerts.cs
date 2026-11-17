using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldTrack.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AlertRulesAndAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alert_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    asset_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    severity = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    sensor = table.Column<string>(type: "text", nullable: true),
                    @operator = table.Column<string>(name: "operator", type: "text", nullable: true),
                    threshold = table.Column<double>(type: "double precision", nullable: true),
                    hysteresis = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alert_rules", x => x.id);
                    table.CheckConstraint("ck_alert_rules_kind", "kind IN ('Threshold')");
                    table.CheckConstraint("ck_alert_rules_operator", "operator IN ('GreaterThan', 'LessThan')");
                    table.CheckConstraint("ck_alert_rules_sensor", "sensor IN ('SpeedKph', 'EngineTempC', 'FuelPct', 'VibrationMmS', 'BatteryV')");
                    table.CheckConstraint("ck_alert_rules_severity", "severity IN ('Warning', 'Critical')");
                    table.CheckConstraint("ck_alert_rules_target", "(asset_id IS NULL) <> (asset_group_id IS NULL)");
                    table.CheckConstraint("ck_alert_rules_threshold", "kind <> 'Threshold' OR (sensor IS NOT NULL AND operator IS NOT NULL AND threshold IS NOT NULL AND hysteresis IS NOT NULL AND hysteresis >= 0)");
                    table.ForeignKey(
                        name: "fk_alert_rules_asset_groups_asset_group_id",
                        column: x => x.asset_group_id,
                        principalTable: "asset_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_alert_rules_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "alert_rule_states",
                columns: table => new
                {
                    alert_rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    breached = table.Column<bool>(type: "boolean", nullable: false),
                    last_device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_boot = table.Column<int>(type: "integer", nullable: false),
                    last_sequence_number = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alert_rule_states", x => new { x.alert_rule_id, x.asset_id });
                    table.ForeignKey(
                        name: "fk_alert_rule_states_alert_rules_alert_rule_id",
                        column: x => x.alert_rule_id,
                        principalTable: "alert_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_alert_rule_states_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "alerts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    severity = table.Column<string>(type: "text", nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    raised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolve_reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    from_backlog = table.Column<bool>(type: "boolean", nullable: false),
                    reading_boot = table.Column<int>(type: "integer", nullable: false),
                    reading_device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reading_sequence_number = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alerts", x => x.id);
                    table.CheckConstraint("ck_alerts_resolve_reason", "resolve_reason IN ('Cleared', 'Manual', 'RuleChanged', 'RuleDisabled', 'AssetArchived', 'DeviceDetached')");
                    table.CheckConstraint("ck_alerts_severity", "severity IN ('Warning', 'Critical')");
                    table.CheckConstraint("ck_alerts_status", "status IN ('Open', 'Acknowledged', 'Resolved')");
                    table.ForeignKey(
                        name: "fk_alerts_alert_rules_alert_rule_id",
                        column: x => x.alert_rule_id,
                        principalTable: "alert_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_alerts_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_alert_rule_states_asset_id",
                table: "alert_rule_states",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_alert_rules_asset_group_id",
                table: "alert_rules",
                column: "asset_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_alert_rules_asset_id",
                table: "alert_rules",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_alerts_alert_rule_id_asset_id_active",
                table: "alerts",
                columns: new[] { "alert_rule_id", "asset_id" },
                unique: true,
                filter: "status <> 'Resolved'");

            migrationBuilder.CreateIndex(
                name: "ix_alerts_asset_id_raised_at",
                table: "alerts",
                columns: new[] { "asset_id", "raised_at" });

            migrationBuilder.CreateIndex(
                name: "ix_alerts_raised_at",
                table: "alerts",
                column: "raised_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_rule_states");

            migrationBuilder.DropTable(
                name: "alerts");

            migrationBuilder.DropTable(
                name: "alert_rules");
        }
    }
}
