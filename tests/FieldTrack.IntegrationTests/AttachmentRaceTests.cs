using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FieldTrack.IntegrationTests;

/// <summary>
/// Concurrent Admin requests against the one-open-Attachment and delete rules. Every assertion is on the
/// final state or on "no 500", so it holds for any interleaving; the partial unique indexes would turn a
/// lost lock into a 500 instead of a 409.
/// </summary>
public sealed class AttachmentRaceTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    private const int Contenders = 8;
    private const int Rounds = 5;

    [Fact]
    public async Task One_Device_attached_to_many_Assets_at_once_is_attached_to_one()
    {
        using var admin = await AdminClientAsync();
        var device = await CreateAsync(admin, "/devices", new { serialNumber = "TRK-007" });
        var assets = await CreateManyAsync(admin, "/assets", i => new { name = $"Haul truck {i}", type = "Truck" });

        var statuses = await Task.WhenAll(assets.Select(asset => PostAsync(admin, "/attachments", new { deviceId = device, assetId = asset })));

        ExactlyOneCreated(statuses);
        (await ListAsync(admin, $"/attachments?deviceId={device}")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Many_Devices_attached_to_one_Asset_at_once_is_one_attached()
    {
        using var admin = await AdminClientAsync();
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck 7", type = "Truck" });
        var devices = await CreateManyAsync(admin, "/devices", i => new { serialNumber = $"TRK-{i}" });

        var statuses = await Task.WhenAll(devices.Select(device => PostAsync(admin, "/attachments", new { deviceId = device, assetId = asset })));

        ExactlyOneCreated(statuses);
        (await ListAsync(admin, $"/attachments?assetId={asset}")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task One_serial_number_registered_at_once_makes_one_Device()
    {
        using var admin = await AdminClientAsync();

        var statuses = await Task.WhenAll(Enumerable.Range(0, Contenders).Select(_ => PostAsync(admin, "/devices", new { serialNumber = "TRK-007" })));

        ExactlyOneCreated(statuses);
        (await ListAsync(admin, "/devices?includeArchived=true")).ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Deleting_a_Device_or_Asset_while_it_is_being_attached_leaves_no_open_Attachment_on_it(bool deleteAsset, bool hasReadings)
    {
        await using var archiving = hasReadings ? ApiWhereEverythingHasReadings() : null;
        using var admin = await AdminClientAsync(archiving);
        var (deleted, deletedId) = deleteAsset ? ("assets", "assetId") : ("devices", "deviceId");

        for (var round = 0; round < Rounds; round++)
        {
            var device = await CreateAsync(admin, "/devices", new { serialNumber = $"TRK-{round}" });
            var asset = await CreateAsync(admin, "/assets", new { name = $"Haul truck {round}", type = "Truck" });
            var target = deleteAsset ? asset : device;

            var statuses = await Task.WhenAll(
                PostAsync(admin, "/attachments", new { deviceId = device, assetId = asset }),
                DeleteAsync(admin, $"/{deleted}/{target}"));

            // Attach loses with 400 (row gone) or 409 (Archived), or wins first and is then ended or removed.
            statuses[0].ShouldBeOneOf(HttpStatusCode.Created, HttpStatusCode.BadRequest, HttpStatusCode.Conflict);
            statuses[1].ShouldBe(HttpStatusCode.NoContent);
            (await ListAsync(admin, $"/attachments?{deletedId}={target}")).ShouldAllBe(attachment => IsEnded(attachment));
        }
    }

    [Fact]
    public async Task Deleting_a_Device_and_its_Asset_at_once_when_they_share_ended_Attachments_deletes_both()
    {
        using var admin = await AdminClientAsync();

        for (var round = 0; round < Rounds; round++)
        {
            var device = await CreateAsync(admin, "/devices", new { serialNumber = $"TRK-{round}" });
            var asset = await CreateAsync(admin, "/assets", new { name = $"Haul truck {round}", type = "Truck" });
            for (var attachment = 0; attachment < 3; attachment++)
            {
                var id = await CreateAsync(admin, "/attachments", new { deviceId = device, assetId = asset });
                (await PostAsync(admin, $"/attachments/{id}/detach", null)).ShouldBe(HttpStatusCode.OK);
            }

            var statuses = await Task.WhenAll(DeleteAsync(admin, $"/devices/{device}"), DeleteAsync(admin, $"/assets/{asset}"));

            statuses.ShouldAllBe(status => status == HttpStatusCode.NoContent);
        }
    }

    private static void ExactlyOneCreated(HttpStatusCode[] statuses)
    {
        statuses.Count(status => status == HttpStatusCode.Created).ShouldBe(1);
        statuses.Count(status => status == HttpStatusCode.Conflict).ShouldBe(Contenders - 1);
    }

    private static bool IsEnded(JsonElement attachment) => attachment.GetProperty("detachedAt").ValueKind != JsonValueKind.Null;

    private static async Task<string[]> CreateManyAsync(HttpClient client, string path, Func<int, object> body) =>
        await Task.WhenAll(Enumerable.Range(0, Contenders).Select(i => CreateAsync(client, path, body(i))));

    private static async Task<HttpStatusCode> PostAsync(HttpClient client, string path, object? body)
    {
        await Task.Yield();
        using var response = await client.PostAsJsonAsync(path, body, CancellationToken);
        return response.StatusCode;
    }

    private static async Task<HttpStatusCode> DeleteAsync(HttpClient client, string path)
    {
        await Task.Yield();
        using var response = await client.DeleteAsync(path, CancellationToken);
        return response.StatusCode;
    }
}
