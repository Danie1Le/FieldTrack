using System.Diagnostics;

namespace FieldTrack.Simulator.Bench;

/// <summary>
/// The crash-loss benchmark (spec #21, ADR 0004): a paced run during which the ingest worker is killed at random moments, scored against the
/// Manifest and the broker's own received count. It passes with 0 missing Readings and 0 stored duplicates, and the broker's count agreeing: it
/// received at least as many PUBLISHes as Readings were stored.
/// </summary>
internal static class CrashBench
{
    /// <summary>
    /// Publishes <paramref name="rate"/> Readings per second for <paramref name="duration"/> and calls <paramref name="killIngest"/> at each offset in
    /// <paramref name="kills"/> (see <see cref="CrashSchedule"/>); it must return once ingest is back up. Writes the result file.
    /// </summary>
    public static async Task<BenchOutcome> RunAsync(
        FleetFile fleet, BenchTarget target, double rate, TimeSpan duration, IReadOnlyList<TimeSpan> kills, Func<CancellationToken, Task> killIngest,
        TextWriter log, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var hits = new List<object>();
        var run = await BenchRun.RunAsync(fleet, target, "crash", rate, duration, stop => KillAsync(stop), log, cancellationToken);
        var score = await target.Database.ScoreAsync(run.Manifest, cancellationToken);
        // The broker received every stored Reading, and also each copy a Device re-sent after a PUBACK timed out, which ingest dropped
        // as an identical duplicate (ADR 0003). So received - stored is the copies dropped, never negative; negative means the Manifest or the counter is wrong.
        var copiesDropped = run.BrokerReceived - score.Stored;

        return await target.WriteResultAsync(
            "crash",
            started,
            new { devices = fleet.Devices.Count, ratePerSecond = rate, durationSeconds = duration.TotalSeconds, kills = kills.Count, killSignal = "SIGKILL" },
            new
            {
                readingsInDatabaseAtStart = run.ReadingsAtStart,
                published = run.Published,
                brokerReceived = run.BrokerReceived,
                stored = score.Stored,
                missing = score.Missing,
                storedDuplicates = score.StoredDuplicates,
                copiesDroppedByIngest = copiesDropped,
                drainSeconds = run.DrainSeconds,
                peakBacklog = run.Backlog.DefaultIfEmpty().Max(),
                kills = hits,
            },
            pass: score.Missing == 0 && score.StoredDuplicates == 0 && copiesDropped >= 0 && run.DrainSeconds is not null,
            cancellationToken);

        async Task KillAsync(CancellationToken stop)
        {
            var clock = Stopwatch.StartNew();
            try
            {
                foreach (var planned in kills)
                {
                    await Task.Delay(planned - clock.Elapsed is { Ticks: > 0 } wait ? wait : TimeSpan.Zero, stop);
                    var killedAt = clock.Elapsed;
                    await killIngest(stop);
                    hits.Add(new { plannedSeconds = planned.TotalSeconds, killedAtSeconds = killedAt.TotalSeconds, backUpAfterSeconds = (clock.Elapsed - killedAt).TotalSeconds });
                    await log.WriteLineAsync($"killed ingest at {killedAt.TotalSeconds:0} s, back up after {(clock.Elapsed - killedAt).TotalSeconds:0.0} s");
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // The run ended before the schedule did.
            }
        }
    }
}
