using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldTrack.Api.Assets;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FieldTrack.IntegrationTests;

public sealed class ExportTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    private const string Header = "device_serial,boot,seq,device_time,received_time,lat,lon,speed_kph,engine_temp_c,fuel_pct,vibration_mm_s,battery_v";

    // Partitions exist from 7 days back, so yesterday is always writable.
    private static readonly DateTimeOffset Yesterday = new(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero);

    [Fact]
    public async Task Export_streams_every_Reading_in_the_range_as_CSV_ordered_by_Device_time()
    {
        using var admin = await AdminClientAsync();
        var asset = await CreateAsync(admin, "/assets", new { name = "Haul truck: 7/B", type = "Truck" });
        var first = await CreateAsync(admin, "/devices", new { serialNumber = "TRK-1" });
        // A serial a spreadsheet would read as a formula; the swap mid-range is why the serial is a column.
        var second = await CreateAsync(admin, "/devices", new { serialNumber = "-A1" });
        await using var database = await OpenDatabaseAsync();
        await SqlAsync(database, """
            INSERT INTO readings (device_id, boot, seq, asset_id, device_time, received_time, location,
                                  speed_kph, engine_temp_c, fuel_pct, vibration_mm_s, battery_v)
            VALUES (@p0, 3, 7, @p2, @p3 + interval '2 hours', @p3 + interval '2 hours 0.123456 seconds', ST_SetSRID(ST_MakePoint(18.5, -33.9), 4326)::geography,
                    42.5, 91.25, 63, 2.5, 12.75),
                   (@p1, 1, 0, @p2, @p3 + interval '5 hours', @p3 + interval '5 hours 1 second', ST_SetSRID(ST_MakePoint(-0.1, 51.5), 4326)::geography,
                    NULL, NULL, NULL, NULL, NULL),
                   (@p0, 3, 6, @p2, @p3 + interval '1 hour', @p3 + interval '1 hour', ST_SetSRID(ST_MakePoint(0, 0), 4326)::geography,
                    1, 2, 3, 4, 5),
                   (@p0, 3, 8, @p2, @p3 + interval '1 day', @p3 + interval '1 day', ST_SetSRID(ST_MakePoint(0, 0), 4326)::geography,
                    NULL, NULL, NULL, NULL, NULL)
            """, Guid.Parse(first), Guid.Parse(second), Guid.Parse(asset), Yesterday);

        var ticket = await PostExportAsync(admin, asset, Yesterday, Yesterday.AddDays(1));
        using var anonymous = Fixture.Api.CreateClient();
        using var response = await anonymous.GetAsync(ticket, HttpCompletionOption.ResponseHeadersRead, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        response.Content.Headers.ContentLength.ShouldBeNull();
        var day = Yesterday.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var nextDay = Yesterday.AddDays(1).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        response.Content.Headers.ContentDisposition!.FileNameStar.ShouldBe($"Haul truck_ 7_B_{day}T000000Z_{nextDay}T000000Z.csv");
        var csv = await response.Content.ReadAsStringAsync(CancellationToken);
        var date = Yesterday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        csv.ShouldBe(string.Join('\n',
            Header,
            $"TRK-1,3,6,{date}T01:00:00.000000Z,{date}T01:00:00.000000Z,0,0,1,2,3,4,5",
            $"TRK-1,3,7,{date}T02:00:00.000000Z,{date}T02:00:00.123456Z,-33.9,18.5,42.5,91.25,63,2.5,12.75",
            $"'-A1,1,0,{date}T05:00:00.000000Z,{date}T05:00:01.000000Z,51.5,-0.1,,,,,",
            ""));
    }

    [Fact]
    public async Task Export_of_several_days_holds_every_Reading_once_under_one_header()
    {
        using var admin = await AdminClientAsync();
        var asset = await CreateAsync(admin, "/assets", new { name = "Generator 1", type = "Generator" });
        var device = await CreateAsync(admin, "/devices", new { serialNumber = "GEN-1" });
        var from = Yesterday.AddDays(-2).AddHours(6);
        await using var database = await OpenDatabaseAsync();
        // One Reading every 30 minutes for three days from `from`, Sequence numbers 0 to 144: the range ends on the last.
        await SqlAsync(database, """
            INSERT INTO readings (device_id, boot, seq, asset_id, device_time, received_time, location)
            SELECT @p0, 1, g, @p1, @p2 + g * interval '30 minutes', now(), ST_SetSRID(ST_MakePoint(0, 0), 4326)::geography
            FROM generate_series(0, 144) g
            """, Guid.Parse(device), Guid.Parse(asset), from);

        var ticket = await PostExportAsync(admin, asset, from, from.AddDays(3));
        using var anonymous = Fixture.Api.CreateClient();
        var lines = (await anonymous.GetStringAsync(ticket, CancellationToken)).Split('\n');

        lines[0].ShouldBe(Header);
        lines[1..^1].Select(line => long.Parse(line.Split(',')[2], CultureInfo.InvariantCulture)).ShouldBe(Enumerable.Range(0, 144).Select(seq => (long)seq));
        lines[^1].ShouldBeEmpty();
    }

    [Fact]
    public async Task Export_of_a_range_ending_near_the_last_representable_time_downloads_without_error()
    {
        using var admin = await AdminClientAsync();
        var asset = await CreateAsync(admin, "/assets", new { name = "Generator 1", type = "Generator" });
        var from = new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero);
        var ticket = await PostExportAsync(admin, asset, from, from.AddHours(12));
        using var anonymous = Fixture.Api.CreateClient();

        using var response = await anonymous.GetAsync(ticket, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldBe(Header + "\n");
    }

    [Fact]
    public async Task Export_leaves_out_another_Assets_Readings_in_the_same_range()
    {
        using var admin = await AdminClientAsync();
        var asset = await CreateAsync(admin, "/assets", new { name = "Generator 1", type = "Generator" });
        var other = await CreateAsync(admin, "/assets", new { name = "Generator 2", type = "Generator" });
        var device = await CreateAsync(admin, "/devices", new { serialNumber = "GEN-1" });
        await using var database = await OpenDatabaseAsync();
        await SqlAsync(database, """
            INSERT INTO readings (device_id, boot, seq, asset_id, device_time, received_time, location)
            VALUES (@p0, 1, 1, @p1, @p3 + interval '1 hour', now(), ST_SetSRID(ST_MakePoint(0, 0), 4326)::geography),
                   (@p0, 1, 2, @p2, @p3 + interval '2 hours', now(), ST_SetSRID(ST_MakePoint(0, 0), 4326)::geography)
            """, Guid.Parse(device), Guid.Parse(asset), Guid.Parse(other), Yesterday);

        var ticket = await PostExportAsync(admin, asset, Yesterday, Yesterday.AddDays(1));
        using var anonymous = Fixture.Api.CreateClient();
        var lines = (await anonymous.GetStringAsync(ticket, CancellationToken)).Split('\n');

        lines.Length.ShouldBe(3);
        lines[1].Split(',')[2].ShouldBe("1");
    }

    [Fact]
    public async Task An_export_download_asks_nginx_not_to_buffer_it()
    {
        using var admin = await AdminClientAsync();
        var asset = await CreateAsync(admin, "/assets", new { name = "Generator 1", type = "Generator" });
        var ticket = await PostExportAsync(admin, asset, Yesterday, Yesterday.AddDays(1));
        using var anonymous = Fixture.Api.CreateClient();

        using var response = await anonymous.GetAsync(ticket, CancellationToken);

        response.Headers.GetValues("X-Accel-Buffering").ShouldBe(["no"]);
    }

    [Fact]
    public async Task An_export_URL_downloads_once()
    {
        using var admin = await AdminClientAsync();
        var asset = await CreateAsync(admin, "/assets", new { name = "Generator 1", type = "Generator" });
        var ticket = await PostExportAsync(admin, asset, Yesterday, Yesterday.AddDays(1));
        using var anonymous = Fixture.Api.CreateClient();

        using var first = await anonymous.GetAsync(ticket, CancellationToken);
        using var second = await anonymous.GetAsync(ticket, CancellationToken);
        using var forged = await anonymous.GetAsync("/exports?token=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", CancellationToken);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await first.Content.ReadAsStringAsync(CancellationToken)).ShouldBe(Header + "\n");
        second.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        forged.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_export_URL_works_for_60_s()
    {
        var time = new AdjustableTimeProvider();
        await using var api = Fixture.Api.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(time)));
        using var admin = await AdminClientAsync(api);
        var asset = await CreateAsync(admin, "/assets", new { name = "Generator 1", type = "Generator" });
        var inTime = await PostExportAsync(admin, asset, Yesterday, Yesterday.AddDays(1));
        var late = await PostExportAsync(admin, asset, Yesterday, Yesterday.AddDays(1));
        using var anonymous = api.CreateClient();

        time.Offset = ExportsController.UrlLifetime - TimeSpan.FromSeconds(1);
        using var beforeExpiry = await anonymous.GetAsync(inTime, CancellationToken);
        time.Offset = ExportsController.UrlLifetime + TimeSpan.FromSeconds(1);
        using var afterExpiry = await anonymous.GetAsync(late, CancellationToken);

        beforeExpiry.StatusCode.ShouldBe(HttpStatusCode.OK);
        afterExpiry.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Export_refuses_a_range_over_31_days_with_a_message_to_split_it()
    {
        using var admin = await AdminClientAsync();
        var asset = await CreateAsync(admin, "/assets", new { name = "Generator 1", type = "Generator" });

        using var tooLong = await admin.PostAsJsonAsync(
            $"/assets/{asset}/exports", new { from = Yesterday, to = Yesterday.AddDays(31).AddSeconds(1) }, CancellationToken);
        using var backwards = await admin.PostAsJsonAsync($"/assets/{asset}/exports", new { from = Yesterday, to = Yesterday }, CancellationToken);
        using var unknown = await admin.PostAsJsonAsync(
            $"/assets/{Guid.NewGuid()}/exports", new { from = Yesterday, to = Yesterday.AddDays(1) }, CancellationToken);

        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await tooLong.Content.ReadAsStringAsync(CancellationToken)).ShouldContain("split");
        backwards.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await PostExportAsync(admin, asset, Yesterday, Yesterday.AddDays(31));
    }

    [Fact]
    public async Task A_user_runs_at_most_two_exports_at_once_and_Operators_export_too()
    {
        using var admin = await AdminClientAsync();
        var asset = await CreateAsync(admin, "/assets", new { name = "Generator 1", type = "Generator" });
        await AuthClient.CreateUserAsync(admin, "operator@fieldtrack.test", "Operator", "Operator-pass-1");
        using var operatorClient = Fixture.Api.CreateClient();
        await AuthClient.AuthorizeAsync(operatorClient, "operator@fieldtrack.test", "Operator-pass-1");
        var tickets = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            tickets.Add(await PostExportAsync(admin, asset, Yesterday, Yesterday.AddDays(1)));
        }

        using var anonymous = Fixture.Api.CreateClient();
        await using var database = await OpenDatabaseAsync();
        // Hold every Reading still, so two exports start and wait inside their COPY.
        await using (var hold = await database.BeginTransactionAsync(CancellationToken))
        {
            await SqlAsync(database, "LOCK TABLE readings IN ACCESS EXCLUSIVE MODE");
            var running = tickets.Take(2).Select(ticket => anonymous.GetAsync(ticket, CancellationToken)).ToList();
            await LockWaitsAsync(2);

            using var third = await anonymous.GetAsync(tickets[2], CancellationToken);
            using var another = await admin.PostAsJsonAsync($"/assets/{asset}/exports", new { from = Yesterday, to = Yesterday.AddDays(1) }, CancellationToken);
            var operatorTicket = await PostExportAsync(operatorClient, asset, Yesterday, Yesterday.AddDays(1));

            third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
            another.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
            await hold.CommitAsync(CancellationToken);
            foreach (var response in await Task.WhenAll(running))
            {
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
                response.Dispose();
            }

            using var operatorExport = await anonymous.GetAsync(operatorTicket, CancellationToken);
            operatorExport.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // A refused download leaves its URL unused, and finished exports free their slots.
        using var retried = await anonymous.GetAsync(tickets[2], CancellationToken);
        retried.StatusCode.ShouldBe(HttpStatusCode.OK);
        await PostExportAsync(admin, asset, Yesterday, Yesterday.AddDays(1));
    }

    private static async Task<string> PostExportAsync(HttpClient client, string asset, DateTimeOffset from, DateTimeOffset to)
    {
        using var response = await client.PostAsJsonAsync($"/assets/{asset}/exports", new { from, to }, CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("url").GetString()!;
    }
}
