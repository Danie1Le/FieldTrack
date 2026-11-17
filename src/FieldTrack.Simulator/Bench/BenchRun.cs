namespace FieldTrack.Simulator.Bench;

/// <summary>What the bench saw of one simulator run.</summary>
/// <param name="Manifest">What the simulator published.</param>
/// <param name="Backlog">The broker's backlog, one sample a second while the run lasted.</param>
/// <param name="BrokerReceived">PUBLISH messages the broker received over the run, from its own counter.</param>
/// <param name="DrainSeconds">How long after the run ended until every listed Reading was stored; null if some never were.</param>
/// <param name="ReadingsAtStart">How many Readings the database held before the run.</param>
/// <param name="ClockOffsets">The database clock's offset from this machine's before and after the run.</param>
/// <param name="TickToPubackMs">Per Reading, milliseconds from the Device's tick to the broker's PUBACK, which ingest sends only after commit and WAL flush.</param>
/// <param name="SideTaskFailure">Why the task that ran beside the publishing failed (which also ended the publishing early), or null.</param>
internal sealed record MeasuredRun(
    RunManifest Manifest, IReadOnlyList<double> Backlog, long BrokerReceived, double? DrainSeconds, long ReadingsAtStart, ClockOffsets ClockOffsets, IReadOnlyList<double> TickToPubackMs,
    string? SideTaskFailure = null)
{
    /// <summary>Readings the Manifest lists.</summary>
    public long Published => Manifest.Published;

    /// <summary>The run's length.</summary>
    public TimeSpan Elapsed => Manifest.EndedAt - Manifest.StartedAt;
}

/// <summary>
/// Runs the simulator the way every benchmark needs it: a fleet publishing at a rate (or flat out) for a duration, while the bench samples the
/// broker's backlog, followed by a wait for ingest to store what was published. Each benchmark then judges the <see cref="MeasuredRun"/> by its own bar.
/// </summary>
internal static class BenchRun
{
    // Ingest may owe a long backlog; it is given up on only when it stops storing for this long.
    private static readonly TimeSpan _stallTimeout = TimeSpan.FromMinutes(1);

    // The drain poll counts the whole window, which competes with ingest: poll often at first, then back off to this.
    private static readonly TimeSpan _slowestPoll = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Publishes <paramref name="rate"/> Readings per second (flat out when null) for <paramref name="duration"/> and waits for them to be stored.
    /// <paramref name="during"/>, if given, runs beside the publishing and is cancelled when it ends (the crash benchmark kills ingest there);
    /// if it fails, the publishing stops at once (there is nothing left to measure) and the failure is in <see cref="MeasuredRun.SideTaskFailure"/>.
    /// </summary>
    public static async Task<MeasuredRun> RunAsync(
        FleetFile fleet, BenchTarget target, string bench, double? rate, TimeSpan duration, Func<CancellationToken, Task>? during, TextWriter log, CancellationToken cancellationToken,
        Scenario? scenario = null, int? noiseSeed = null)
    {
        await using var counters = await BrokerCounters.ConnectAsync(target.BrokerHost, target.BrokerPort, cancellationToken);
        await BrokerCounters.SettleAsync(cancellationToken);
        var receivedBefore = counters.PublishesReceived;
        var readingsAtStart = await target.Database.CountAllReadingsAsync(cancellationToken);
        var offsetBefore = await target.Database.ClockOffsetAsync(cancellationToken);

        using var publishing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var backlog = new List<double>();
        var sampling = SampleBacklogAsync(counters, backlog, log, publishing.Token);
        var alongside = AlongsideAsync(during, publishing);

        var tickToPuback = new List<double>();
        var options = new RunOptions(
            target.BrokerHost, target.BrokerPort, rate, duration, target.StatePath, target.PathFor("manifest-", bench, DateTimeOffset.UtcNow),
            scenario,
            Acknowledged: wait =>
            {
                lock (tickToPuback)
                {
                    tickToPuback.Add(wait.TotalMilliseconds);
                }
            },
            NoiseSeed: noiseSeed);
        RunManifest manifest;
        try
        {
            manifest = await FleetRunner.RunAsync(fleet, options, log, TimeProvider.System, publishing.Token);
        }
        finally
        {
            await publishing.CancelAsync();
            await sampling;
        }

        // A failure of the side task (a container that would not restart) must fail the benchmark, not pass quietly: the caller judges it.
        var sideTaskFailure = await alongside;
        var offsetAfter = await target.Database.ClockOffsetAsync(cancellationToken);
        var drain = await WaitForStoredAsync(target.Database, manifest, cancellationToken);
        await BrokerCounters.SettleAsync(cancellationToken);
        return new MeasuredRun(manifest, backlog, counters.PublishesReceived - receivedBefore, drain, readingsAtStart, new ClockOffsets(offsetBefore, offsetAfter), tickToPuback, sideTaskFailure);
    }

    private static async Task<string?> AlongsideAsync(Func<CancellationToken, Task>? during, CancellationTokenSource publishing)
    {
        if (during is null)
        {
            return null;
        }

        try
        {
            await during(publishing.Token);
            return null;
        }
        catch (OperationCanceledException) when (publishing.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            await publishing.CancelAsync();
            return exception.Message;
        }
    }

    private static async Task SampleBacklogAsync(BrokerCounters counters, List<double> samples, TextWriter log, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                samples.Add(counters.Backlog);
                if (samples.Count % 30 == 0)
                {
                    await log.WriteLineAsync($"{samples.Count} s: broker backlog {samples[^1]}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The run ended; the samples are complete.
        }
    }

    private static async Task<double?> WaitForStoredAsync(BenchDatabase database, RunManifest manifest, CancellationToken cancellationToken)
    {
        var published = manifest.Published;
        var started = DateTimeOffset.UtcNow;
        var (stored, progressedAt) = (-1L, started);
        var poll = TimeSpan.FromSeconds(1);
        while (DateTimeOffset.UtcNow - progressedAt < _stallTimeout)
        {
            var now = await database.CountReadingsAsync(manifest.StartedAt - BenchDatabase.Padding, manifest.EndedAt + BenchDatabase.Padding, cancellationToken);
            if (now >= published)
            {
                return (DateTimeOffset.UtcNow - started).TotalSeconds;
            }

            if (now > stored)
            {
                (stored, progressedAt) = (now, DateTimeOffset.UtcNow);
            }

            await Task.Delay(poll, cancellationToken);
            poll = poll * 2 > _slowestPoll ? _slowestPoll : poll * 2;
        }

        return null;
    }
}
