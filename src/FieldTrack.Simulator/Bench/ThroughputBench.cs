namespace FieldTrack.Simulator.Bench;

/// <summary>
/// The throughput benchmarks (spec #21). <b>Sustained</b>: a paced rate held for the run, passing when ingest latency p95 stays under
/// <see cref="P95LimitMs"/>, the broker's backlog stays flat and every Reading is stored. <b>Flood</b>: as fast as the broker acknowledges,
/// reporting the highest rate ingest absorbed; it passes when nothing is lost.
/// </summary>
internal static class ThroughputBench
{
    /// <summary>The ingest latency p95 a sustained run must stay under, in milliseconds.</summary>
    public const double P95LimitMs = 200;

    // One ingest batch: a backlog that ends within this of where it started has not grown.
    private const double FlatBacklogTolerance = 1_000;

    /// <summary>Holds <paramref name="rate"/> Readings per second for <paramref name="duration"/> and writes the result file.</summary>
    public static async Task<BenchOutcome> SustainedAsync(
        FleetFile fleet, BenchTarget target, double rate, TimeSpan duration, TextWriter log, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var run = await BenchRun.RunAsync(fleet, target, "throughput", rate, duration, during: null, log, cancellationToken);

        var offset = await target.Database.ClockOffsetAsync(cancellationToken);
        var latency = await target.Database.LatencyAsync(run.Manifest.StartedAt, run.Manifest.EndedAt, offset, cancellationToken);
        var score = await target.Database.ScoreAsync(run.Manifest, cancellationToken);
        var flat = run.Backlog.Count >= 3 && BenchStats.IsFlat(run.Backlog, FlatBacklogTolerance);
        var pass = latency is { P95Ms: < P95LimitMs } && flat && score.Missing == 0;

        return await target.WriteResultAsync(
            "throughput",
            started,
            new { devices = fleet.Devices.Count, targetRatePerSecond = rate, durationSeconds = duration.TotalSeconds, p95LimitMs = P95LimitMs },
            new
            {
                published = run.Published,
                achievedRatePerSecond = run.Published / run.Elapsed.TotalSeconds,
                stored = score.Stored,
                missing = score.Missing,
                storedDuplicates = score.StoredDuplicates,
                drainSeconds = run.DrainSeconds,
                ingestLatencyMs = latency,
                clockOffsetMs = offset.TotalMilliseconds,
                backlog = new
                {
                    flat,
                    samples = run.Backlog.Count,
                    max = run.Backlog.DefaultIfEmpty().Max(),
                    firstThirdMedian = Third(run.Backlog, last: false),
                    lastThirdMedian = Third(run.Backlog, last: true),
                    tolerance = FlatBacklogTolerance,
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

    private static double? Third(IReadOnlyList<double> samples, bool last) =>
        samples.Count < 3 ? null : BenchStats.Median([.. last ? samples.Skip(samples.Count - (samples.Count / 3)) : samples.Take(samples.Count / 3)]);
}
