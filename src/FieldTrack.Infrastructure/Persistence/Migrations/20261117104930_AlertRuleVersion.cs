using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldTrack.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The Alert Rule version row of ADR 0005: one counter that every change to what the ingest worker evaluates or
    /// caches (Alert Rules, Geofences, and the Archives and detaches that resolve Active Alerts or reset rule state)
    /// bumps in its own transaction, so the worker reloads only when it moved. Hand-written and outside the EF model,
    /// like the readings tables, because only SQL reads and bumps it.
    /// </summary>
    public partial class AlertRuleVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Exactly one row, created here and never inserted or deleted again.
            migrationBuilder.Sql("""
                CREATE TABLE alert_rule_version (
                    id boolean PRIMARY KEY DEFAULT true CHECK (id),
                    version bigint NOT NULL
                );
                INSERT INTO alert_rule_version (version) VALUES (0);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE alert_rule_version");
        }
    }
}
