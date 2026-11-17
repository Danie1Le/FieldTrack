namespace FieldTrack.Simulator.Bench;

/// <summary>
/// The offline accuracy benchmark (spec #21, story 39): a Scenario of long and short Power offs and Coverage gaps against an Offline rule. A long
/// dropout must raise an Alert whose <c>CreatedAt</c> is within the last Received time plus the rule's seconds plus 2 s; an Alert without a matching
/// long dropout is a false alarm (see <see cref="OfflineScore"/>). Alerts from before the run, or raised after it when the Devices fall silent at
/// its end, are not scored.
/// </summary>
internal static class OfflineBench
{
    // The last Received time is searched for this far back in Device time; the Scenario's Readings come at least this often.
    private static readonly TimeSpan _lookBack = TimeSpan.FromMinutes(2);

    /// <summary>Runs <paramref name="scenario"/> over <paramref name="fleet"/> (seeded) at <paramref name="rate"/> and writes the result file.</summary>
    public static async Task<BenchOutcome> RunAsync(
        FleetFile fleet, BenchTarget target, FieldTrackApi api, Scenario scenario, int offlineSeconds, double rate, TimeSpan duration, TextWriter log,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var assets = await AlertBenchSupport.AssetsBySerialAsync(api, fleet, cancellationToken);
        var rules = await AlertBenchSupport.CreateOfflineRulesAsync(api, assets.Values, offlineSeconds, cancellationToken);
        var run = await BenchRun.RunAsync(fleet, target, "offline", rate, duration, during: null, log, cancellationToken, scenario);
        var score = await target.Database.ScoreAsync(run.Manifest, cancellationToken);

        // Alert times are the platform's; the Manifest's are this machine's.
        var offset = run.ClockOffsets.Mean;
        var serials = assets.ToDictionary(pair => pair.Value, pair => pair.Key);
        var alerts = (await AlertBenchSupport.AlertsAsync(target.Database.DataSource, rules.Values, cancellationToken))
            .Select(alert => (Serial: serials[alert.AssetId], CreatedAt: alert.CreatedAt - offset))
            .Where(alert => alert.CreatedAt >= run.Manifest.StartedAt && alert.CreatedAt <= run.Manifest.EndedAt)
            .ToList();

        var dropouts = new List<Dropout>();
        foreach (var device in run.Manifest.Devices)
        {
            foreach (var window in device.Windows.Where(window => window.Kind is InjectionKind.PowerOff or InjectionKind.CoverageGap))
            {
                var last = await AlertBenchSupport.LastReceivedAsync(target.Database.DataSource, device.Serial, window.End + offset, window.Start - _lookBack, cancellationToken);
                dropouts.Add(new(device.Serial, window.Start, window.End, last is { } received ? received - offset : window.Start));
            }
        }

        var offline = OfflineScore.Of(dropouts, alerts, TimeSpan.FromSeconds(offlineSeconds));
        return await target.WriteResultAsync(
            "offline",
            started,
            new
            {
                devices = fleet.Devices.Count,
                offlineSeconds,
                ratePerSecond = rate,
                durationSeconds = duration.TotalSeconds,
                toleranceSeconds = OfflineScore.Tolerance.TotalSeconds,
                longDropoutMinimumSeconds = offlineSeconds + OfflineScore.LongMargin.TotalSeconds,
            },
            new
            {
                dropouts = dropouts.Count,
                shortDropouts = dropouts.Count - offline.LongDropouts,
                longDropouts = offline.LongDropouts,
                offline.Detected,
                offline.Late,
                offline.Missed,
                offline.FalseAlarms,
                alertsScored = alerts.Count,
                published = run.Published,
                missing = score.Missing,
                clockOffsetMs = offset.TotalMilliseconds,
            },
            pass: offline.LongDropouts > 0 && offline.Detected == offline.LongDropouts && offline.FalseAlarms == 0 && score.Missing == 0,
            cancellationToken);
    }
}
