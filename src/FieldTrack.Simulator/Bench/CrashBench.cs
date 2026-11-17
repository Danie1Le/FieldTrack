using System.Diagnostics;

namespace FieldTrack.Simulator.Bench;

/// <summary>
/// The crash-loss benchmark (spec #21, ADR 0004): a paced run during which the ingest worker is killed at random moments, scored against the
/// Manifest and the broker's own received count. It passes with 0 missing Readings and 0 stored duplicates, the broker's count agreeing (it
/// received at least as many PUBLISHes as Readings were stored), and every kill having taken the measured stack down: a run whose kills
/// did not crash anything proves nothing. A kill that fails ends the run at once and still writes a result, with <c>pass: false</c> and the kills so far.
/// </summary>
internal static class CrashBench
{
    /// <summary>
    /// Publishes <paramref name="rate"/> Readings per second for <paramref name="duration"/> and calls <paramref name="killIngest"/> at each offset in
    /// <paramref name="kills"/> (see <see cref="CrashSchedule"/>); it must return once ingest is back up. Writes the result file.
    /// </summary>
    public static async Task<BenchOutcome> RunAsync(
        FleetFile fleet, BenchTarget target, double rate, TimeSpan duration, IReadOnlyList<TimeSpan> kills, Func<CancellationToken, Task<KillResult>> killIngest,
        TextWriter log, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var hits = new List<object>();
        var allWentDown = true;
        var run = await BenchRun.RunAsync(fleet, target, "crash", rate, duration, stop => KillAsync(stop), log, cancellationToken);
        var score = await target.Database.ScoreAsync(run.Manifest, cancellationToken);
        // The broker received every stored Reading, and also each copy a Device re-sent after a PUBACK timed out, which ingest dropped
        // as an identical duplicate (ADR 0003). So received - stored is the copies dropped, never negative; negative means the Manifest or the counter is wrong.
        var retryCopies = run.BrokerReceived - score.Stored;
        var crashed = hits.Count == kills.Count && allWentDown && run.SideTaskFailure is null;

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
                // Only the copies a publisher re-sent after a PUBACK timeout. A message the broker redelivers after a kill (ADR 0004) is not in the broker's
                // received count, and ingest's own duplicate counter is not readable from outside the process (#51), so a kill that landed between commit
                // and PUBACK is not visible here.
                publisherRetryCopies = retryCopies,
                redeliveriesCounted = false,
                drainSeconds = run.DrainSeconds,
                peakBacklog = run.Backlog.DefaultIfEmpty().Max(),
                allKillsCrashedTheStack = crashed,
                killFailure = run.SideTaskFailure,
                kills = hits,
            },
            pass: crashed && score.Missing == 0 && score.StoredDuplicates == 0 && retryCopies >= 0 && run.DrainSeconds is not null,
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
                    var result = await killIngest(stop);
                    hits.Add(new
                    {
                        plannedSeconds = planned.TotalSeconds,
                        killedAtSeconds = killedAt.TotalSeconds,
                        container = result.Container,
                        healthWentDown = result.WentDown,
                        backUpAfterSeconds = result.BackUpAfter.TotalSeconds,
                    });
                    await log.WriteLineAsync($"killed ingest at {killedAt.TotalSeconds:0} s, back up after {result.BackUpAfter.TotalSeconds:0.0} s");
                    if (!result.WentDown)
                    {
                        allWentDown = false;
                        throw new SimulatorException($"Killing container {result.Container} did not take /health down at {killedAt.TotalSeconds:0} s: it is not the stack being measured.");
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // The run ended before the schedule did.
            }
        }
    }
}
