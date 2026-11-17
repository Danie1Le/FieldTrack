namespace FieldTrack.Simulator.Bench;

/// <summary>
/// The Hysteresis A/B (spec #21, story 36): two Threshold rules on every Asset's engine temperature, one with Hysteresis 0 and one with the real
/// Hysteresis, evaluate the very same noisy Readings of one seeded Scenario, so the two arms see identical input. A false Alert is a Threshold
/// Alert raised outside every Fault window of its Device in the Manifest. The result reports both counts, the Fault windows each arm still caught
/// and the percentage the Hysteresis removed. The noise is seeded, so the same <c>seed</c> draws the same Readings again; the result records it.
/// </summary>
internal static class HysteresisBench
{
    /// <summary>Runs <paramref name="scenario"/> over <paramref name="fleet"/> (noise on the engine temperature drawn from <paramref name="seed"/>) and writes the result file.</summary>
    public static async Task<BenchOutcome> RunAsync(
        FleetFile fleet, BenchTarget target, FieldTrackApi api, Scenario scenario, double threshold, double hysteresis, int seed, double rate, TimeSpan duration, TextWriter log,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var assets = await AlertBenchSupport.AssetsBySerialAsync(api, fleet, cancellationToken);
        var without = await AlertBenchSupport.CreateThresholdRulesAsync(api, assets.Values, "Bench hysteresis 0", threshold, 0, cancellationToken);
        var with = await AlertBenchSupport.CreateThresholdRulesAsync(api, assets.Values, "Bench hysteresis", threshold, hysteresis, cancellationToken);
        var run = await BenchRun.RunAsync(fleet, target, "hysteresis", rate, duration, during: null, log, cancellationToken, scenario, noiseSeed: seed);
        var score = await target.Database.ScoreAsync(run.Manifest, cancellationToken);

        var serials = assets.ToDictionary(pair => pair.Value, pair => pair.Key);
        var faults = run.Manifest.Devices.ToDictionary(
            device => device.Serial,
            device => (IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)>)[.. device.Windows
                .Where(window => window.Kind is InjectionKind.Overheat or InjectionKind.FuelLeak)
                .Select(window => (window.Start, window.End))]);
        var alerts = await AlertBenchSupport.AlertsAsync(target.Database.DataSource, [.. without.Values, .. with.Values], cancellationToken);

        var plain = Arm(without.Values, 0);
        var real = Arm(with.Values, hysteresis);
        var reduction = HysteresisScore.ReductionPercent(plain.FalseAlerts, real.FalseAlerts);
        return await target.WriteResultAsync(
            "hysteresis",
            started,
            new
            {
                devices = fleet.Devices.Count,
                thresholdC = threshold,
                hysteresisC = hysteresis,
                seed,
                boots = run.Manifest.Devices.ToDictionary(device => device.Serial, device => device.Ranges.Select(range => range.Boot)),
                ratePerSecond = rate,
                durationSeconds = duration.TotalSeconds,
                faultWindows = faults.Values.Sum(windows => windows.Count),
            },
            new
            {
                published = run.Published,
                missing = score.Missing,
                withoutHysteresis = plain,
                withHysteresis = real,
                falseAlertReductionPercent = reduction,
            },
            // The experiment means something only when noise made the 0-Hysteresis arm raise false Alerts; the Hysteresis must not add any
            // and must not lose a real one (a rule that never raises would otherwise pass).
            pass: score.Missing == 0 && plain.FalseAlerts > 0 && real.FalseAlerts <= plain.FalseAlerts && real.FaultWindowsCaught >= plain.FaultWindowsCaught,
            cancellationToken);

        HysteresisArm Arm(IEnumerable<Guid> ruleIds, double arm)
        {
            var mine = alerts.Where(alert => ruleIds.Contains(alert.RuleId)).Select(alert => (Serial: serials[alert.AssetId], alert.RaisedAt, alert.ResolvedAt)).ToList();
            var falseAlerts = HysteresisScore.FalseAlerts(mine.Select(alert => (alert.Serial, alert.RaisedAt)), faults);
            return new(arm, mine.Count, falseAlerts, mine.Count - falseAlerts, HysteresisScore.FaultWindowsCaught(mine, faults));
        }
    }

    private sealed record HysteresisArm(double HysteresisC, int Alerts, int FalseAlerts, int AlertsInFaultWindows, int FaultWindowsCaught);
}
