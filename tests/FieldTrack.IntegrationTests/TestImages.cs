namespace FieldTrack.IntegrationTests;

/// <summary>
/// The container images the integration tests start. They must equal the images in
/// docker-compose.yml so tests and local runs use the same PostgreSQL, PostGIS and Mosquitto;
/// a unit test fails when the two drift apart.
/// </summary>
internal static class TestImages
{
    /// <summary>PostgreSQL 16 with PostGIS 3.6, the versions Azure Flexible Server offers (spec #21).</summary>
    public const string PostGis = "postgis/postgis:16-3.6";

    /// <summary>The MQTT broker the Devices publish to and the ingest worker subscribes to.</summary>
    public const string Mosquitto = "eclipse-mosquitto:2.1.2-alpine";
}
