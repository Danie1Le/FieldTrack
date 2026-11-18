using System.Text;
using FieldTrack.Domain.Telemetry;

namespace FieldTrack.UnitTests;

public sealed class TelemetryPayloadTests
{
    // 2026-11-16T12:00:00Z
    private const long ReceivedMs = 1_794_830_400_000;
    private static readonly DateTimeOffset _received = DateTimeOffset.FromUnixTimeMilliseconds(ReceivedMs);

    [Fact]
    public void A_full_payload_becomes_a_Reading_and_unknown_fields_are_ignored()
    {
        var reading = Accept($$"""
            {"boot":3,"seq":42,"ts":{{ReceivedMs - 1500}},"lat":-33.865143,"lon":151.2099,
             "speedKph":61.5,"engineTempC":92.25,"fuelPct":48,"vibrationMmS":3.5,"batteryV":12.75,"firmware":"1.4.2"}
            """);

        reading.ShouldBe(new Reading(3, 42, new DateTimeOffset(2026, 11, 16, 11, 59, 58, 500, TimeSpan.Zero), -33.865143, 151.2099, 61.5f, 92.25f, 48f, 3.5f, 12.75f));
    }

    [Fact]
    public void Sensors_the_Device_does_not_carry_are_left_empty()
    {
        var reading = Accept($$"""{"boot":1,"seq":0,"ts":{{ReceivedMs}},"lat":0,"lon":0,"batteryV":3.3}""");

        reading.ShouldBe(new Reading(1, 0, _received, 0, 0, null, null, null, null, 3.3f));
    }

    [Theory]
    [InlineData("boot")]
    [InlineData("seq")]
    [InlineData("ts")]
    [InlineData("lat")]
    [InlineData("lon")]
    public void A_payload_without_a_required_field_is_rejected(string missing)
    {
        var fields = new Dictionary<string, string> { ["boot"] = "1", ["seq"] = "7", ["ts"] = $"{ReceivedMs}", ["lat"] = "51.5", ["lon"] = "-0.12" };
        fields.Remove(missing);

        Reject("{" + string.Join(",", fields.Select(field => $"\"{field.Key}\":{field.Value}")) + "}").ShouldBe(RejectReason.MissingField);
    }

    [Fact]
    public void A_required_field_sent_as_null_counts_as_missing() =>
        Reject($$"""{"boot":1,"seq":null,"ts":{{ReceivedMs}},"lat":51.5,"lon":-0.12}""").ShouldBe(RejectReason.MissingField);

    [Fact]
    public void Field_names_are_camelCase_only() =>
        Reject($$"""{"Boot":1,"Seq":7,"Ts":{{ReceivedMs}},"Lat":51.5,"Lon":-0.12}""").ShouldBe(RejectReason.MissingField);

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2]")]
    [InlineData("{\"boot\":\"1\",\"seq\":7,\"ts\":1794830400000,\"lat\":51.5,\"lon\":-0.12}")]
    [InlineData("{\"boot\":1.5,\"seq\":7,\"ts\":1794830400000,\"lat\":51.5,\"lon\":-0.12}")]
    [InlineData("{\"boot\":1,\"seq\":7,\"ts\":1794830400000,\"lat\":51.5,\"lon\":-0.12,\"fuelPct\":\"full\"}")]
    public void A_payload_that_is_not_JSON_of_the_right_types_is_rejected_as_bad_JSON(string payload) =>
        Reject(payload).ShouldBe(RejectReason.BadJson);

    [Theory]
    [InlineData(90.000001, 0)]
    [InlineData(-90.000001, 0)]
    [InlineData(0, 180.000001)]
    [InlineData(0, -180.000001)]
    public void A_position_off_the_globe_is_rejected(double lat, double lon) =>
        Reject($$"""{"boot":1,"seq":7,"ts":{{ReceivedMs}},"lat":{{lat}},"lon":{{lon}}}""").ShouldBe(RejectReason.PositionOutOfRange);

    [Theory]
    [InlineData(90, 180)]
    [InlineData(-90, -180)]
    public void The_poles_and_the_antimeridian_are_on_the_globe(double lat, double lon) =>
        Accept($$"""{"boot":1,"seq":7,"ts":{{ReceivedMs}},"lat":{{lat}},"lon":{{lon}}}""").Latitude.ShouldBe(lat);

    [Fact]
    public void A_Reading_up_to_5_minutes_ahead_of_its_Received_time_is_accepted_and_one_more_millisecond_is_not()
    {
        var limit = ReceivedMs + (long)TimeSpan.FromMinutes(5).TotalMilliseconds;

        Accept(Payload(limit)).DeviceTime.ToUnixTimeMilliseconds().ShouldBe(limit);
        Reject(Payload(limit + 1)).ShouldBe(RejectReason.TooFarAhead);
    }

    [Fact]
    public void A_Reading_up_to_7_days_late_is_accepted_and_one_more_millisecond_is_not()
    {
        var limit = ReceivedMs - (long)TimeSpan.FromDays(7).TotalMilliseconds;

        Accept(Payload(limit)).DeviceTime.ToUnixTimeMilliseconds().ShouldBe(limit);
        Reject(Payload(limit - 1)).ShouldBe(RejectReason.TooLate);
    }

    [Theory]
    [InlineData(long.MaxValue, RejectReason.TooFarAhead)]
    [InlineData(long.MinValue, RejectReason.TooLate)]
    public void A_Device_time_beyond_the_calendar_is_rejected_by_the_time_window(long ts, RejectReason reason) =>
        Reject(Payload(ts)).ShouldBe(reason);

    [Theory]
    [InlineData("1e39")]
    [InlineData("-1e39")]
    public void A_sensor_value_past_the_range_of_a_float_is_rejected_as_bad_JSON(string fuelPct) =>
        Reject($$"""{"boot":1,"seq":7,"ts":{{ReceivedMs}},"lat":51.5,"lon":-0.12,"fuelPct":{{fuelPct}}}""").ShouldBe(RejectReason.BadJson);

    [Fact]
    public void A_field_sent_twice_is_rejected_as_bad_JSON() =>
        Reject($$"""{"boot":1,"seq":7,"ts":{{ReceivedMs}},"lat":51.5,"lon":-0.12,"boot":2}""").ShouldBe(RejectReason.BadJson);

    [Fact]
    public void Invalid_UTF8_is_rejected_as_bad_JSON_even_in_an_ignored_field()
    {
        var payload = Encoding.UTF8.GetBytes($$"""{"boot":1,"seq":7,"ts":{{ReceivedMs}},"lat":51.5,"lon":-0.12,"x":"??"}""");
        payload[^3] = 0xFF;
        payload[^4] = 0xFE;

        TelemetryPayload.TryParse(payload, _received, out _, out var reason).ShouldBeFalse();
        reason.ShouldBe(RejectReason.BadJson);
    }

    [Fact]
    public void A_payload_up_to_MaxPayloadBytes_is_accepted_and_one_more_byte_is_not()
    {
        var head = Payload(ReceivedMs)[..^1] + ",\"x\":\"";
        string PaddedTo(int bytes) => head + new string('a', bytes - head.Length - 2) + "\"}";

        Accept(PaddedTo(TelemetryPayload.MaxPayloadBytes));
        Reject(PaddedTo(TelemetryPayload.MaxPayloadBytes + 1)).ShouldBe(RejectReason.BadJson);
    }

    [Theory]
    [InlineData(-1, 7)]
    [InlineData(1, -1)]
    public void A_negative_Boot_or_Sequence_number_is_rejected_as_bad_JSON(int boot, long seq) =>
        Reject($$"""{"boot":{{boot}},"seq":{{seq}},"ts":{{ReceivedMs}},"lat":51.5,"lon":-0.12}""").ShouldBe(RejectReason.BadJson);

    [Theory]
    [InlineData("mosquitto.conf")]
    [InlineData("mosquitto-cloud/mosquitto.conf")]
    public void Mosquitto_refuses_payloads_larger_than_MaxPayloadBytes(string config) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, config))
            .ShouldContain($"\nmessage_size_limit {TelemetryPayload.MaxPayloadBytes}");

    private static string Payload(long ts) => $$"""{"boot":1,"seq":7,"ts":{{ts}},"lat":51.5,"lon":-0.12}""";

    private static Reading Accept(string payload)
    {
        TelemetryPayload.TryParse(Encoding.UTF8.GetBytes(payload), _received, out var reading, out var reason)
            .ShouldBeTrue($"rejected as {reason}");
        return reading.ShouldNotBeNull();
    }

    private static RejectReason Reject(string payload)
    {
        TelemetryPayload.TryParse(Encoding.UTF8.GetBytes(payload), _received, out var reading, out var reason).ShouldBeFalse();
        reading.ShouldBeNull();
        return reason;
    }
}
