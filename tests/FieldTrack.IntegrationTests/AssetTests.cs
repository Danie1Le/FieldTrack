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

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        listed.Select(Describe).ShouldBe([("Generator 1", "Generator", null, false)]);
        withArchived.Select(Describe).ShouldBe([("Generator 1", "Generator", null, false), ("Haul truck 7", "Truck", null, true)]);
    }

    [Fact]
    public async Task Deleting_an_unknown_Asset_is_not_found()
    {
        using var admin = await AdminClientAsync();

        using var response = await admin.DeleteAsync($"/assets/{Guid.NewGuid()}", CancellationToken);

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
        using var groups = await client.GetAsync("/asset-groups", CancellationToken);
        using var createAsset = await PostAssetAsync(client, new { name = "Generator 1", type = "Generator" });
        using var editAsset = await client.PutAsJsonAsync($"/assets/{asset}", new { name = "Renamed", type = "Truck" }, CancellationToken);
        using var deleteAsset = await client.DeleteAsync($"/assets/{asset}", CancellationToken);
        using var createGroup = await client.PostAsJsonAsync("/asset-groups", new { name = "Quarry" }, CancellationToken);
        using var renameGroup = await client.PutAsJsonAsync($"/asset-groups/{group}", new { name = "Renamed" }, CancellationToken);

        assets.Select(Describe).ShouldBe([("Haul truck 7", "Truck", group, false)]);
        groups.StatusCode.ShouldBe(HttpStatusCode.OK);
        new[] { createAsset, editAsset, deleteAsset, createGroup, renameGroup }
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
