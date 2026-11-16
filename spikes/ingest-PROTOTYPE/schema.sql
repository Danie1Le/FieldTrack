-- PROTOTYPE, wipe me. Telemetry table per ADR 0003: daily partitions on device_time + unpartitioned key table.
DROP TABLE IF EXISTS reading, reading_key;
CREATE EXTENSION IF NOT EXISTS postgis;

CREATE TABLE reading_key (
  device_id int NOT NULL, boot int NOT NULL, seq bigint NOT NULL,
  device_time timestamptz NOT NULL, content_hash bytea NOT NULL,
  PRIMARY KEY (device_id, boot, seq)
);

CREATE TABLE reading (
  device_id int NOT NULL, boot int NOT NULL, seq bigint NOT NULL,
  asset_id int NOT NULL,
  device_time timestamptz NOT NULL, received_time timestamptz NOT NULL,
  location geography(Point,4326) NOT NULL,
  speed_kph real, engine_temp_c real, fuel_pct real, vibration_mm_s real, battery_v real
) PARTITION BY RANGE (device_time);

DO $$
DECLARE d date;
BEGIN
  FOR d IN SELECT generate_series(current_date - 1, current_date + 2, interval '1 day')::date LOOP
    EXECUTE format('CREATE TABLE reading_%s PARTITION OF reading FOR VALUES FROM (%L) TO (%L)',
                   to_char(d, 'YYYYMMDD'), d, d + 1);
  END LOOP;
END $$;

CREATE INDEX ON reading (asset_id, device_time);
