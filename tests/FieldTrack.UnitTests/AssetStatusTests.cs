using FieldTrack.Domain.Assets;

namespace FieldTrack.UnitTests;

public sealed class AssetStatusTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_device_comes_first_even_with_an_Active_Alert()
    {
        AssetStatuses.Of(hasDevice: false, hasActiveAlert: true, lastReceivedAt: null, _now).ShouldBe(AssetStatus.NoDevice);
    }

    [Fact]
    public void Alerting_beats_Offline()
    {
        AssetStatuses.Of(hasDevice: true, hasActiveAlert: true, lastReceivedAt: _now.AddHours(-1), _now).ShouldBe(AssetStatus.Alerting);
    }

    [Theory]
    [InlineData(120, AssetStatus.Normal)]
    [InlineData(121, AssetStatus.Offline)]
    public void Offline_after_120_seconds_without_a_Reading_received(int secondsAgo, AssetStatus expected)
    {
        AssetStatuses.Of(hasDevice: true, hasActiveAlert: false, lastReceivedAt: _now.AddSeconds(-secondsAgo), _now).ShouldBe(expected);
    }

    [Fact]
    public void A_Device_that_never_sent_a_Reading_is_Offline()
    {
        AssetStatuses.Of(hasDevice: true, hasActiveAlert: false, lastReceivedAt: null, _now).ShouldBe(AssetStatus.Offline);
    }

    [Fact]
    public void Normal_when_the_Device_reported_recently_and_nothing_is_Active()
    {
        AssetStatuses.Of(hasDevice: true, hasActiveAlert: false, lastReceivedAt: _now.AddSeconds(-5), _now).ShouldBe(AssetStatus.Normal);
    }
}
