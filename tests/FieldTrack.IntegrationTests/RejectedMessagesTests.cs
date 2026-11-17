using System.Net;
using FieldTrack.Api.Telemetry;

namespace FieldTrack.IntegrationTests;

public sealed class RejectedMessagesTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    private const string Insert = "INSERT INTO rejected_messages (topic, topic_clipped, payload, reason, received_time) VALUES (@p0, false, @p1, @p2, @p3)";

    [Fact]
    public async Task Admins_list_Rejected_messages_newest_first_with_the_payload_exactly_as_received()
    {
        using var admin = await AdminClientAsync();
        var received = new DateTimeOffset(2026, 11, 16, 9, 30, 0, 123, TimeSpan.Zero);
        await using var database = await OpenDatabaseAsync();
        await SqlAsync(database, Insert, "fieldtrack/v1/devices/GHOST-1/telemetry", "{\"boot\":1}"u8.ToArray(), "UnknownSerialNumber", received);
        // Not UTF-8, so only the exact bytes can show what the Device sent.
        await SqlAsync(database, Insert, "fieldtrack/v1/devices/TRK-007/telemetry", new byte[] { 0xff, 0x00, 0x7b }, "BadJson", received.AddSeconds(1));

        var listed = await ListAsync(admin, "/rejected-messages");

        listed.Select(message => message.GetProperty("reason").GetString()).ShouldBe(["BadJson", "UnknownSerialNumber"]);
        listed[0].GetProperty("payload").GetBytesFromBase64().ShouldBe([0xff, 0x00, 0x7b]);
        listed[0].GetProperty("topic").GetString().ShouldBe("fieldtrack/v1/devices/TRK-007/telemetry");
        listed[0].GetProperty("topicClipped").GetBoolean().ShouldBeFalse();
        listed[1].GetProperty("payload").GetBytesFromBase64().ShouldBe("{\"boot\":1}"u8.ToArray());
        listed[1].GetProperty("receivedTime").GetDateTimeOffset().ShouldBe(received);
    }

    [Fact]
    public async Task A_page_holds_PageSize_Rejected_messages_and_before_continues_with_older_ones()
    {
        using var admin = await AdminClientAsync();
        await using var database = await OpenDatabaseAsync();
        await SqlAsync(database, """
            INSERT INTO rejected_messages (topic, topic_clipped, payload, reason, received_time)
            SELECT 'fieldtrack/v1/devices/TRK-007/telemetry', false, '\x7b7d', 'BadJson', now() FROM generate_series(0, @p0)
            """, RejectedMessagesController.PageSize);

        var first = await ListAsync(admin, "/rejected-messages");
        var rest = await ListAsync(admin, $"/rejected-messages?before={first[^1].GetProperty("id").GetInt64()}");

        first.Length.ShouldBe(RejectedMessagesController.PageSize);
        rest.Length.ShouldBe(1);
        rest[0].GetProperty("id").GetInt64().ShouldBeLessThan(first[^1].GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Operators_may_not_list_Rejected_messages()
    {
        using var admin = await AdminClientAsync();
        await AuthClient.CreateUserAsync(admin, "operator@fieldtrack.test", "Operator", "Operator-pass-1");
        using var client = Fixture.Api.CreateClient();
        await AuthClient.AuthorizeAsync(client, "operator@fieldtrack.test", "Operator-pass-1");

        using var response = await client.GetAsync("/rejected-messages", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
