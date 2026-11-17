using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldTrack.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Readings and the Reading key table (ADR 0003). EF cannot model a partitioned table, so both are
    /// hand-written here and stay out of the EF model; the ingest writer (ADR 0004) must match them.
    /// Daily partitions are created by the API's maintenance job, not by migrations.
    /// </summary>
    public partial class Readings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                -- One row per stored Reading identity (ADR 0001); the first stored copy wins and the hash tells
                -- identical from conflicting duplicates. Unpartitioned, because a unique key on the partitioned
                -- table would have to include device_time.
                CREATE TABLE reading_keys (
                    device_id uuid NOT NULL,
                    boot integer NOT NULL,
                    seq bigint NOT NULL,
                    device_time timestamptz NOT NULL,
                    content_hash bytea NOT NULL,
                    CONSTRAINT pk_reading_keys PRIMARY KEY (device_id, boot, seq)
                );

                -- No foreign keys: an ingest batch must never fail on one, and deleting an Asset or Device
                -- decides between remove and Archive by asking whether Readings exist (#24, #25).
                -- asset_id is null for a Reading from a Device with no Attachment covering its Device time.
                CREATE TABLE readings (
                    device_id uuid NOT NULL,
                    boot integer NOT NULL,
                    seq bigint NOT NULL,
                    asset_id uuid NULL,
                    device_time timestamptz NOT NULL,
                    received_time timestamptz NOT NULL,
                    location geography(Point, 4326) NOT NULL,
                    speed_kph real NULL,
                    engine_temp_c real NULL,
                    fuel_pct real NULL,
                    vibration_mm_s real NULL,
                    battery_v real NULL
                ) PARTITION BY RANGE (device_time);

                -- An Asset's history by Device time, and whether an Asset or Device has Readings at all.
                CREATE INDEX ix_readings_asset_id_device_time ON readings (asset_id, device_time);
                CREATE INDEX ix_readings_device_id_device_time ON readings (device_id, device_time);
                CREATE INDEX ix_readings_location ON readings USING gist (location);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TABLE readings;
                DROP TABLE reading_keys;
                """);
        }
    }
}
