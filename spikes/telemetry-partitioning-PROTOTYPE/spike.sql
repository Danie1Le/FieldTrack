-- PROTOTYPE, wipe me. Throwaway spike for the Wayfinder ticket
-- "Telemetry table: time partitions vs idempotent writes". Never merged.
--
-- Question: how can the Reading table be partitioned by time while each Reading,
-- identified by (Device, Boot, Sequence number) per ADR 0001, is stored only once?
-- Postgres requires every unique constraint on a partitioned table to include the
-- partition key, so unique (device_id, boot, seq) cannot sit on a time-partitioned table.
--
-- Options compared (one schema each):
--   a  RANGE partitions on device_time, PK (device_id, boot, seq, device_time)
--   b  RANGE partitions on device_time, plus an unpartitioned key table (device_id, boot, seq)
--   c  HASH partitions on device_id, PK (device_id, boot, seq)
--   d  no partitioning, PK (device_id, boot, seq)
--
-- Data: 50 Devices (one Asset each), one Reading per minute for 30 days (~2.16M rows),
-- then timed batched inserts of fresh Readings, then duplicate replays.
-- Timings are server-side only (no client or network), so compare options, not absolutes.

\set ON_ERROR_STOP on
\timing off
SET client_min_messages = warning;
CREATE EXTENSION IF NOT EXISTS postgis;

DROP SCHEMA IF EXISTS a, b, c, d CASCADE;
CREATE SCHEMA a; CREATE SCHEMA b; CREATE SCHEMA c; CREATE SCHEMA d;

-- shared clock for the run
DROP TABLE IF EXISTS public.spike_clock;
CREATE TABLE public.spike_clock AS SELECT date_trunc('day', now()) - interval '30 days' AS t0;

-- ---------- schemas ----------
-- Columns follow the telemetry contract; asset_id is stamped on store (Asset model decision).
CREATE TABLE a.reading (
  device_id int NOT NULL, boot int NOT NULL, seq bigint NOT NULL,
  device_time timestamptz NOT NULL, received_at timestamptz NOT NULL DEFAULT now(),
  asset_id int, location geography(Point,4326) NOT NULL,
  speed_kph real, engine_temp_c real, fuel_pct real, vibration_mm_s real, battery_v real,
  PRIMARY KEY (device_id, boot, seq, device_time)
) PARTITION BY RANGE (device_time);

CREATE TABLE b.reading (
  device_id int NOT NULL, boot int NOT NULL, seq bigint NOT NULL,
  device_time timestamptz NOT NULL, received_at timestamptz NOT NULL DEFAULT now(),
  asset_id int, location geography(Point,4326) NOT NULL,
  speed_kph real, engine_temp_c real, fuel_pct real, vibration_mm_s real, battery_v real
) PARTITION BY RANGE (device_time);
CREATE TABLE b.reading_key (
  device_id int NOT NULL, boot int NOT NULL, seq bigint NOT NULL,
  device_time timestamptz NOT NULL,
  PRIMARY KEY (device_id, boot, seq)
);

CREATE TABLE c.reading (LIKE b.reading INCLUDING DEFAULTS, PRIMARY KEY (device_id, boot, seq))
  PARTITION BY HASH (device_id);
CREATE TABLE d.reading (LIKE b.reading INCLUDING DEFAULTS, PRIMARY KEY (device_id, boot, seq));

-- daily partitions from t0 - 1 day to t0 + 33 days
DO $$
DECLARE t0 timestamptz := (SELECT t0 FROM public.spike_clock); i int;
BEGIN
  FOR i IN -1..33 LOOP
    EXECUTE format('CREATE TABLE a.reading_%s PARTITION OF a.reading FOR VALUES FROM (%L) TO (%L)',
                   i + 1, t0 + i * interval '1 day', t0 + (i + 1) * interval '1 day');
    EXECUTE format('CREATE TABLE b.reading_%s PARTITION OF b.reading FOR VALUES FROM (%L) TO (%L)',
                   i + 1, t0 + i * interval '1 day', t0 + (i + 1) * interval '1 day');
  END LOOP;
  FOR i IN 0..7 LOOP
    EXECUTE format('CREATE TABLE c.reading_%s PARTITION OF c.reading FOR VALUES WITH (MODULUS 8, REMAINDER %s)', i, i);
  END LOOP;
END $$;

CREATE INDEX ON a.reading (asset_id, device_time);
CREATE INDEX ON b.reading (asset_id, device_time);
CREATE INDEX ON c.reading (asset_id, device_time);
CREATE INDEX ON d.reading (asset_id, device_time);
CREATE INDEX ON a.reading USING gist (location);
CREATE INDEX ON b.reading USING gist (location);
CREATE INDEX ON c.reading USING gist (location);
CREATE INDEX ON d.reading USING gist (location);

-- ---------- batch generator ----------
-- Readings for minutes [m_from, m_to) after t0, for all 50 devices, boot 1, seq = minute.
CREATE OR REPLACE FUNCTION public.gen(m_from int, m_to int, shift_secs int DEFAULT 0)
RETURNS TABLE (device_id int, boot int, seq bigint, device_time timestamptz, asset_id int,
               location geography, speed_kph real, engine_temp_c real, fuel_pct real,
               vibration_mm_s real, battery_v real)
LANGUAGE sql AS $$
  SELECT dv, 1, m, (SELECT t0 FROM public.spike_clock) + m * interval '1 minute' + shift_secs * interval '1 second', dv,
         ST_SetSRID(ST_MakePoint(-122.68 + dv * 0.01 + sin(m / 300.0) * 0.02,
                                 45.52 + cos(m / 300.0) * 0.02), 4326)::geography,
         (random() * 80)::real, (80 + random() * 20)::real, (100 - m / 500.0)::real,
         (random() * 5)::real, (12 + random())::real
  FROM generate_series(1, 50) dv, generate_series(m_from, m_to - 1) m
$$;

-- ---------- insert paths (what the ingest worker would run per batch) ----------
CREATE OR REPLACE FUNCTION public.ins(opt text, m_from int, m_to int, shift_secs int DEFAULT 0)
RETURNS int LANGUAGE plpgsql AS $$
DECLARE n int;
BEGIN
  IF opt = 'b' THEN
    WITH batch AS (SELECT * FROM public.gen(m_from, m_to, shift_secs)),
         k AS (INSERT INTO b.reading_key (device_id, boot, seq, device_time)
               SELECT device_id, boot, seq, device_time FROM batch
               ON CONFLICT DO NOTHING RETURNING device_id, boot, seq),
         r AS (INSERT INTO b.reading (device_id, boot, seq, device_time, asset_id, location,
                                      speed_kph, engine_temp_c, fuel_pct, vibration_mm_s, battery_v)
               SELECT x.device_id, x.boot, x.seq, x.device_time, x.asset_id, x.location,
                      x.speed_kph, x.engine_temp_c, x.fuel_pct, x.vibration_mm_s, x.battery_v
               FROM batch x JOIN k USING (device_id, boot, seq) RETURNING 1)
    SELECT count(*) INTO n FROM r;
  ELSE
    EXECUTE format(
      'WITH r AS (INSERT INTO %I.reading (device_id, boot, seq, device_time, asset_id, location,
                    speed_kph, engine_temp_c, fuel_pct, vibration_mm_s, battery_v)
                  SELECT * FROM public.gen($1, $2, $3) ON CONFLICT DO NOTHING RETURNING 1)
       SELECT count(*) FROM r', opt)
    INTO n USING m_from, m_to, shift_secs;
  END IF;
  RETURN n;
END $$;

CREATE TABLE IF NOT EXISTS public.spike_result (opt text, test text, value numeric, unit text);
TRUNCATE public.spike_result;

-- ---------- 1. bulk load 30 days (43,200 minutes x 50 devices) ----------
DO $$
DECLARE o text; t timestamptz; m int;
BEGIN
  FOREACH o IN ARRAY ARRAY['a','b','c','d'] LOOP
    t := clock_timestamp();
    FOR m IN 0..43199 BY 1440 LOOP PERFORM public.ins(o, m, m + 1440); END LOOP;
    INSERT INTO public.spike_result VALUES
      (o, '1 bulk load 2.16M rows', round(extract(epoch FROM clock_timestamp() - t)::numeric, 1), 's');
  END LOOP;
END $$;
VACUUM ANALYZE;

-- ---------- 2. steady-state batched upserts: 200k fresh rows in batches of 1,000 ----------
-- 20 minutes x 50 devices = 1,000 rows per batch; 200 batches.
DO $$
DECLARE o text; t timestamptz; m int; total int;
BEGIN
  FOREACH o IN ARRAY ARRAY['a','b','c','d'] LOOP
    t := clock_timestamp(); total := 0;
    FOR m IN 43200..(43200 + 200 * 20 - 1) BY 20 LOOP total := total + public.ins(o, m, m + 20); END LOOP;
    INSERT INTO public.spike_result VALUES
      (o, '2 fresh upsert throughput', round(total / extract(epoch FROM clock_timestamp() - t)::numeric), 'rows/s');
  END LOOP;
END $$;

-- ---------- 3. redelivery replay: same 200k rows again (identical duplicates) ----------
DO $$
DECLARE o text; t timestamptz; m int; total int;
BEGIN
  FOREACH o IN ARRAY ARRAY['a','b','c','d'] LOOP
    t := clock_timestamp(); total := 0;
    FOR m IN 43200..(43200 + 200 * 20 - 1) BY 20 LOOP total := total + public.ins(o, m, m + 20); END LOOP;
    INSERT INTO public.spike_result VALUES
      (o, '3 identical replay: rows wrongly stored', total, 'rows'),
      (o, '3 identical replay throughput', round(200000 / extract(epoch FROM clock_timestamp() - t)::numeric), 'rows/s');
  END LOOP;
END $$;

-- ---------- 4. conflicting duplicates: same key, Device time shifted by 1 s ----------
DO $$
DECLARE o text; total int;
BEGIN
  FOREACH o IN ARRAY ARRAY['a','b','c','d'] LOOP
    total := public.ins(o, 43200, 43220, 1);
    INSERT INTO public.spike_result VALUES (o, '4 conflicting dup: rows wrongly stored (of 1000)', total, 'rows');
  END LOOP;
END $$;
VACUUM ANALYZE;

-- ---------- 5. 24-hour route for one Asset (day 15), median of 7 runs ----------
DO $$
DECLARE o text; i int; t timestamptz; ms numeric[]; t0 timestamptz := (SELECT t0 FROM public.spike_clock);
BEGIN
  FOREACH o IN ARRAY ARRAY['a','b','c','d'] LOOP
    ms := '{}';
    FOR i IN 1..7 LOOP
      t := clock_timestamp();
      EXECUTE format('SELECT count(*) FROM (SELECT device_time, location FROM %I.reading
                      WHERE asset_id = 7 AND device_time >= $1 AND device_time < $1 + interval ''1 day''
                      ORDER BY device_time) s', o) USING t0 + interval '15 days';
      ms := ms || round(extract(epoch FROM clock_timestamp() - t)::numeric * 1000, 2);
    END LOOP;
    INSERT INTO public.spike_result
      SELECT o, '5 24h route query (median of 7)', percentile_disc(0.5) WITHIN GROUP (ORDER BY v), 'ms'
      FROM unnest(ms) v;
  END LOOP;
END $$;

-- ---------- 6. storage ----------
INSERT INTO public.spike_result
SELECT s, '6 total size incl. indexes', round(sum(pg_total_relation_size(c.oid)) / 1048576.0), 'MB'
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
CROSS JOIN LATERAL (SELECT n.nspname AS s) x
WHERE n.nspname IN ('a','b','c','d') AND c.relkind = 'r'
GROUP BY s;

-- ---------- results ----------
\pset format aligned
SELECT test,
       max(value) FILTER (WHERE opt = 'a') AS "a range+wide PK",
       max(value) FILTER (WHERE opt = 'b') AS "b range+key table",
       max(value) FILTER (WHERE opt = 'c') AS "c hash device",
       max(value) FILTER (WHERE opt = 'd') AS "d plain",
       max(unit) AS unit
FROM public.spike_result GROUP BY test ORDER BY test;

-- plan check: does option a/b prune partitions for the route query?
EXPLAIN (COSTS OFF)
SELECT device_time, location FROM a.reading
WHERE asset_id = 7 AND device_time >= (SELECT t0 FROM public.spike_clock) + interval '15 days'
  AND device_time < (SELECT t0 FROM public.spike_clock) + interval '16 days';
