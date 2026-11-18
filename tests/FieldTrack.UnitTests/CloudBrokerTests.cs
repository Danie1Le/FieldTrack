using FieldTrack.IntegrationTests;

namespace FieldTrack.UnitTests;

/// <summary>The Container Apps broker image (<c>docker/mosquitto/cloud</c>) against the local broker it stands in for (#154).</summary>
public sealed class CloudBrokerTests
{
    private static readonly string[] _cloudConfig = Lines("mosquitto-cloud/mosquitto.conf");

    [Theory]
    // ADR 0004: the ingest session and its queue survive a broker restart, and the broker never drops what it queued.
    [InlineData("persistence true")]
    [InlineData("persistence_location /mosquitto/data/")]
    [InlineData("max_queued_messages 0")]
    public void The_cloud_broker_keeps_the_local_brokers_queue_settings(string setting)
    {
        Lines("mosquitto.conf").ShouldContain(setting);
        _cloudConfig.ShouldContain(setting);
    }

    [Fact]
    public void The_cloud_broker_admits_only_users_in_its_password_file()
    {
        _cloudConfig.ShouldContain("allow_anonymous false");
        _cloudConfig.ShouldContain(line => line.StartsWith("password_file ", StringComparison.Ordinal));
    }

    [Fact]
    public void The_cloud_broker_image_builds_on_the_tested_Mosquitto_image() =>
        Lines("mosquitto-cloud/Dockerfile").ShouldContain($"FROM {TestImages.Mosquitto}");

    private static string[] Lines(string file) =>
        [.. File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, file)).Select(line => line.Trim())];
}
