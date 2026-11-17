namespace FieldTrack.Simulator.Bench;

/// <summary>
/// The throughput benchmarks (spec #21). <b>Sustained</b>: a paced rate held for the run, passing when the fleet achieved the rate
/// (within <see cref="RateTolerance"/>), ingest latency p95 stays under <see cref="P95LimitMs"/> (a lower bound, see
/// <see cref="BenchDatabase.LatencyAsync"/>), the broker's backlog stays flat and every Reading is stored. <b>Flood</b>: as fast as the broker acknowledges,
/// reporting the highest rate ingest absorbed; it passes when nothing is lost.
/// </summary>
internal static class ThroughputBench
{
    /// <summary>The ingest latency p95 a sustained run must stay under, in milliseconds.</summary>
    public const double P95LimitMs = 200;

    /// <summary>How far below its target a run's achieved rate may fall and still count as holding it. Publishers wait for each PUBACK, so an overloaded stack lowers the rate quietly.</summary>
    public const double RateTolerance = 0.02;

    // A backlog that ends within this many seconds of the target rate (1,000 messages at 2,000/s) of where it started has not grown.
    private const double FlatBacklogSeconds = 0.5;

    /// <summary>Holds <paramref name="rate"/> Readings per second for <paramref name="duration"/> and writes the result file.</summary>
    public static async Task<BenchOutcome> SustainedAsync(
        FleetFile fleet, BenchTarget target, double rate, TimeSpan duration, TextWriter log, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var run = await BenchRun.RunAsync(fleet, target, "throughput", rate, duration, during: null, log, cancellationToken);

        var latency = await target.Database.LatencyAsync(run.Manifest.StartedAt, run.Manifest.EndedAt, run.ClockOffsets.Mean, cancellationToken);
        var score = await target.Database.ScoreAsync(run.Manifest, cancellationToken);
        var tolerance = rate * FlatBacklogSeconds;
        var flat = run.Backlog.Count >= 3 && BenchStats.IsFlat(run.Backlog, tolerance);
        var achieved = run.Published / run.Elapsed.TotalSeconds;
        var rateAchieved = HoldsRate(achieved, rate);
        var pass = rateAchieved && latency is { P95Ms: < P95LimitMs } && flat && score.Missing == 0;

        return await target.WriteResultAsync(
            "throughput",
            started,
            new { devices = fleet.Devices.Count, targetRatePerSecond = rate, rateTolerance = RateTolerance, durationSeconds = duration.TotalSeconds, p95LimitMs = P95LimitMs },
            new
            {
                readingsInDatabaseAtStart = run.ReadingsAtStart,
                published = run.Published,
                achievedRatePerSecond = achieved,
                rateAchieved,
                stored = score.Stored,
                missing = score.Missing,
                storedDuplicates = score.StoredDuplicates,
                drainSeconds = run.DrainSeconds,
                ingestLatencyMs = latency,
                // Commit start minus tick: excludes the WAL flush (a lower bound under stalls) and includes publisher delay before the publish.
                latencyBasis = "tick to commit timestamp; lower bound under WAL stalls",
                // Tick to the broker's PUBACK, which follows commit and flush: the latency a Device sees, harness delay included.
                tickToPubackMs = LatencySummary.Of(run.TickToPubackMs),
                clockOffsetMs = new
                {
                    before = run.ClockOffsets.Before.TotalMilliseconds,
                    after = run.ClockOffsets.After.TotalMilliseconds,
                    used = run.ClockOffsets.Mean.TotalMilliseconds,
                    drifted = run.ClockOffsets.Drifted,
                },
                backlog = new
                {
                    flat,
                    samples = run.Backlog.Count,
                    max = run.Backlog.DefaultIfEmpty().Max(),
                    firstThirdMedian = Third(run.Backlog, last: false),
                    lastThirdMedian = Third(run.Backlog, last: true),
                    tolerance,
                },
            },
            pass,
            cancellationToken);
    }

    /// <summary>Publishes flat out for <paramref name="duration"/> and writes the result file.</summary>
    public static async Task<BenchOutcome> FloodAsync(
        FleetFile fleet, BenchTarget target, TimeSpan duration, TextWriter log, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var run = await BenchRun.RunAsync(fleet, target, "flood", rate: null, duration, during: null, log, cancellationToken);
        var score = await target.Database.ScoreAsync(run.Manifest, cancellationToken);

        return await target.WriteResultAsync(
            "flood",
            started,
            new { devices = fleet.Devices.Count, durationSeconds = duration.TotalSeconds },
            new
            {
                readingsInDatabaseAtStart = run.ReadingsAtStart,
                published = run.Published,
                publishedPerSecond = run.Published / run.Elapsed.TotalSeconds,
                // Stored over the run plus the time ingest needed to catch up: the rate ingest absorbed end to end.
                ingestedPerSecond = score.Stored / (run.Elapsed.TotalSeconds + (run.DrainSeconds ?? 0)),
                stored = score.Stored,
                missing = score.Missing,
                storedDuplicates = score.StoredDuplicates,
                drainSeconds = run.DrainSeconds,
                peakBacklog = run.Backlog.DefaultIfEmpty().Max(),
            },
            pass: score.Missing == 0 && score.StoredDuplicates == 0,
            cancellationToken);
    }

    /// <summary>Whether a run that published <paramref name="achieved"/> Readings per second held <paramref name="target"/>, within <see cref="RateTolerance"/>.</summary>
    internal static bool HoldsRate(double achieved, double target) => achieved >= target * (1 - RateTolerance);

    private static double? Third(IReadOnlyList<double> samples, bool last) =>
        samples.Count < 3 ? null : BenchStats.Median([.. last ? samples.Skip(samples.Count - (samples.Count / 3)) : samples.Take(samples.Count / 3)]);
}
