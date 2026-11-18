using FieldTrack.Domain.Alerts;
using RuleSensor = FieldTrack.Domain.Alerts.Sensor;

namespace FieldTrack.Simulator;

/// <summary>
/// The Geofence and Alert Rules the demo Scenario (<c>fleets/demo-scenario.json</c>) needs so that an Overheat, a Geofence exit,
/// an Offline Device and a Coverage gap backlog show on the dashboard. Created through the REST API on the Assets of
/// <c>fleets/demo.json</c>; running it again adds only what is missing, matched by name.
/// </summary>
internal static class DemoSetup
{
    /// <summary>The Geofence Haul truck 2 starts in and drives out of about 25 s into a run.</summary>
    public const string YardName = "North yard";

    // A box of about 310 m by 220 m around the start of routes/site-loop.gpx (45.5290, -122.6830), whose first leg heads east out
    // of it. GeoJSON positions are [lon, lat].
    private static readonly double[][][] _yard =
        [[[-122.6845, 45.5280], [-122.6805, 45.5280], [-122.6805, 45.5300], [-122.6845, 45.5300], [-122.6845, 45.5280]]];

    /// <summary>
    /// Adds <see cref="YardName"/> and the demo's Alert Rules unless rules or a Geofence of those names exist. Writes one line per item
    /// to <paramref name="output"/>.
    /// </summary>
    /// <exception cref="SimulatorException">An Asset of the demo fleet is missing: seed <c>fleets/demo.json</c> first.</exception>
    public static async Task ApplyAsync(FieldTrackApi api, TextWriter output, CancellationToken cancellationToken)
    {
        var assets = await api.ListAssetsAsync(cancellationToken);
        Guid AssetId(string name) =>
            assets.FirstOrDefault(asset => asset.Name == name)?.Id ?? throw new SimulatorException($"No Asset named '{name}': seed fleets/demo.json first.");
        var (truck1, truck2, generator) = (AssetId("Haul truck 1"), AssetId("Haul truck 2"), AssetId("Generator 1"));

        var yard = (await api.ListGeofencesAsync(cancellationToken)).FirstOrDefault(geofence => geofence.Name == YardName);
        output.WriteLine($"{YardName}: {(yard is null ? "created" : "found")}");
        yard ??= await api.CreateGeofenceAsync(new { name = YardName, kind = "Inclusion", area = new { type = "Polygon", coordinates = _yard } }, cancellationToken);

        DemoRule[] rules =
        [
            Overheat("Demo: Haul truck 1 overheat", truck1),
            new($"Demo: Haul truck 2 left {YardName}", AlertRuleKind.GeofenceExit, truck2, Severity.Warning, GeofenceId: yard.Id),
            Overheat("Demo: Generator 1 overheat", generator),
            Offline("Demo: Generator 1 offline", generator),
        ];
        var existing = (await api.ListAlertRulesAsync(cancellationToken)).Select(rule => rule.Name).ToHashSet();
        foreach (var rule in rules)
        {
            var found = existing.Contains(rule.Name);
            if (!found)
            {
                await api.CreateAlertRuleAsync(rule, cancellationToken);
            }

            output.WriteLine($"{rule.Name}: {(found ? "found" : "created")}");
        }
    }

    // The Overheat Faults peak at 120 °C from a normal 85 °C; clearing needs 100 °C.
    private static DemoRule Overheat(string name, Guid assetId) =>
        new(name, AlertRuleKind.Threshold, assetId, Severity.Critical, Sensor: RuleSensor.EngineTempC, Operator: ThresholdOperator.GreaterThan, Threshold: 105, Hysteresis: 5);

    // Only Generator 1 gets one: an Active Offline Alert ranks the Asset as Alerting, so Excavator 1, without one, shows the Offline status.
    // 60 s, so the Alert is raised well inside the Coverage gap and the backlog resolves it.
    private static DemoRule Offline(string name, Guid assetId) => new(name, AlertRuleKind.Offline, assetId, Severity.Warning, OfflineSeconds: 60);

    /// <summary>
    /// The JSON of <c>POST /alert-rules</c> for one rule on one Asset; settings of other kinds stay null. Typed with the
    /// Domain enums, sent by name, so a renamed member fails the build instead of the demo.
    /// </summary>
    private sealed record DemoRule(
        string Name,
        AlertRuleKind Kind,
        Guid AssetId,
        Severity Severity,
        RuleSensor? Sensor = null,
        ThresholdOperator? Operator = null,
        double? Threshold = null,
        double? Hysteresis = null,
        Guid? GeofenceId = null,
        int? OfflineSeconds = null,
        bool Enabled = true);
}
