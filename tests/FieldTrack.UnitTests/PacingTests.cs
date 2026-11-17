using FieldTrack.Simulator;

namespace FieldTrack.UnitTests;

public sealed class PacingTests
{
    [Fact]
    public void Each_Device_owes_a_Reading_every_devices_over_rate_seconds()
    {
        var pacing = new Pacing(devices: 4, ratePerSecond: 100);

        pacing.DueAt(deviceIndex: 2, count: 0).ShouldBe(TimeSpan.FromMilliseconds(20));
        pacing.DueAt(deviceIndex: 2, count: 3).ShouldBe(TimeSpan.FromMilliseconds(140));
    }

    [Fact]
    public void Devices_start_one_slot_apart_so_sends_spread_evenly_across_the_interval()
    {
        var pacing = new Pacing(devices: 4, ratePerSecond: 100);

        var firsts = Enumerable.Range(0, 4).Select(device => pacing.DueAt(device, count: 0)).ToArray();

        firsts.ShouldBe([TimeSpan.Zero, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(30)]);
    }

    [Fact(Timeout = 30_000)]
    public async Task A_clock_that_wakes_late_still_delivers_every_Reading_due_so_the_rate_is_met()
    {
        // Windows wakes a timer on a 15.625 ms boundary: 7 Devices at 500/s are due every 2 ms, so every wake finds several overdue.
        const int devices = 7;
        const double rate = 500;
        var granule = TimeSpan.FromMilliseconds(15.625);
        var pacing = new Pacing(devices, rate);
        var clock = new CoarseClock();
        var startTimestamp = clock.GetTimestamp();
        var due = new List<(int Device, TimeSpan At)>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var consumers = Enumerable.Range(0, devices).Select(async device =>
        {
            try
            {
                await foreach (var at in pacing.DueTimesAsync(device, clock, startTimestamp, stop.Token))
                {
                    lock (due)
                    {
                        due.Add((device, at));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The test ended the run.
            }
        }).ToArray();

        // The aggregate Readings due after n wake-ups: reading k is due at k / rate, so those at or before the clock.
        for (var wake = 1; wake <= 64; wake++)
        {
            clock.Advance(granule);
            var expected = (int)Math.Floor((granule * wake).TotalSeconds * rate) + 1;
            await WaitForAsync(() => { lock (due) { return due.Count >= expected; } });
            lock (due)
            {
                due.Count.ShouldBe(expected, $"after {wake} wake-ups of {granule.TotalMilliseconds} ms");
            }
        }

        await stop.CancelAsync();
        await Task.WhenAll(consumers);
        lock (due)
        {
            due.Select(reading => reading.At).Order().ShouldBe(Enumerable.Range(0, due.Count).Select(k => TimeSpan.FromSeconds(k / rate)), "every Reading, each at its slot");
            due.GroupBy(reading => reading.Device).Select(group => group.Count()).Max().ShouldBeLessThanOrEqualTo(due.Count / devices + 1);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        // Bounded, so a missing Reading fails the count assertion that follows instead of hanging.
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
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

    // A clock that moves only when told and wakes every timer that is due at once, as a coarse OS timer does.
    private sealed class CoarseClock : TimeProvider
    {
        private readonly List<CoarseTimer> _timers = [];
        private long _now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _now);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new CoarseTimer(callback, state, GetTimestamp() + dueTime.Ticks);
            lock (_timers)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        public void Advance(TimeSpan by)
        {
            var now = Interlocked.Add(ref _now, by.Ticks);
            CoarseTimer[] due;
            lock (_timers)
            {
                due = [.. _timers.Where(timer => timer.DueTimestamp <= now)];
                _timers.RemoveAll(timer => timer.DueTimestamp <= now);
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        // One-shot only: Task.Delay never repeats a timer.
        private sealed class CoarseTimer(TimerCallback callback, object? state, long dueTimestamp) : ITimer
        {
            public long DueTimestamp => dueTimestamp;

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
