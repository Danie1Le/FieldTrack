#!/usr/bin/env bash
# PROTOTYPE, wipe me. Ingest spike: PostGIS + Mosquitto in Docker, worker and publisher on the host.
# Usage: bash spikes/ingest-PROTOTYPE/run.sh [suite|quick]   (default: suite, about 50 minutes)
# Results land in spikes/ingest-PROTOTYPE/out/. Containers are left running; remove with:
#   docker rm -f fieldtrack-spike-pg-PROTOTYPE fieldtrack-spike-mqtt-PROTOTYPE
set -euo pipefail
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")"
pg=fieldtrack-spike-pg-PROTOTYPE
mq=fieldtrack-spike-mqtt-PROTOTYPE

docker rm -f "$pg" "$mq" >/dev/null 2>&1 || true
docker run -d --name "$pg" -e POSTGRES_PASSWORD=spike -p 15432:5432 --shm-size=1g postgis/postgis:18-3.6 >/dev/null
docker create --name "$mq" -p 11883:1883 eclipse-mosquitto:2.0.22 >/dev/null
docker cp mosquitto.conf "$mq":/mosquitto/config/mosquitto.conf
docker start "$mq" >/dev/null

# The postgis image restarts once after init; TCP only answers after the final start.
until docker exec "$pg" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1; do sleep 1; done
sleep 2

mkdir -p out
dotnet build -c Release -o bin/out -v q --nologo
dotnet bin/out/IngestSpike.PROTOTYPE.dll "${1:-suite}"
