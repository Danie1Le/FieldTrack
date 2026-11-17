using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FieldTrack.IntegrationTests;

public sealed class AssetTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Admin_creates_an_Asset_and_lists_it()
    {
        using var admin = await AdminClientAsync();

        using var created = await PostAssetAsync(admin, new { name = "Haul truck 7", type = "Truck" });
        var assets = await ListAsync(admin, "/assets");

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        assets.Select(Describe).ShouldBe([("Haul truck 7", "Truck", null, false)]);
    }

    [Fact]
    public async Task Admin_creates_and_renames_Asset_Groups()
    {
        using var admin = await AdminClientAsync();
        var quarry = await CreateAsync(admin, "/asset-groups", new { name = "North quarry" });
        await CreateAsync(admin, "/asset-groups", new { name = "Depot" });

        using var renamed = await admin.PutAsJsonAsync($"/asset-groups/{quarry}", new { name = "South quarry" }, CancellationToken);
        var groups = await admin.GetFromJsonAsync<JsonElement[]>("/asset-groups", CancellationToken);

        renamed.StatusCode.ShouldBe(HttpStatusCode.OK);
        groups.ShouldNotBeNull().Select(group => group.GetProperty("name").GetString()).ShouldBe(["Depot", "South quarry"]);
    }

    [Fact]
    public async Task Admin_deletes_an_Asset_Group_that_nothing_names()
    {
        using var admin = await AdminClientAsync();
        var quarry = await CreateAsync(admin, "/asset-groups", new { name = "North quarry" });
        await CreateAsync(admin, "/asset-groups", new { name = "Depot" });

        using var deleted = await admin.DeleteAsync($"/asset-groups/{quarry}", CancellationToken);
        using var unknown = await admin.DeleteAsync($"/asset-groups/{Guid.NewGuid()}", CancellationToken);
        var groups = await ListAsync(admin, "/asset-groups");

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        groups.Select(group => group.GetProperty("name").GetString()).ShouldBe(["Depot"]);
    }

    [Fact]
    public async Task Deleting_an_Asset_Group_is_refused_while_an_Asset_Archived_or_not_or_an_Alert_Rule_names_it()
    {
        using var admin = await AdminClientAsync();
        var withAsset = await CreateAsync(admin, "/asset-groups", new { name = "Depot" });
        var withArchived = await CreateAsync(admin, "/asset-groups", new { name = "Old depot" });
        var withRule = await CreateAsync(admin, "/asset-groups", new { name = "North quarry" });
        await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck", assetGroupId = withAsset });
        var archived = await CreateAsync(admin, "/assets", new { name = "Generator 1", type = "Generator", assetGroupId = withArchived });
        await using (var database = await OpenDatabaseAsync())
        {
            await SqlAsync(database, "UPDATE assets SET archived = true WHERE id = @p0", Guid.Parse(archived));
        }

        await CreateAsync(admin, "/alert-rules", Overheat(assetGroupId: withRule));

        foreach (var group in new[] { withAsset, withArchived, withRule })
        {
            using var refused = await admin.DeleteAsync($"/asset-groups/{group}", CancellationToken);
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        (await ListAsync(admin, "/asset-groups")).Length.ShouldBe(3);
    }

    [Fact]
    public async Task An_Asset_Group_deleted_while_an_Asset_joins_it_waits_and_is_refused()
    {
        using var admin = await AdminClientAsync();
        var group = await CreateAsync(admin, "/asset-groups", new { name = "Depot" });
        await using var other = await OpenDatabaseAsync();
        await using var joining = await other.BeginTransactionAsync(CancellationToken);
        await SqlAsync(other, "INSERT INTO assets (id, name, type, asset_group_id, archived) VALUES (gen_random_uuid(), 'Haul truck 7', 'Truck', @p0, false)", Guid.Parse(group));

        var delete = admin.DeleteAsync($"/asset-groups/{group}", CancellationToken);
        await LockWaitsAsync(1);
        await joining.CommitAsync(CancellationToken);

        using var deleted = await delete;
        deleted.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task An_Asset_that_joins_an_Asset_Group_while_it_is_deleted_is_refused_as_an_unknown_group()
    {
        using var admin = await AdminClientAsync();
        var group = await CreateAsync(admin, "/asset-groups", new { name = "Depot" });
        await using var other = await OpenDatabaseAsync();
        await using var deleting = await other.BeginTransactionAsync(CancellationToken);
        await SqlAsync(other, "DELETE FROM asset_groups WHERE id = @p0", Guid.Parse(group));

        var create = PostAssetAsync(admin, new { name = "Haul truck 7", type = "Truck", assetGroupId = group });
        await LockWaitsAsync(1);
        await deleting.CommitAsync(CancellationToken);

        using var created = await create;
        created.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ListAsync(admin, "/assets?includeArchived=true")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Renaming_an_Asset_Group_while_it_is_deleted_finds_no_group()
    {
        using var admin = await AdminClientAsync();
        var group = await CreateAsync(admin, "/asset-groups", new { name = "Depot" });
        await using var other = await OpenDatabaseAsync();
        await using var deleting = await other.BeginTransactionAsync(CancellationToken);
        await SqlAsync(other, "DELETE FROM asset_groups WHERE id = @p0", Guid.Parse(group));

        var rename = admin.PutAsJsonAsync($"/asset-groups/{group}", new { name = "Main depot" }, CancellationToken);
        await LockWaitsAsync(1);
        await deleting.CommitAsync(CancellationToken);

        using var renamed = await rename;
        renamed.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ListAsync(admin, "/asset-groups")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Admin_edits_an_Asset_and_moves_it_between_Asset_Groups()
    {
        using var admin = await AdminClientAsync();
        var quarry = await CreateAsync(admin, "/asset-groups", new { name = "North quarry" });
        var depot = await CreateAsync(admin, "/asset-groups", new { name = "Depot" });
        var asset = await CreateAsync(admin, "/assets", new { name = "Excavator 1", type = "Excavator", assetGroupId = quarry });

        using var moved = await admin.PutAsJsonAsync($"/assets/{asset}", new { name = "Excavator 2", type = "Excavator", assetGroupId = depot }, CancellationToken);
        var afterMove = await ListAsync(admin, "/assets");
        using var removed = await admin.PutAsJsonAsync($"/assets/{asset}", new { name = "Generator 2", type = "Generator" }, CancellationToken);
        var afterRemoval = await ListAsync(admin, "/assets");

        moved.StatusCode.ShouldBe(HttpStatusCode.OK);
        afterMove.Select(Describe).ShouldBe([("Excavator 2", "Excavator", depot, false)]);
        removed.StatusCode.ShouldBe(HttpStatusCode.OK);
        afterRemoval.Select(Describe).ShouldBe([("Generator 2", "Generator", null, false)]);
    }

    [Fact]
    public async Task Edit_refuses_an_unknown_Asset_Group_and_an_unknown_Asset()
    {
        using var admin = await AdminClientAsync();
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });

        using var unknownGroup = await admin.PutAsJsonAsync($"/assets/{asset}", new { name = "Haul truck 7", type = "Truck", assetGroupId = Guid.NewGuid() }, CancellationToken);
        using var unknownAsset = await admin.PutAsJsonAsync($"/assets/{Guid.NewGuid()}", new { name = "Haul truck 7", type = "Truck" }, CancellationToken);
        using var unknownRenamedGroup = await admin.PutAsJsonAsync($"/asset-groups/{Guid.NewGuid()}", new { name = "Depot" }, CancellationToken);

        unknownGroup.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        unknownAsset.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        unknownRenamedGroup.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Deleting_an_Asset_without_Readings_removes_it()
    {
        using var admin = await AdminClientAsync();
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });

        using var deleted = await admin.DeleteAsync($"/assets/{asset}", CancellationToken);
        var assets = await ListAsync(admin, "/assets?includeArchived=true");

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        assets.ShouldBeEmpty();
    }

    [Fact]
    public async Task Deleting_an_Asset_with_Readings_Archives_it_and_lists_hide_it_unless_asked()
    {
        await using var api = await StartIngestAsync();
        using var admin = await AdminClientAsync(api);
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });
        await CreateAsync(admin, "/assets", new { name = "Generator 1", type = "Generator" });
        var device = await CreateAsync(admin, "/devices", new { serialNumber = "TRK-007" });
        await CreateAsync(admin, "/attachments", new { deviceId = device, assetId = asset });
        await using (var publisher = await TelemetryPublisher.ConnectAsync(Fixture))
        {
            await publisher.PublishAsync("TRK-007", TelemetryPublisher.Payload(1, 0, DateTimeOffset.UtcNow));
        }

        await EventuallyAsync(
            () => ListAsync(admin, "/devices"),
            devices => devices.Single().GetProperty("lastReceivedAt").ValueKind != JsonValueKind.Null);

        using var deleted = await admin.DeleteAsync($"/assets/{asset}", CancellationToken);
        var listed = await ListAsync(admin, "/assets");
        var withArchived = await ListAsync(admin, "/assets?includeArchived=true");
        var archived = await admin.GetFromJsonAsync<JsonElement>($"/assets/{asset}", CancellationToken);

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        listed.Select(Describe).ShouldBe([("Generator 1", "Generator", null, false)]);
        withArchived.Select(Describe).ShouldBe([("Generator 1", "Generator", null, false), ("Haul truck 7", "Truck", null, true)]);
        Describe(archived).ShouldBe(("Haul truck 7", "Truck", null, true));
    }

    [Fact]
    public async Task Deleting_an_Asset_with_an_Offline_Alert_but_no_Readings_Archives_it_so_the_Alert_keeps_its_Asset()
    {
        using var admin = await AdminClientAsync();
        var asset = await CreateAsync(admin, "/assets", new { name = "Generator 1", type = "Generator" });
        var rule = await CreateAsync(admin, "/alert-rules", Silent(asset));
        // A Device that never reported raises an Offline Alert without any Reading; ingest's sweep writes it.
        await InsertAlertAsync(rule, asset, offline: true);

        using var deleted = await admin.DeleteAsync($"/assets/{asset}", CancellationToken);
        var withArchived = await ListAsync(admin, "/assets?includeArchived=true");

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        withArchived.Select(Describe).ShouldBe([("Generator 1", "Generator", null, true)]);
    }

    [Fact]
    public async Task Deleting_an_unknown_Asset_is_not_found()
    {
        using var admin = await AdminClientAsync();

        using var response = await admin.DeleteAsync($"/assets/{Guid.NewGuid()}", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reading_an_unknown_Asset_is_not_found()
    {
        using var admin = await AdminClientAsync();

        using var response = await admin.GetAsync($"/assets/{Guid.NewGuid()}", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Operators_read_Assets_and_Asset_Groups_but_cannot_change_them()
    {
        using var admin = await AdminClientAsync();
        var group = await CreateAsync(admin, "/asset-groups", new { name = "Depot" });
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck", assetGroupId = group });
        await AuthClient.CreateUserAsync(admin, "operator@fieldtrack.test", "Operator", "Operator-pass-1");
        using var client = Fixture.Api.CreateClient();
        await AuthClient.AuthorizeAsync(client, "operator@fieldtrack.test", "Operator-pass-1");

        var assets = await ListAsync(client, "/assets");
        var one = await client.GetFromJsonAsync<JsonElement>($"/assets/{asset}", CancellationToken);
        using var groups = await client.GetAsync("/asset-groups", CancellationToken);
        using var createAsset = await PostAssetAsync(client, new { name = "Generator 1", type = "Generator" });
        using var editAsset = await client.PutAsJsonAsync($"/assets/{asset}", new { name = "Renamed", type = "Truck" }, CancellationToken);
        using var deleteAsset = await client.DeleteAsync($"/assets/{asset}", CancellationToken);
        using var createGroup = await client.PostAsJsonAsync("/asset-groups", new { name = "Quarry" }, CancellationToken);
        using var renameGroup = await client.PutAsJsonAsync($"/asset-groups/{group}", new { name = "Renamed" }, CancellationToken);
        using var deleteGroup = await client.DeleteAsync($"/asset-groups/{group}", CancellationToken);

        assets.Select(Describe).ShouldBe([("Haul truck 7", "Truck", group, false)]);
        Describe(one).ShouldBe(("Haul truck 7", "Truck", group, false));
        groups.StatusCode.ShouldBe(HttpStatusCode.OK);
        new[] { createAsset, editAsset, deleteAsset, createGroup, renameGroup, deleteGroup }
            .Select(response => response.StatusCode)
            .ShouldAllBe(status => status == HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("/assets")]
    [InlineData("/asset-groups")]
    public async Task Nobody_signed_out_reads_Assets_or_Asset_Groups(string path)
    {
        using var client = Fixture.Api.CreateClient();

        using var response = await client.GetAsync(path, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    public static TheoryData<object> InvalidAssets() => new()
    {
        new { name = "Haul truck 7", type = "Crane" },
        new { name = "Haul truck 7", type = 1 },
        new { name = "Haul truck 7", type = "Truck, Excavator" },
        new { name = "Haul truck 7" },
        new { name = " ", type = "Truck" },
        new { name = "Haul truck 7", type = "Truck", assetGroupId = Guid.NewGuid() },
    };

    [Theory]
    [MemberData(nameof(InvalidAssets))]
    public async Task Create_refuses_an_unknown_Asset_Type_a_blank_name_or_an_unknown_Asset_Group(object asset)
    {
        using var admin = await AdminClientAsync();

        using var response = await PostAssetAsync(admin, asset);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ListAsync(admin, "/assets?includeArchived=true")).ShouldBeEmpty();
    }

    private static Task<HttpResponseMessage> PostAssetAsync(HttpClient client, object asset) =>
        client.PostAsJsonAsync("/assets", asset, CancellationToken);

    private static (string?, string?, string?, bool) Describe(JsonElement asset) => (
        asset.GetProperty("name").GetString(),
        asset.GetProperty("type").GetString(),
        asset.GetProperty("assetGroupId").GetString(),
        asset.GetProperty("archived").GetBoolean());
}
