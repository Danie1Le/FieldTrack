namespace FieldTrack.Simulator.Bench;

/// <summary>
/// The Hysteresis A/B (spec #21, story 36): two Threshold rules on every Asset's engine temperature, one with Hysteresis 0 and one with the real
/// Hysteresis, evaluate the very same noisy Readings of one seeded Scenario, so the two arms see identical input. A false Alert is a Threshold
/// Alert raised outside every Fault window of its Device in the Manifest. The result reports both counts and the percentage the Hysteresis removed.
/// </summary>
internal static class HysteresisBench
{
    /// <summary>Runs <paramref name="scenario"/> over <paramref name="fleet"/> (seeded, with noise on the engine temperature) and writes the result file.</summary>
    public static async Task<BenchOutcome> RunAsync(
        FleetFile fleet, BenchTarget target, FieldTrackApi api, Scenario scenario, double threshold, double hysteresis, double rate, TimeSpan duration, TextWriter log,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var assets = await AlertBenchSupport.AssetsBySerialAsync(api, fleet, cancellationToken);
        var without = await AlertBenchSupport.CreateThresholdRulesAsync(api, assets.Values, "Bench hysteresis 0", threshold, 0, cancellationToken);
        var with = await AlertBenchSupport.CreateThresholdRulesAsync(api, assets.Values, "Bench hysteresis", threshold, hysteresis, cancellationToken);
        var run = await BenchRun.RunAsync(fleet, target, "hysteresis", rate, duration, during: null, log, cancellationToken, scenario);
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
            // The experiment means something only when noise made the 0-Hysteresis arm raise false Alerts, and the Hysteresis must not add any.
            pass: score.Missing == 0 && plain.FalseAlerts > 0 && real.FalseAlerts <= plain.FalseAlerts,
            cancellationToken);

        HysteresisArm Arm(IEnumerable<Guid> ruleIds, double arm)
        {
            var mine = alerts.Where(alert => ruleIds.Contains(alert.RuleId)).Select(alert => (Serial: serials[alert.AssetId], alert.RaisedAt)).ToList();
            var falseAlerts = HysteresisScore.FalseAlerts(mine, faults);
            return new(arm, mine.Count, falseAlerts, mine.Count - falseAlerts);
        }
    }

    private sealed record HysteresisArm(double HysteresisC, int Alerts, int FalseAlerts, int AlertsInFaultWindows);
}
