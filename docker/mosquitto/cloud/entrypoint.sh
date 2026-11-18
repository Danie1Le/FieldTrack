#!/bin/sh
# Writes the broker's password file from the environment at every start, so no password is baked into the image or
# committed (#154). The deployment supplies both passwords as Container Apps secrets.
set -eu

: "${MQTT_INGEST_PASSWORD:?Set MQTT_INGEST_PASSWORD, the password of the ingest user}"
: "${MQTT_SIMULATOR_PASSWORD:?Set MQTT_SIMULATOR_PASSWORD, the password of the simulator user}"

passwords=/mosquitto/config/passwords
rm -f "$passwords"
# The password is an argument only of this short-lived command inside the container; mosquitto_passwd stores a salted hash.
mosquitto_passwd -c -b "$passwords" ingest "$MQTT_INGEST_PASSWORD"
mosquitto_passwd -b "$passwords" simulator "$MQTT_SIMULATOR_PASSWORD"
# Mosquitto refuses a password file that other users can read.
chown mosquitto:mosquitto "$passwords"
chmod 0600 "$passwords"

# The base image's entrypoint hands the data directory to the mosquitto user and runs the broker.
exec /docker-entrypoint.sh "$@"
