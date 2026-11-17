using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldTrack.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The version row of ADR 0005: one counter that every change to what ingest evaluates (Alert Rules, Geofences,
    /// rule state reset outside ingest) raises in its own transaction, so the ingest worker reloads only when it
    /// moved. Hand-written and outside the EF model, like the readings tables, because only SQL reads and raises it.
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
