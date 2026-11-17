using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldTrack.Api.Live;

namespace FieldTrack.IntegrationTests;

public sealed class GeofenceTests(FieldTrackFixture fixture) : AlertTest(fixture)
{
    [Fact]
    public async Task Admin_creates_reads_edits_lists_and_deletes_a_Geofence_with_its_area_as_a_GeoJSON_polygon()
    {
        using var admin = await AdminClientAsync();

        var geofence = await CreateAsync(admin, "/geofences", Depot());
        var created = await admin.GetFromJsonAsync<JsonElement>($"/geofences/{geofence}", CancellationToken);
        using var edited = await admin.PutAsJsonAsync($"/geofences/{geofence}", Depot() with { name = "No-go zone", kind = "Exclusion" }, CancellationToken);
        var listed = await ListAsync(admin, "/geofences");
        using var deleted = await admin.DeleteAsync($"/geofences/{geofence}", CancellationToken);

        created.GetProperty("name").GetString().ShouldBe("Depot");
        created.GetProperty("kind").GetString().ShouldBe("Inclusion");
        created.GetProperty("area").GetProperty("type").GetString().ShouldBe("Polygon");
        created.GetProperty("area").GetProperty("coordinates").Deserialize<double[][][]>().ShouldBe(Square(-0.13, 51.5, 0.01).coordinates);
        edited.StatusCode.ShouldBe(HttpStatusCode.OK);
        var only = listed.ShouldHaveSingleItem();
        only.GetProperty("name").GetString().ShouldBe("No-go zone");
        only.GetProperty("kind").GetString().ShouldBe("Exclusion");
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ListAsync(admin, "/geofences")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_refuses_an_area_that_is_not_one_valid_WGS_84_polygon()
    {
        using var admin = await AdminClientAsync();
        double[][] open = [[0, 0], [1, 0], [1, 1], [0, 1]];
        double[][] bowTie = [[0, 0], [1, 1], [1, 0], [0, 1], [0, 0]];
        double[][] tooFewPositions = [[0, 0], [1, 0], [0, 0]];
        double[][] latitudeOutOfRange = [[0, 0], [1, 0], [1, 91], [0, 0]];
        double[][] longitudeOutOfRange = [[0, 0], [181, 0], [1, 1], [0, 0]];
        double[][] antipodalEdge = [[0, 0], [180, 0], [180, 10], [0, 10], [0, 0]];
        double[][] antimeridianStrip = [[170, 0], [-170, 0], [-170, 10], [170, 10], [170, 0]];
        double[][] threeNumbers = [[0, 0, 5], [1, 0, 5], [1, 1, 5], [0, 0, 5]];
        double[][] tooManyPositions = [.. Enumerable.Range(0, 1000).Select(i => new[] { Math.Cos(i * Math.Tau / 1000), Math.Sin(i * Math.Tau / 1000) }), [1, 0]];

        GeofenceRequest[] invalid =
        [
            Depot() with { area = Square(0, 0, 1) with { type = "MultiPolygon" } },
            Depot() with { area = new Area("Polygon", []) },
            Depot() with { area = new Area("Polygon", [open]) },
            Depot() with { area = new Area("Polygon", [bowTie]) },
            Depot() with { area = new Area("Polygon", [tooFewPositions]) },
            Depot() with { area = new Area("Polygon", [latitudeOutOfRange]) },
            Depot() with { area = new Area("Polygon", [longitudeOutOfRange]) },
            Depot() with { area = new Area("Polygon", [antipodalEdge]) },
            Depot() with { area = new Area("Polygon", [antimeridianStrip]) },
            Depot() with { area = new Area("Polygon", [threeNumbers]) },
            Depot() with { area = new Area("Polygon", [tooManyPositions]) },
            Depot() with { area = null },
            Depot() with { kind = null },
            Depot() with { kind = "Inclusion, Exclusion" },
            Depot() with { name = "" },
        ];
        foreach (var request in invalid)
        {
            using var response = await admin.PostAsJsonAsync("/geofences", request, CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, JsonSerializer.Serialize(request));
        }

        (await ListAsync(admin, "/geofences")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_polygon_with_a_hole_is_kept_whole()
    {
        using var admin = await AdminClientAsync();
        double[][] outer = [[0, 0], [10, 0], [10, 10], [0, 10], [0, 0]];
        double[][] hole = [[4, 4], [6, 4], [6, 6], [4, 6], [4, 4]];

        var geofence = await CreateAsync(admin, "/geofences", Depot() with { area = new Area("Polygon", [outer, hole]) });

        var area = (await admin.GetFromJsonAsync<JsonElement>($"/geofences/{geofence}", CancellationToken)).GetProperty("area");
        area.GetProperty("coordinates").Deserialize<double[][][]>().ShouldBe([outer, hole]);
    }

    [Fact]
    public async Task Operators_read_Geofences_but_cannot_change_them()
    {
        using var admin = await AdminClientAsync();
        var geofence = await CreateAsync(admin, "/geofences", Depot());
        await AuthClient.CreateUserAsync(admin, "operator@fieldtrack.test", "Operator", "Operator-pass-1");
        using var client = Fixture.Api.CreateClient();
        await AuthClient.AuthorizeAsync(client, "operator@fieldtrack.test", "Operator-pass-1");

        var geofences = await ListAsync(client, "/geofences");
        using var create = await client.PostAsJsonAsync("/geofences", Depot(), CancellationToken);
        using var edit = await client.PutAsJsonAsync($"/geofences/{geofence}", Depot(), CancellationToken);
        using var delete = await client.DeleteAsync($"/geofences/{geofence}", CancellationToken);

        geofences.ShouldHaveSingleItem();
        new[] { create, edit, delete }.ShouldAllBe(response => response.StatusCode == HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_rule_created_while_its_Geofence_is_being_deleted_waits_for_the_delete_and_is_refused()
    {
        using var admin = await AdminClientAsync();
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });
        var geofence = await CreateAsync(admin, "/geofences", Depot());
        await using var deleter = await OpenDatabaseAsync();
        await using var delete = await deleter.BeginTransactionAsync(CancellationToken);
        // What Geofence delete does: bump the Alert Rule version, then delete the Geofence row.
        await SqlAsync(deleter, "UPDATE alert_rule_version SET version = version + 1");

        var create = admin.PostAsJsonAsync("/alert-rules", LeftDepot(asset, geofence), CancellationToken);
        await LockWaitsAsync(1);
        // A create that checked the Geofence before its own bump has passed the check here, and fails on the foreign key.
        await SqlAsync(deleter, "DELETE FROM geofences WHERE id = @p0", Guid.Parse(geofence));
        await delete.CommitAsync(CancellationToken);

        using var created = await create;
        created.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Read_edit_and_delete_answer_404_for_an_unknown_Geofence()
    {
        using var admin = await AdminClientAsync();
        var unknown = Guid.NewGuid();

        using var read = await admin.GetAsync($"/geofences/{unknown}", CancellationToken);
        using var edit = await admin.PutAsJsonAsync($"/geofences/{unknown}", Depot(), CancellationToken);
        using var delete = await admin.DeleteAsync($"/geofences/{unknown}", CancellationToken);

        new[] { read, edit, delete }.ShouldAllBe(response => response.StatusCode == HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_Geofence_rule_names_one_known_Geofence_which_then_cannot_be_deleted()
    {
        using var admin = await AdminClientAsync();
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });
        var geofence = await CreateAsync(admin, "/geofences", Depot());

        using var withoutGeofence = await admin.PostAsJsonAsync("/alert-rules", LeftDepot(asset, geofenceId: null), CancellationToken);
        using var unknownGeofence = await admin.PostAsJsonAsync("/alert-rules", LeftDepot(asset, Guid.NewGuid().ToString()), CancellationToken);
        var rule = await CreateAsync(admin, "/alert-rules", LeftDepot(asset, geofence));
        var enter = await CreateAsync(admin, "/alert-rules", LeftDepot(asset, geofence) with { kind = "GeofenceEnter" });
        var threshold = await CreateAsync(admin, "/alert-rules", Overheat(asset));
        using var deleted = await admin.DeleteAsync($"/geofences/{geofence}", CancellationToken);

        withoutGeofence.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        unknownGeofence.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var created = await admin.GetFromJsonAsync<JsonElement>($"/alert-rules/{rule}", CancellationToken);
        created.GetProperty("kind").GetString().ShouldBe("GeofenceExit");
        created.GetProperty("geofenceId").GetString().ShouldBe(geofence);
        (await admin.GetFromJsonAsync<JsonElement>($"/alert-rules/{enter}", CancellationToken)).GetProperty("kind").GetString().ShouldBe("GeofenceEnter");
        (await admin.GetFromJsonAsync<JsonElement>($"/alert-rules/{threshold}", CancellationToken)).GetProperty("geofenceId").ValueKind.ShouldBe(JsonValueKind.Null);
        deleted.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ListAsync(admin, "/geofences")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Every_Geofence_edit_and_delete_raises_the_Alert_Rule_version()
    {
        using var admin = await AdminClientAsync();
        await using var database = await OpenDatabaseAsync();
        var geofence = await CreateAsync(admin, "/geofences", Depot());
        var changes = new Dictionary<string, Func<Task>>
        {
            ["rename"] = async () => (await admin.PutAsJsonAsync($"/geofences/{geofence}", Depot() with { name = "Yard" }, CancellationToken)).EnsureSuccessStatusCode(),
            ["move"] = async () => (await admin.PutAsJsonAsync($"/geofences/{geofence}", Depot() with { area = Square(0, 0, 1) }, CancellationToken)).EnsureSuccessStatusCode(),
            ["delete"] = async () => (await admin.DeleteAsync($"/geofences/{geofence}", CancellationToken)).EnsureSuccessStatusCode(),
        };

        foreach (var (name, change) in changes)
        {
            var before = (long)(await SqlAsync(database, "SELECT version FROM alert_rule_version"))!;
            await change();
            ((long)(await SqlAsync(database, "SELECT version FROM alert_rule_version"))!).ShouldBeGreaterThan(before, name);
        }
    }

    [Fact]
    public async Task Every_Geofence_create_edit_and_delete_tells_the_dashboards_and_a_refused_change_does_not()
    {
        using var admin = await AdminClientAsync();
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });
        await using var live = await LiveClient.ConnectAsync(Fixture.Api, admin.DefaultRequestHeaders.Authorization!.Parameter);

        var named = await CreateAsync(admin, "/geofences", Depot());
        await live.NextAsync(LiveHub.GeofencesChanged);
        await CreateAsync(admin, "/alert-rules", LeftDepot(asset, named));
        using var invalid = await admin.PostAsJsonAsync("/geofences", Depot() with { name = "" }, CancellationToken);
        using var unknown = await admin.PutAsJsonAsync($"/geofences/{Guid.NewGuid()}", Depot(), CancellationToken);
        using var refused = await admin.DeleteAsync($"/geofences/{named}", CancellationToken);
        // Each push is sent before its response returns, so a push by a refused change would already be queued.
        (await live.AnyWithinAsync(TimeSpan.FromSeconds(1))).ShouldBeNull();
        var geofence = await CreateAsync(admin, "/geofences", Depot() with { name = "Yard" });
        await live.NextAsync(LiveHub.GeofencesChanged);
        (await admin.PutAsJsonAsync($"/geofences/{geofence}", Depot() with { area = Square(0, 0, 1) }, CancellationToken)).EnsureSuccessStatusCode();
        await live.NextAsync(LiveHub.GeofencesChanged);
        (await admin.DeleteAsync($"/geofences/{geofence}", CancellationToken)).EnsureSuccessStatusCode();
        await live.NextAsync(LiveHub.GeofencesChanged);

        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("move the Geofence", "RuleChanged")]
    [InlineData("point the rule at another Geofence", "RuleChanged")]
    [InlineData("rename the Geofence", null)]
    [InlineData("make it an Exclusion", null)]
    public async Task A_new_area_for_a_rule_resolves_its_Active_Alert_but_a_new_name_or_kind_does_not(string change, string? reason)
    {
        using var admin = await AdminClientAsync();
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });
        var geofence = await CreateAsync(admin, "/geofences", Depot());
        var rule = await CreateAsync(admin, "/alert-rules", LeftDepot(asset, geofence));
        var alert = await InsertOpenAlertAsync(rule, asset);

        using var response = change switch
        {
            "move the Geofence" => await admin.PutAsJsonAsync($"/geofences/{geofence}", Depot() with { area = Square(0, 0, 1) }, CancellationToken),
            "point the rule at another Geofence" => await admin.PutAsJsonAsync(
                $"/alert-rules/{rule}", LeftDepot(asset, await CreateAsync(admin, "/geofences", Depot() with { name = "Yard" })), CancellationToken),
            "rename the Geofence" => await admin.PutAsJsonAsync($"/geofences/{geofence}", Depot() with { name = "Yard" }, CancellationToken),
            "make it an Exclusion" => await admin.PutAsJsonAsync($"/geofences/{geofence}", Depot() with { kind = "Exclusion" }, CancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(change), change, null),
        };

        response.EnsureSuccessStatusCode();
        var after = await admin.GetFromJsonAsync<JsonElement>($"/alerts/{alert}", CancellationToken);
        after.GetProperty("status").GetString().ShouldBe(reason is null ? "Open" : "Resolved");
        after.GetProperty("resolveReason").ValueKind.ShouldBe(reason is null ? JsonValueKind.Null : JsonValueKind.String);
        if (reason is not null)
        {
            after.GetProperty("resolveReason").GetString().ShouldBe(reason);
        }
    }

#pragma warning disable IDE1006 // JSON property names
    private sealed record Area(string type, double[][][] coordinates);

    private sealed record GeofenceRequest(string name, string? kind, Area? area);

    private sealed record GeofenceRuleRequest(string name, string kind, string assetId, string severity, bool enabled, string? geofenceId);
#pragma warning restore IDE1006

    // A square of side `size` degrees with its south-west corner at (lon, lat), counter-clockwise as GeoJSON prefers.
    private static Area Square(double lon, double lat, double size) =>
        new("Polygon", [[[lon, lat], [lon + size, lat], [lon + size, lat + size], [lon, lat + size], [lon, lat]]]);

    private static GeofenceRequest Depot() => new("Depot", "Inclusion", Square(-0.13, 51.5, 0.01));

    private static GeofenceRuleRequest LeftDepot(string asset, string? geofenceId) => new("Left depot", "GeofenceExit", asset, "Warning", true, geofenceId);

    // Ingest is the only writer of Alerts; these API tests place one directly.
    private async Task<string> InsertOpenAlertAsync(string rule, string asset)
    {
        var id = Guid.NewGuid();
        await using var database = await OpenDatabaseAsync();
        await SqlAsync(database, """
            INSERT INTO alerts (id, alert_rule_id, asset_id, reading_device_id, reading_boot, reading_sequence_number, severity, message,
                                status, raised_at, created_at, from_backlog)
            VALUES (@p0, @p1, @p2, gen_random_uuid(), 1, 0, 'Warning', 'Left Geofence Depot', 'Open', now(), now(), false)
            """, id, Guid.Parse(rule), Guid.Parse(asset));
        return id.ToString();
    }
}
