using Npgsql;

namespace FieldTrack.Simulator.Bench;

/// <summary>An Alert row as the alert benchmarks read it from the database.</summary>
/// <param name="RuleId">The Alert Rule that raised it.</param>
/// <param name="AssetId">The Asset it is about.</param>
/// <param name="RaisedAt">Device time of the Reading that raised it; the sweep time for an Offline Alert.</param>
/// <param name="CreatedAt">Platform time it was created.</param>
internal sealed record AlertRow(Guid RuleId, Guid AssetId, DateTimeOffset RaisedAt, DateTimeOffset CreatedAt);

/// <summary>
/// What the alert benchmarks share: the Alert Rules they create through the API for the fleet's Assets (one per Asset and kind) and the
/// reads of what the platform then stored. Alerts are read from the database, not <c>GET /alerts</c>, which returns only the newest 500.
/// </summary>
internal static class AlertBenchSupport
{
    /// <summary>The Asset of each Device of <paramref name="fleet"/>, by serial number; the fleet must be seeded.</summary>
    public static async Task<IReadOnlyDictionary<string, Guid>> AssetsBySerialAsync(FieldTrackApi api, FleetFile fleet, CancellationToken cancellationToken)
    {
        var assets = await api.ListAssetsAsync(cancellationToken);
        return fleet.Devices.ToDictionary(
            device => device.Serial,
            device => assets.FirstOrDefault(asset => asset.Name == device.AssetName)?.Id ?? throw new SimulatorException($"Asset '{device.AssetName}' not found: seed the fleet first."));
    }

    /// <summary>Creates, for every Asset, a Critical Threshold rule on the engine temperature that breaches above <paramref name="threshold"/>.</summary>
    /// <returns>The rule of each Asset.</returns>
    public static async Task<IReadOnlyDictionary<Guid, Guid>> CreateThresholdRulesAsync(
        FieldTrackApi api, IEnumerable<Guid> assetIds, string name, double threshold, double hysteresis, CancellationToken cancellationToken)
    {
        var rules = new Dictionary<Guid, Guid>();
        foreach (var assetId in assetIds)
        {
            rules[assetId] = (await api.CreateAlertRuleAsync(
                new { name, kind = "Threshold", assetId, severity = "Critical", sensor = "EngineTempC", @operator = "GreaterThan", threshold, hysteresis }, cancellationToken)).Id;
        }

        return rules;
    }

    /// <summary>Creates, for every Asset, a Warning Offline rule that raises after <paramref name="seconds"/> of silence.</summary>
    /// <returns>The rule of each Asset.</returns>
    public static async Task<IReadOnlyDictionary<Guid, Guid>> CreateOfflineRulesAsync(
        FieldTrackApi api, IEnumerable<Guid> assetIds, int seconds, CancellationToken cancellationToken)
    {
        var rules = new Dictionary<Guid, Guid>();
        foreach (var assetId in assetIds)
        {
            rules[assetId] = (await api.CreateAlertRuleAsync(
                new { name = $"Bench offline {seconds} s", kind = "Offline", assetId, severity = "Warning", offlineSeconds = seconds }, cancellationToken)).Id;
        }

        return rules;
    }

    /// <summary>Every Alert the rules <paramref name="ruleIds"/> have raised.</summary>
    public static async Task<IReadOnlyList<AlertRow>> AlertsAsync(NpgsqlDataSource dataSource, IEnumerable<Guid> ruleIds, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT alert_rule_id, asset_id, raised_at, created_at FROM alerts WHERE alert_rule_id = ANY(@ids)");
        command.Parameters.AddWithValue("ids", ruleIds.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var alerts = new List<AlertRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            alerts.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return alerts;
    }

    /// <summary>
    /// The platform time of the last Reading <paramref name="serial"/> had received before <paramref name="before"/>, looking back no
    /// further than <paramref name="since"/> in Device time; null when it received none. A Coverage gap's kept Readings carry Device times inside
    /// the gap but arrive after it, so they are not in this answer for a <paramref name="before"/> at the gap's end.
    /// </summary>
    public static async Task<DateTimeOffset?> LastReceivedAsync(
        NpgsqlDataSource dataSource, string serial, DateTimeOffset before, DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT max(r.received_time)
            FROM readings r JOIN devices d ON d.id = r.device_id
            WHERE d.serial_number = @serial AND r.received_time < @before AND r.device_time >= @since
            """);
        command.Parameters.AddWithValue("serial", serial);
        command.Parameters.AddWithValue("before", before);
        command.Parameters.AddWithValue("since", since);
        return await command.ExecuteScalarAsync(cancellationToken) is DateTime last ? new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc)) : null;
    }
}
