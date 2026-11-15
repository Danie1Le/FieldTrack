#!/usr/bin/env bash
# PROTOTYPE, wipe me. Runs the telemetry partitioning spike in a throwaway PostGIS container.
# Usage: bash spikes/telemetry-partitioning-PROTOTYPE/run.sh
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
name=fieldtrack-spike-pg-PROTOTYPE
image=postgis/postgis:18-3.6

docker rm -f "$name" >/dev/null 2>&1 || true
docker run -d --name "$name" -e POSTGRES_PASSWORD=spike -e POSTGRES_DB=spike \
  --shm-size=1g "$image" >/dev/null
until docker exec "$name" pg_isready -U postgres -d spike >/dev/null 2>&1; do sleep 1; done
sleep 3
docker exec -i "$name" psql -U postgres -d spike -q < "$here/spike.sql"
echo "Container $name left running; remove with: docker rm -f $name"
