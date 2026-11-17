using FieldTrack.Simulator;

namespace FieldTrack.UnitTests;

public sealed class PacingTests
{
    [Fact]
    public void Each_Device_publishes_every_devices_over_rate_seconds()
    {
        var pacing = new Pacing(devices: 4, ratePerSecond: 100);

        pacing.Interval.ShouldBe(TimeSpan.FromMilliseconds(40));
    }

    [Fact]
    public void Devices_start_one_slot_apart_so_sends_spread_evenly_across_the_interval()
    {
        var pacing = new Pacing(devices: 4, ratePerSecond: 100);

        var offsets = Enumerable.Range(0, 4).Select(pacing.StartOffset).ToArray();

        offsets.ShouldBe([TimeSpan.Zero, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(30)]);
        offsets[^1].ShouldBeLessThan(pacing.Interval);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(3, 0)]
    [InlineData(3, -5)]
    [InlineData(3, double.NaN)]
    [InlineData(3, double.PositiveInfinity)]
    [InlineData(1, 1e9)]
    [InlineData(1, 1e-9)]
    public void A_fleet_or_rate_that_cannot_be_paced_is_refused(int devices, double rate)
    {
        Should.Throw<SimulatorException>(() => new Pacing(devices, rate));
    }
}
