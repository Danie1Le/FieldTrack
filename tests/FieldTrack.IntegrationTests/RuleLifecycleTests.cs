using System.Net.Http.Json;
using System.Text.Json;
using FieldTrack.Api.Live;

namespace FieldTrack.IntegrationTests;

public sealed class RuleLifecycleTests(FieldTrackFixture fixture) : AlertTest(fixture)
{
    [Theory]
    [InlineData("edit rule", "RuleChanged")]
    [InlineData("disable rule", "RuleDisabled")]
    [InlineData("archive Asset", "AssetArchived")]
    [InlineData("detach Device", "DeviceDetached")]
    [InlineData("archive Device", "DeviceDetached")]
    public async Task A_change_resolves_the_Active_Alert_as_the_platform_with_its_reason_and_pushes_it_live(string change, string reason)
    {
        using var admin = await AdminClientAsync();
        var (device, asset, rule, alert) = await OpenAlertAsync(admin);
        await using var live = await LiveClient.ConnectAsync(Fixture.Api, admin.DefaultRequestHeaders.Authorization!.Parameter);

        await ChangeAsync(admin, change, device, asset, rule);

        var resolved = await admin.GetFromJsonAsync<JsonElement>($"/alerts/{alert}", CancellationToken);
        resolved.GetProperty("status").GetString().ShouldBe("Resolved");
        resolved.GetProperty("resolveReason").GetString().ShouldBe(reason);
        resolved.GetProperty("resolvedBy").ValueKind.ShouldBe(JsonValueKind.Null);
        resolved.GetProperty("resolvedAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1));
        var pushed = await live.NextAsync(LiveHub.AlertUpdated, pushed => pushed.GetProperty("id").GetString() == alert);
        pushed.GetProperty("resolveReason").GetString().ShouldBe(reason);
    }

    [Fact]
    public async Task An_edited_rule_restarts_from_clean_state_so_a_breach_that_still_holds_raises_a_new_Alert()
    {
        using var admin = await AdminClientAsync();
        var (_, asset, rule, _) = await OpenAlertAsync(admin);

        await ChangeAsync(admin, "edit rule", null, asset, rule);
        // Still past the threshold: a rule that kept its breached state would raise nothing.
        await WriteAsync([Received(1, DateTimeOffset.UtcNow, 111)]);

        var active = (await ListAsync(admin, "/alerts?active=true")).ShouldHaveSingleItem();
        active.GetProperty("reading").GetProperty("sequenceNumber").GetInt64().ShouldBe(1);
        active.GetProperty("latched").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task After_a_detach_the_next_Device_starts_the_Asset_from_clean_state()
    {
        using var admin = await AdminClientAsync();
        var (device, asset, rule, _) = await OpenAlertAsync(admin);
        var next = await CreateAsync(admin, "/devices", new { serialNumber = "TRK-008" });

        await ChangeAsync(admin, "detach Device", device, asset, rule);
        await CreateAsync(admin, "/attachments", new { deviceId = next, assetId = asset });
        // Already past the threshold when fitted: the old Device's breached state must not hold this back.
        await WriteAsync([Received(0, DateTimeOffset.UtcNow, 112, "TRK-008")]);

        var active = (await ListAsync(admin, "/alerts?active=true")).ShouldHaveSingleItem();
        active.GetProperty("reading").GetProperty("deviceId").GetString().ShouldBe(next);
    }

    [Fact]
    public async Task Archiving_an_Asset_disables_its_own_rules_and_resolves_the_Alerts_of_its_Asset_Group_rules_too()
    {
        using var admin = await AdminClientAsync();
        var group = await CreateAsync(admin, "/asset-groups", new { name = "North quarry" });
        var (_, asset) = await AttachedDeviceAsync(admin, assetGroupId: group);
        var own = await CreateAsync(admin, "/alert-rules", Overheat(asset));
        var groupRule = await CreateAsync(admin, "/alert-rules", Overheat(assetGroupId: group));
        await WriteAsync([Received(0, DateTimeOffset.UtcNow, 110)]);

        await ChangeAsync(admin, "archive Asset", null, asset, own);

        (await ListAsync(admin, "/alerts?active=false")).Select(alert => alert.GetProperty("resolveReason").GetString())
            .ShouldBe(["AssetArchived", "AssetArchived"]);
        (await admin.GetFromJsonAsync<JsonElement>($"/alert-rules/{own}", CancellationToken)).GetProperty("enabled").GetBoolean().ShouldBeFalse();
        (await admin.GetFromJsonAsync<JsonElement>($"/alert-rules/{groupRule}", CancellationToken)).GetProperty("enabled").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Every_change_to_what_ingest_evaluates_raises_the_Alert_Rule_version()
    {
        using var admin = await AdminClientAsync();
        var (device, asset) = await AttachedDeviceAsync(admin);
        await WriteAsync([Received(0, DateTimeOffset.UtcNow, 90)]);
        await using var database = await OpenDatabaseAsync();
        var rule = "";
        var changes = new Dictionary<string, Func<Task>>
        {
            ["create rule"] = async () => rule = await CreateAsync(admin, "/alert-rules", Overheat(asset)),
            ["edit rule"] = () => ChangeAsync(admin, "edit rule", device, asset, rule),
            ["delete rule"] = async () => (await admin.DeleteAsync($"/alert-rules/{rule}", CancellationToken)).EnsureSuccessStatusCode(),
            ["detach Device"] = () => ChangeAsync(admin, "detach Device", device, asset, rule),
            ["archive Asset"] = () => ChangeAsync(admin, "archive Asset", device, asset, rule),
        };

        foreach (var (name, change) in changes)
        {
            var before = (long)(await SqlAsync(database, "SELECT version FROM alert_rule_version"))!;
            await change();
            ((long)(await SqlAsync(database, "SELECT version FROM alert_rule_version"))!).ShouldBeGreaterThan(before, name);
        }
    }

    // An Engine overheat rule on an Asset fitted with TRK-007, and the Open Alert its first Reading raised.
    private async Task<(string Device, string Asset, string Rule, string Alert)> OpenAlertAsync(HttpClient admin)
    {
        var (device, asset) = await AttachedDeviceAsync(admin);
        var rule = await CreateAsync(admin, "/alert-rules", Overheat(asset));
        await WriteAsync([Received(0, DateTimeOffset.UtcNow, 110)]);
        var alert = (await ListAsync(admin, "/alerts?active=true")).ShouldHaveSingleItem().GetProperty("id").GetString()!;
        return (device, asset, rule, alert);
    }

    private static async Task ChangeAsync(HttpClient admin, string change, string? device, string asset, string rule)
    {
        using var response = change switch
        {
            "edit rule" => await admin.PutAsJsonAsync($"/alert-rules/{rule}", Overheat(asset) with { name = "Engine hot" }, CancellationToken),
            "disable rule" => await admin.PutAsJsonAsync($"/alert-rules/{rule}", Overheat(asset) with { enabled = false }, CancellationToken),
            "archive Asset" => await admin.DeleteAsync($"/assets/{asset}", CancellationToken),
            "detach Device" => await admin.PostAsync(
                $"/attachments/{(await ListAsync(admin, $"/attachments?assetId={asset}"))[0].GetProperty("id").GetString()}/detach", content: null, CancellationToken),
            "archive Device" => await admin.DeleteAsync($"/devices/{device}", CancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(change), change, null),
        };
        response.EnsureSuccessStatusCode();
    }
}
