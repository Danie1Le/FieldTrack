# The broker the Azure Container Apps demo runs (#154): MQTT over WebSockets behind HTTPS ingress, with password auth.
FROM eclipse-mosquitto:2.1.2-alpine

COPY mosquitto.conf /mosquitto/config/mosquitto.conf
COPY entrypoint.sh /fieldtrack-entrypoint.sh
RUN chmod 0755 /fieldtrack-entrypoint.sh

EXPOSE 8080
ENTRYPOINT ["/fieldtrack-entrypoint.sh"]
# Setting ENTRYPOINT clears the base image's CMD.
CMD ["/usr/sbin/mosquitto", "-c", "/mosquitto/config/mosquitto.conf"]
