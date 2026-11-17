using FieldTrack.Domain.Assets;

namespace FieldTrack.UnitTests;

public sealed class AssetStatusTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _attachedLongAgo = _now.AddDays(-1);

    [Fact]
    public void No_device_comes_first_even_with_an_Active_Alert()
    {
        AssetStatuses.Of(attachedAt: null, hasActiveAlert: true, lastReceivedAt: null, _now).ShouldBe(AssetStatus.NoDevice);
    }

    [Theory]
    [InlineData(3600)]
    [InlineData(5)]
    public void Alerting_beats_Offline_and_Normal(int receivedSecondsAgo)
    {
        AssetStatuses.Of(_attachedLongAgo, hasActiveAlert: true, lastReceivedAt: _now.AddSeconds(-receivedSecondsAgo), _now)
            .ShouldBe(AssetStatus.Alerting);
    }

    [Theory]
    [InlineData(119, AssetStatus.Normal)]
    [InlineData(120, AssetStatus.Offline)]
    public void Offline_from_120_seconds_without_a_Reading_received(int secondsAgo, AssetStatus expected)
    {
        AssetStatuses.Of(_attachedLongAgo, hasActiveAlert: false, lastReceivedAt: _now.AddSeconds(-secondsAgo), _now).ShouldBe(expected);
    }

    [Theory]
    [InlineData(119, AssetStatus.Normal)]
    [InlineData(120, AssetStatus.Offline)]
    public void A_Device_that_never_sent_a_Reading_counts_silence_from_the_attach(int attachedSecondsAgo, AssetStatus expected)
    {
        AssetStatuses.Of(_now.AddSeconds(-attachedSecondsAgo), hasActiveAlert: false, lastReceivedAt: null, _now).ShouldBe(expected);
    }

    [Fact]
    public void A_refitted_Device_counts_silence_from_the_attach_not_its_old_Reading()
    {
        AssetStatuses.Of(_now.AddSeconds(-10), hasActiveAlert: false, lastReceivedAt: _now.AddDays(-30), _now).ShouldBe(AssetStatus.Normal);
    }

    [Fact]
    public void Normal_when_the_Device_reported_recently_and_nothing_is_Active()
    {
        AssetStatuses.Of(_attachedLongAgo, hasActiveAlert: false, lastReceivedAt: _now.AddSeconds(-5), _now).ShouldBe(AssetStatus.Normal);
    }
}
