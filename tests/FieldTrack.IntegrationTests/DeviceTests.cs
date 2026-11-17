using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FieldTrack.IntegrationTests;

public sealed class DeviceTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Admin_registers_Devices_by_serial_number_and_lists_them()
    {
        using var admin = await AdminClientAsync();

        using var created = await admin.PostAsJsonAsync("/devices", new { serialNumber = "TRK-007" }, CancellationToken);
        await CreateAsync(admin, "/devices", new { serialNumber = "GEN-001" });
        var devices = await ListAsync(admin, "/devices");

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        devices.Select(DescribeDevice).ShouldBe([("GEN-001", false), ("TRK-007", false)]);
        devices.ShouldAllBe(device => device.GetProperty("lastReceivedAt").ValueKind == JsonValueKind.Null);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("TRK/007")]
    [InlineData("TRK+")]
    [InlineData("TRK#")]
    [InlineData("TRK 007")]
    public async Task Register_refuses_a_serial_number_that_cannot_be_a_telemetry_topic_level(string serialNumber)
    {
        using var admin = await AdminClientAsync();

        using var response = await admin.PostAsJsonAsync("/devices", new { serialNumber }, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ListAsync(admin, "/devices?includeArchived=true")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Register_refuses_a_serial_number_already_registered()
    {
        using var admin = await AdminClientAsync();
        await CreateAsync(admin, "/devices", new { serialNumber = "TRK-007" });

        using var response = await admin.PostAsJsonAsync("/devices", new { serialNumber = "TRK-007" }, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemDetailAsync(response)).ShouldNotBeNull().ShouldContain("TRK-007");
    }

    [Fact]
    public async Task Attach_and_detach_record_times_and_history_is_listed_per_Asset_and_per_Device()
    {
        using var admin = await AdminClientAsync();
        var device = await CreateAsync(admin, "/devices", new { serialNumber = "TRK-007" });
        var truck = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });
        var spare = await CreateAsync(admin, "/assets", new { name = "Haul truck 8", type = "Truck" });
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var first = await CreateAsync(admin, "/attachments", new { deviceId = device, assetId = truck });
        using var detached = await admin.PostAsync($"/attachments/{first}/detach", null, CancellationToken);
        var second = await CreateAsync(admin, "/attachments", new { deviceId = device, assetId = spare });
        var after = DateTimeOffset.UtcNow.AddSeconds(1);
        var byDevice = await ListAsync(admin, $"/attachments?deviceId={device}");
        var byTruck = await ListAsync(admin, $"/attachments?assetId={truck}");

        detached.StatusCode.ShouldBe(HttpStatusCode.OK);
        byDevice.Select(DescribeAttachment).ShouldBe([(second, device, spare, false), (first, device, truck, true)]);
        byTruck.Select(DescribeAttachment).ShouldBe([(first, device, truck, true)]);
        var (firstAttached, firstDetached) = (TimeOf(byDevice[1], "attachedAt"), TimeOf(byDevice[1], "detachedAt"));
        var secondAttached = TimeOf(byDevice[0], "attachedAt");
        firstAttached.ShouldBeInRange(before, after);
        firstDetached.ShouldBeInRange(firstAttached, secondAttached);
        secondAttached.ShouldBeLessThanOrEqualTo(after);
    }

    [Fact]
    public async Task A_Device_is_on_at_most_one_Asset_and_an_Asset_has_at_most_one_Device()
    {
        using var admin = await AdminClientAsync();
        var device = await CreateAsync(admin, "/devices", new { serialNumber = "TRK-007" });
        var otherDevice = await CreateAsync(admin, "/devices", new { serialNumber = "TRK-008" });
        var truck = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });
        var spare = await CreateAsync(admin, "/assets", new { name = "Haul truck 8", type = "Truck" });
        var attachment = await CreateAsync(admin, "/attachments", new { deviceId = device, assetId = truck });

        using var deviceTaken = await admin.PostAsJsonAsync("/attachments", new { deviceId = device, assetId = spare }, CancellationToken);
        using var assetTaken = await admin.PostAsJsonAsync("/attachments", new { deviceId = otherDevice, assetId = truck }, CancellationToken);
        await admin.PostAsync($"/attachments/{attachment}/detach", null, CancellationToken);
        using var afterDetach = await admin.PostAsJsonAsync("/attachments", new { deviceId = device, assetId = spare }, CancellationToken);

        deviceTaken.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemDetailAsync(deviceTaken)).ShouldBe("The Device is already attached to an Asset; detach it first.");
        assetTaken.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemDetailAsync(assetTaken)).ShouldBe("The Asset already has a Device attached; detach it first.");
        afterDetach.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Attach_refuses_unknown_and_Archived_Devices_and_Assets()
    {
        await using var api = ApiWhereEverythingHasReadings();
        using var admin = await AdminClientAsync(api);
        var device = await CreateAsync(admin, "/devices", new { serialNumber = "TRK-007" });
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });
        var archivedDevice = await CreateAsync(admin, "/devices", new { serialNumber = "TRK-008" });
        var archivedAsset = await CreateAsync(admin, "/assets", new { name = "Haul truck 8", type = "Truck" });
        await admin.DeleteAsync($"/devices/{archivedDevice}", CancellationToken);
        await admin.DeleteAsync($"/assets/{archivedAsset}", CancellationToken);

        using var unknownDevice = await admin.PostAsJsonAsync("/attachments", new { deviceId = Guid.NewGuid(), assetId = asset }, CancellationToken);
        using var unknownAsset = await admin.PostAsJsonAsync("/attachments", new { deviceId = device, assetId = Guid.NewGuid() }, CancellationToken);
        using var toArchivedDevice = await admin.PostAsJsonAsync("/attachments", new { deviceId = archivedDevice, assetId = asset }, CancellationToken);
        using var toArchivedAsset = await admin.PostAsJsonAsync("/attachments", new { deviceId = device, assetId = archivedAsset }, CancellationToken);

        unknownDevice.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        unknownAsset.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        toArchivedDevice.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        toArchivedAsset.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ListAsync(admin, "/attachments")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Detach_refuses_an_ended_or_unknown_Attachment()
    {
        using var admin = await AdminClientAsync();
        var device = await CreateAsync(admin, "/devices", new { serialNumber = "TRK-007" });
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });
        var attachment = await CreateAsync(admin, "/attachments", new { deviceId = device, assetId = asset });
        await admin.PostAsync($"/attachments/{attachment}/detach", null, CancellationToken);

        using var again = await admin.PostAsync($"/attachments/{attachment}/detach", null, CancellationToken);
        using var unknown = await admin.PostAsync($"/attachments/{Guid.NewGuid()}/detach", null, CancellationToken);

        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Deleting_a_Device_without_Readings_removes_it_and_its_Attachments()
    {
        using var admin = await AdminClientAsync();
        var device = await CreateAsync(admin, "/devices", new { serialNumber = "TRK-007" });
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });
        await CreateAsync(admin, "/attachments", new { deviceId = device, assetId = asset });

        using var deleted = await admin.DeleteAsync($"/devices/{device}", CancellationToken);

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ListAsync(admin, "/devices?includeArchived=true")).ShouldBeEmpty();
        (await ListAsync(admin, "/attachments")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Deleting_a_Device_with_Readings_Archives_it_and_ends_its_Attachment()
    {
        await using var api = ApiWhereEverythingHasReadings();
        using var admin = await AdminClientAsync(api);
        var device = await CreateAsync(admin, "/devices", new { serialNumber = "TRK-007" });
        await CreateAsync(admin, "/devices", new { serialNumber = "GEN-001" });
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });
        var attachment = await CreateAsync(admin, "/attachments", new { deviceId = device, assetId = asset });

        using var deleted = await admin.DeleteAsync($"/devices/{device}", CancellationToken);

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ListAsync(admin, "/devices")).Select(DescribeDevice).ShouldBe([("GEN-001", false)]);
        (await ListAsync(admin, "/devices?includeArchived=true")).Select(DescribeDevice).ShouldBe([("GEN-001", false), ("TRK-007", true)]);
        (await ListAsync(admin, "/attachments")).Select(DescribeAttachment).ShouldBe([(attachment, device, asset, true)]);
        // Ending the Attachment frees the Asset for another Device.
        var otherDevice = await CreateAsync(admin, "/devices", new { serialNumber = "TRK-008" });
        using var reattached = await admin.PostAsJsonAsync("/attachments", new { deviceId = otherDevice, assetId = asset }, CancellationToken);
        reattached.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Deleting_an_Asset_with_Attachments_but_no_Readings_removes_it_and_its_Attachments()
    {
        using var admin = await AdminClientAsync();
        var device = await CreateAsync(admin, "/devices", new { serialNumber = "TRK-007" });
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });
        var attachment = await CreateAsync(admin, "/attachments", new { deviceId = device, assetId = asset });
        await admin.PostAsync($"/attachments/{attachment}/detach", null, CancellationToken);
        await CreateAsync(admin, "/attachments", new { deviceId = device, assetId = asset });

        using var deleted = await admin.DeleteAsync($"/assets/{asset}", CancellationToken);

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ListAsync(admin, "/assets?includeArchived=true")).ShouldBeEmpty();
        (await ListAsync(admin, "/attachments")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Archiving_an_Asset_ends_its_Attachment()
    {
        await using var api = ApiWhereEverythingHasReadings();
        using var admin = await AdminClientAsync(api);
        var device = await CreateAsync(admin, "/devices", new { serialNumber = "TRK-007" });
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });
        var attachment = await CreateAsync(admin, "/attachments", new { deviceId = device, assetId = asset });

        using var deleted = await admin.DeleteAsync($"/assets/{asset}", CancellationToken);

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ListAsync(admin, "/attachments")).Select(DescribeAttachment).ShouldBe([(attachment, device, asset, true)]);
        // Ending the Attachment frees the Device for another Asset.
        var otherAsset = await CreateAsync(admin, "/assets", new { name = "Haul truck 8", type = "Truck" });
        using var reattached = await admin.PostAsJsonAsync("/attachments", new { deviceId = device, assetId = otherAsset }, CancellationToken);
        reattached.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Deleting_an_unknown_Device_is_not_found()
    {
        using var admin = await AdminClientAsync();

        using var response = await admin.DeleteAsync($"/devices/{Guid.NewGuid()}", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Operators_read_Devices_and_Attachments_but_cannot_change_them()
    {
        using var admin = await AdminClientAsync();
        var device = await CreateAsync(admin, "/devices", new { serialNumber = "TRK-007" });
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });
        var attachment = await CreateAsync(admin, "/attachments", new { deviceId = device, assetId = asset });
        await AuthClient.CreateUserAsync(admin, "operator@fieldtrack.test", "Operator", "Operator-pass-1");
        using var client = Fixture.Api.CreateClient();
        await AuthClient.AuthorizeAsync(client, "operator@fieldtrack.test", "Operator-pass-1");

        var devices = await ListAsync(client, "/devices");
        var attachments = await ListAsync(client, "/attachments");
        using var register = await client.PostAsJsonAsync("/devices", new { serialNumber = "GEN-001" }, CancellationToken);
        using var delete = await client.DeleteAsync($"/devices/{device}", CancellationToken);
        using var attach = await client.PostAsJsonAsync("/attachments", new { deviceId = device, assetId = asset }, CancellationToken);
        using var detach = await client.PostAsync($"/attachments/{attachment}/detach", null, CancellationToken);

        devices.Select(DescribeDevice).ShouldBe([("TRK-007", false)]);
        attachments.Select(DescribeAttachment).ShouldBe([(attachment, device, asset, false)]);
        new[] { register, delete, attach, detach }
            .Select(response => response.StatusCode)
            .ShouldAllBe(status => status == HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("/devices")]
    [InlineData("/attachments")]
    public async Task Nobody_signed_out_reads_Devices_or_Attachments(string path)
    {
        using var client = Fixture.Api.CreateClient();

        using var response = await client.GetAsync(path, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static async Task<string?> ProblemDetailAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("detail").GetString();

    private static (string?, bool) DescribeDevice(JsonElement device) => (
        device.GetProperty("serialNumber").GetString(),
        device.GetProperty("archived").GetBoolean());

    /// <summary>Id, Device, Asset, and whether the Attachment has ended.</summary>
    private static (string?, string?, string?, bool) DescribeAttachment(JsonElement attachment) => (
        attachment.GetProperty("id").GetString(),
        attachment.GetProperty("deviceId").GetString(),
        attachment.GetProperty("assetId").GetString(),
        attachment.GetProperty("detachedAt").ValueKind != JsonValueKind.Null);

    private static DateTimeOffset TimeOf(JsonElement attachment, string property) =>
        attachment.GetProperty(property).GetDateTimeOffset();
}
