using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FieldTrack.IntegrationTests;

public sealed class AlertRuleTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Admin_creates_edits_lists_and_deletes_a_Threshold_rule()
    {
        using var admin = await AdminClientAsync();
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });

        var rule = await CreateAsync(admin, "/alert-rules", Overheat(assetId: asset));
        var created = await admin.GetFromJsonAsync<JsonElement>($"/alert-rules/{rule}", CancellationToken);
        using var edited = await admin.PutAsJsonAsync($"/alert-rules/{rule}", Overheat(assetId: asset) with { threshold = 110, enabled = false }, CancellationToken);
        var listed = await ListAsync(admin, "/alert-rules");
        using var deleted = await admin.DeleteAsync($"/alert-rules/{rule}", CancellationToken);

        created.GetProperty("name").GetString().ShouldBe("Engine overheat");
        created.GetProperty("kind").GetString().ShouldBe("Threshold");
        created.GetProperty("assetId").GetString().ShouldBe(asset);
        created.GetProperty("assetGroupId").ValueKind.ShouldBe(JsonValueKind.Null);
        created.GetProperty("severity").GetString().ShouldBe("Critical");
        created.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        created.GetProperty("sensor").GetString().ShouldBe("EngineTempC");
        created.GetProperty("operator").GetString().ShouldBe("GreaterThan");
        created.GetProperty("threshold").GetDouble().ShouldBe(105);
        created.GetProperty("hysteresis").GetDouble().ShouldBe(5);
        edited.StatusCode.ShouldBe(HttpStatusCode.OK);
        var only = listed.ShouldHaveSingleItem();
        only.GetProperty("threshold").GetDouble().ShouldBe(110);
        only.GetProperty("enabled").GetBoolean().ShouldBeFalse();
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ListAsync(admin, "/alert-rules")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_rule_may_target_an_Asset_Group()
    {
        using var admin = await AdminClientAsync();
        var group = await CreateAsync(admin, "/asset-groups", new { name = "North quarry" });

        var rule = await CreateAsync(admin, "/alert-rules", Overheat(assetGroupId: group));

        (await admin.GetFromJsonAsync<JsonElement>($"/alert-rules/{rule}", CancellationToken)).GetProperty("assetGroupId").GetString().ShouldBe(group);
    }

    [Fact]
    public async Task Create_refuses_a_rule_without_exactly_one_known_target_or_valid_Threshold_settings_or_with_a_value_that_is_not_one_enum_name()
    {
        using var admin = await AdminClientAsync();
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });
        var group = await CreateAsync(admin, "/asset-groups", new { name = "North quarry" });

        RuleRequest[] invalid =
        [
            Overheat(),
            Overheat(assetId: asset, assetGroupId: group),
            Overheat(assetId: Guid.NewGuid().ToString()),
            Overheat(assetGroupId: Guid.NewGuid().ToString()),
            Overheat(assetId: asset) with { sensor = null },
            Overheat(assetId: asset) with { @operator = null },
            Overheat(assetId: asset) with { threshold = null },
            Overheat(assetId: asset) with { hysteresis = null },
            Overheat(assetId: asset) with { hysteresis = -1 },
            Overheat(assetId: asset) with { kind = null },
            Overheat(assetId: asset) with { severity = null },
            Overheat(assetId: asset) with { name = "" },
            Overheat(assetId: asset) with { threshold = "NaN" },
            Overheat(assetId: asset) with { threshold = "-Infinity" },
            Overheat(assetId: asset) with { sensor = "FuelPct, BatteryV" },
            Overheat(assetId: asset) with { @operator = "GreaterThan, LessThan" },
            Overheat(assetId: asset) with { severity = "Warning, Critical" },
            Overheat(assetId: asset) with { kind = "Threshold, Threshold" },
            Overheat(assetId: asset) with { sensor = "1" },
        ];
        foreach (var request in invalid)
        {
            using var response = await admin.PostAsJsonAsync("/alert-rules", request, CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"{request}");
        }

        (await ListAsync(admin, "/alert-rules")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_rule_on_an_Archived_Asset_can_still_be_disabled_but_no_rule_can_move_onto_one()
    {
        using var admin = await AdminClientAsync();
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });
        var other = await CreateAsync(admin, "/assets", new { name = "Haul truck 8", type = "Truck" });
        var rule = await CreateAsync(admin, "/alert-rules", Overheat(assetId: asset));
        var moved = await CreateAsync(admin, "/alert-rules", Overheat(assetId: other));
        await using (var database = await OpenDatabaseAsync())
        {
            await SqlAsync(database, "UPDATE assets SET archived = true WHERE id = @p0", Guid.Parse(asset));
        }

        using var disabled = await admin.PutAsJsonAsync($"/alert-rules/{rule}", Overheat(assetId: asset) with { enabled = false }, CancellationToken);
        using var move = await admin.PutAsJsonAsync($"/alert-rules/{moved}", Overheat(assetId: asset), CancellationToken);

        disabled.StatusCode.ShouldBe(HttpStatusCode.OK);
        move.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Read_edit_and_delete_answer_404_for_an_unknown_rule()
    {
        using var admin = await AdminClientAsync();
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });

        using var get = await admin.GetAsync($"/alert-rules/{Guid.NewGuid()}", CancellationToken);
        using var edit = await admin.PutAsJsonAsync($"/alert-rules/{Guid.NewGuid()}", Overheat(assetId: asset), CancellationToken);
        using var delete = await admin.DeleteAsync($"/alert-rules/{Guid.NewGuid()}", CancellationToken);

        new[] { get, edit, delete }.ShouldAllBe(response => response.StatusCode == HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_rule_that_has_raised_Alerts_cannot_be_deleted_so_their_history_keeps_its_rule()
    {
        using var admin = await AdminClientAsync();
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });
        var rule = await CreateAsync(admin, "/alert-rules", Overheat(assetId: asset));
        await InsertAlertAsync(rule, asset, "Resolved");

        using var deleted = await admin.DeleteAsync($"/alert-rules/{rule}", CancellationToken);

        deleted.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ListAsync(admin, "/alert-rules")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Operators_read_rules_and_Alerts_but_cannot_change_rules()
    {
        using var admin = await AdminClientAsync();
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });
        var rule = await CreateAsync(admin, "/alert-rules", Overheat(assetId: asset));
        await AuthClient.CreateUserAsync(admin, "operator@fieldtrack.test", "Operator", "Operator-pass-1");
        using var client = Fixture.Api.CreateClient();
        await AuthClient.AuthorizeAsync(client, "operator@fieldtrack.test", "Operator-pass-1");

        var rules = await ListAsync(client, "/alert-rules");
        var alerts = await ListAsync(client, "/alerts");
        using var create = await client.PostAsJsonAsync("/alert-rules", Overheat(assetId: asset), CancellationToken);
        using var edit = await client.PutAsJsonAsync($"/alert-rules/{rule}", Overheat(assetId: asset), CancellationToken);
        using var delete = await client.DeleteAsync($"/alert-rules/{rule}", CancellationToken);

        rules.ShouldHaveSingleItem();
        alerts.ShouldBeEmpty();
        new[] { create, edit, delete }.ShouldAllBe(response => response.StatusCode == HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Alerts_are_listed_newest_first_filtered_by_Active_and_read_one_by_one()
    {
        using var admin = await AdminClientAsync();
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });
        var rule = await CreateAsync(admin, "/alert-rules", Overheat(assetId: asset));
        var resolved = await InsertAlertAsync(rule, asset, "Resolved", raisedAt: DateTimeOffset.UtcNow.AddHours(-2));
        var open = await InsertAlertAsync(rule, asset, "Open", raisedAt: DateTimeOffset.UtcNow.AddHours(-1));

        var all = await ListAsync(admin, "/alerts");
        var active = await ListAsync(admin, "/alerts?active=true");
        var history = await ListAsync(admin, "/alerts?active=false");
        var one = await admin.GetFromJsonAsync<JsonElement>($"/alerts/{open}", CancellationToken);
        using var unknown = await admin.GetAsync($"/alerts/{Guid.NewGuid()}", CancellationToken);

        all.Select(alert => alert.GetProperty("id").GetGuid()).ShouldBe([open, resolved]);
        active.Select(alert => alert.GetProperty("id").GetGuid()).ShouldBe([open]);
        history.Select(alert => alert.GetProperty("id").GetGuid()).ShouldBe([resolved]);
        one.GetProperty("alertRuleId").GetString().ShouldBe(rule);
        one.GetProperty("assetId").GetString().ShouldBe(asset);
        one.GetProperty("reading").GetProperty("boot").GetInt32().ShouldBe(1);
        one.GetProperty("reading").GetProperty("sequenceNumber").GetInt64().ShouldBe(7);
        one.GetProperty("severity").GetString().ShouldBe("Critical");
        one.GetProperty("message").GetString().ShouldBe("Engine temp 108.4 °C, above 105 (clears below 100)");
        one.GetProperty("status").GetString().ShouldBe("Open");
        one.GetProperty("resolvedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        one.GetProperty("resolveReason").ValueKind.ShouldBe(JsonValueKind.Null);
        one.GetProperty("fromBacklog").GetBoolean().ShouldBeFalse();
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Alerts_are_listed_for_one_Asset()
    {
        using var admin = await AdminClientAsync();
        var truck = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });
        var excavator = await CreateAsync(admin, "/assets", new { name = "Excavator 2", type = "Excavator" });
        var rule = await CreateAsync(admin, "/alert-rules", Overheat(assetId: truck));
        var otherRule = await CreateAsync(admin, "/alert-rules", Overheat(assetId: excavator));
        var resolved = await InsertAlertAsync(rule, truck, "Resolved", raisedAt: DateTimeOffset.UtcNow.AddHours(-2));
        var open = await InsertAlertAsync(rule, truck, "Open", raisedAt: DateTimeOffset.UtcNow.AddHours(-1));
        await InsertAlertAsync(otherRule, excavator, "Open");

        var all = await ListAsync(admin, $"/alerts?assetId={truck}");
        var active = await ListAsync(admin, $"/alerts?assetId={truck}&active=true");
        var unknown = await ListAsync(admin, $"/alerts?assetId={Guid.NewGuid()}");

        all.Select(alert => alert.GetProperty("id").GetGuid()).ShouldBe([open, resolved]);
        active.Select(alert => alert.GetProperty("id").GetGuid()).ShouldBe([open]);
        unknown.ShouldBeEmpty();
    }

    /// <summary>The body of POST /alert-rules; nullable members so tests can leave each one out.</summary>
#pragma warning disable IDE1006 // JSON property names
    private sealed record RuleRequest(
        string name, string? kind, string? assetId, string? assetGroupId, string? severity, bool enabled,
        string? sensor, string? @operator, object? threshold, double? hysteresis);
#pragma warning restore IDE1006

    private static RuleRequest Overheat(string? assetId = null, string? assetGroupId = null) =>
        new("Engine overheat", "Threshold", assetId, assetGroupId, "Critical", true, "EngineTempC", "GreaterThan", 105, 5);

    // Ingest is the only writer of Alerts; these API tests place one directly.
    private async Task<Guid> InsertAlertAsync(string rule, string asset, string status, DateTimeOffset? raisedAt = null)
    {
        var id = Guid.NewGuid();
        await using var database = await OpenDatabaseAsync();
        await SqlAsync(database, """
            INSERT INTO alerts (id, alert_rule_id, asset_id, reading_device_id, reading_boot, reading_sequence_number, severity, message,
                                status, raised_at, created_at, resolved_at, resolve_reason, from_backlog)
            VALUES (@p0, @p1, @p2, gen_random_uuid(), 1, 7, 'Critical', 'Engine temp 108.4 °C, above 105 (clears below 100)',
                    @p3, @p4, now(), CASE WHEN @p3 = 'Resolved' THEN now() END, CASE WHEN @p3 = 'Resolved' THEN 'Cleared' END, false)
            """, id, Guid.Parse(rule), Guid.Parse(asset), status, raisedAt ?? DateTimeOffset.UtcNow);
        return id;
    }
}
