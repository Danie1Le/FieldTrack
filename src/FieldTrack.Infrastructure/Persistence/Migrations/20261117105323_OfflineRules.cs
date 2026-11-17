using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldTrack.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OfflineRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_alert_rules_kind",
                table: "alert_rules");

            migrationBuilder.AlterColumn<long>(
                name: "reading_sequence_number",
                table: "alerts",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<Guid>(
                name: "reading_device_id",
                table: "alerts",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<int>(
                name: "reading_boot",
                table: "alerts",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "offline_seconds",
                table: "alert_rules",
                type: "integer",
                nullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "last_sequence_number",
                table: "alert_rule_states",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<Guid>(
                name: "last_device_id",
                table: "alert_rule_states",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<int>(
                name: "last_boot",
                table: "alert_rule_states",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddCheckConstraint(
                name: "ck_alerts_reading",
                table: "alerts",
                sql: "(reading_device_id IS NULL) = (reading_boot IS NULL) AND (reading_boot IS NULL) = (reading_sequence_number IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_alert_rules_kind",
                table: "alert_rules",
                sql: "kind IN ('Threshold', 'Offline')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_alert_rules_offline",
                table: "alert_rules",
                sql: "(kind = 'Offline') = (offline_seconds IS NOT NULL) AND (offline_seconds IS NULL OR offline_seconds > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_alert_rule_states_last_evaluated",
                table: "alert_rule_states",
                sql: "(last_device_id IS NULL) = (last_boot IS NULL) AND (last_boot IS NULL) = (last_sequence_number IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_alerts_reading",
                table: "alerts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_alert_rules_kind",
                table: "alert_rules");

            migrationBuilder.DropCheckConstraint(
                name: "ck_alert_rules_offline",
                table: "alert_rules");

            migrationBuilder.DropCheckConstraint(
                name: "ck_alert_rule_states_last_evaluated",
                table: "alert_rule_states");

            migrationBuilder.DropColumn(
                name: "offline_seconds",
                table: "alert_rules");

            migrationBuilder.AlterColumn<long>(
                name: "reading_sequence_number",
                table: "alerts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "reading_device_id",
                table: "alerts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "reading_boot",
                table: "alerts",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "last_sequence_number",
                table: "alert_rule_states",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "last_device_id",
                table: "alert_rule_states",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "last_boot",
                table: "alert_rule_states",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_alert_rules_kind",
                table: "alert_rules",
                sql: "kind IN ('Threshold')");
        }
    }
}
