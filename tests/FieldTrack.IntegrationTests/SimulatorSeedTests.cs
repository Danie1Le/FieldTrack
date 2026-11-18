using System.Net;
using System.Text.Json;
using FieldTrack.Simulator;
using Microsoft.Extensions.Time.Testing;

namespace FieldTrack.IntegrationTests;

public sealed class SimulatorSeedTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    private const string FleetJson = """
        { "devices": [
          { "serial": "TRK-001", "assetName": "Haul truck 1", "assetType": "Truck" },
          { "serial": "GEN-001", "assetType": "Generator", "position": { "lat": 45.5, "lon": -122.6 } }
        ] }
        """;

    private static FleetFile Fleet(string json = FleetJson) => FleetFile.Parse(json, AppContext.BaseDirectory);

    private async Task<FieldTrackApi> SignInAsync() =>
        await FieldTrackApi.SignInAsync(Fixture.Api.CreateClient(), FieldTrackApiFactory.AdminEmail, FieldTrackApiFactory.AdminPassword, CancellationToken);

    [Fact]
    public async Task Seeding_creates_each_Asset_and_Device_and_attaches_them()
    {
        var api = await SignInAsync();

        await Seeder.SeedAsync(api, Fleet(), TextWriter.Null, CancellationToken);

        using var admin = await AdminClientAsync();
        var assets = await ListAsync(admin, "/assets");
        assets.Select(asset => (asset.GetProperty("name").GetString(), asset.GetProperty("type").GetString()))
            .ShouldBe([("GEN-001", "Generator"), ("Haul truck 1", "Truck")]);
        var devices = await ListAsync(admin, "/devices");
        devices.Select(device => device.GetProperty("serialNumber").GetString()).ShouldBe(["GEN-001", "TRK-001"]);
        var attachments = await ListAsync(admin, "/attachments");
        attachments.Length.ShouldBe(2);
        attachments.ShouldAllBe(attachment => attachment.GetProperty("detachedAt").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Seeding_twice_changes_nothing_the_second_time()
    {
        var api = await SignInAsync();
        await Seeder.SeedAsync(api, Fleet(), TextWriter.Null, CancellationToken);
        using var admin = await AdminClientAsync();
        var before = (await ListAsync(admin, "/attachments")).Select(attachment => attachment.GetRawText()).ToArray();

        await Seeder.SeedAsync(api, Fleet(), TextWriter.Null, CancellationToken);

        (await ListAsync(admin, "/assets")).Length.ShouldBe(2);
        (await ListAsync(admin, "/devices")).Length.ShouldBe(2);
        (await ListAsync(admin, "/attachments")).Select(attachment => attachment.GetRawText()).ShouldBe(before);
    }

    [Fact]
    public async Task Seeding_adds_only_what_a_grown_fleet_file_is_missing()
    {
        var api = await SignInAsync();
        await Seeder.SeedAsync(api, Fleet(), TextWriter.Null, CancellationToken);

        await Seeder.SeedAsync(api, Fleet("""
            { "devices": [
              { "serial": "TRK-001", "assetName": "Haul truck 1", "assetType": "Truck" },
              { "serial": "EXC-001", "assetType": "Excavator" }
            ] }
            """), TextWriter.Null, CancellationToken);

        using var admin = await AdminClientAsync();
        (await ListAsync(admin, "/assets")).Length.ShouldBe(3);
        (await ListAsync(admin, "/attachments")).Length.ShouldBe(3);
    }

    [Fact]
    public async Task Seeding_a_Device_that_is_attached_to_another_Asset_is_refused()
    {
        var api = await SignInAsync();
        await Seeder.SeedAsync(api, Fleet(), TextWriter.Null, CancellationToken);

        var exception = await Should.ThrowAsync<SimulatorException>(() => Seeder.SeedAsync(api, Fleet("""
            { "devices": [ { "serial": "TRK-001", "assetName": "Another truck", "assetType": "Truck" } ] }
            """), TextWriter.Null, CancellationToken));

        exception.Message.ShouldContain("TRK-001");
        exception.Message.ShouldContain("Haul truck 1");
    }

    [Fact]
    public async Task Seeding_an_Asset_whose_Asset_Type_differs_is_refused()
    {
        var api = await SignInAsync();
        await Seeder.SeedAsync(api, Fleet(), TextWriter.Null, CancellationToken);

        var exception = await Should.ThrowAsync<SimulatorException>(() => Seeder.SeedAsync(api, Fleet("""
            { "devices": [ { "serial": "TRK-001", "assetName": "Haul truck 1", "assetType": "Excavator" } ] }
            """), TextWriter.Null, CancellationToken));

        exception.Message.ShouldContain("Haul truck 1");
        exception.Message.ShouldContain("Truck");
    }

    [Theory]
    [InlineData("Excavator", "exists as a Truck")]
    [InlineData("Truck", "already has Device TRK-001")]
    public async Task A_new_Device_whose_Asset_conflicts_is_refused_and_not_created(string assetType, string reason)
    {
        var api = await SignInAsync();
        await Seeder.SeedAsync(api, Fleet(), TextWriter.Null, CancellationToken);

        var exception = await Should.ThrowAsync<SimulatorException>(() => Seeder.SeedAsync(api, Fleet($$"""
            { "devices": [ { "serial": "TRK-002", "assetName": "Haul truck 1", "assetType": "{{assetType}}" } ] }
            """), TextWriter.Null, CancellationToken));

        exception.Message.ShouldContain(reason);
        using var admin = await AdminClientAsync();
        (await ListAsync(admin, "/devices")).Select(device => device.GetProperty("serialNumber").GetString()).ShouldBe(["GEN-001", "TRK-001"]);
    }

    [Fact]
    public async Task Signing_in_with_a_temporary_password_tells_the_developer_to_change_it()
    {
        await WithUsersAsync(async users =>
        {
            var admin = (await users.FindByEmailAsync(FieldTrackApiFactory.AdminEmail)).ShouldNotBeNull();
            admin.MustChangePassword = true;
            (await users.UpdateAsync(admin)).Succeeded.ShouldBeTrue();
        });

        var exception = await Should.ThrowAsync<SimulatorException>(() =>
            FieldTrackApi.SignInAsync(Fixture.Api.CreateClient(), FieldTrackApiFactory.AdminEmail, FieldTrackApiFactory.AdminPassword, CancellationToken));

        exception.Message.ShouldContain("/auth/change-password");
        exception.Message.ShouldNotContain(FieldTrackApiFactory.AdminPassword);
    }

    [Fact]
    public async Task Signing_in_with_the_temporary_password_replaces_it_with_the_final_one()
    {
        const string final = "Final-password-1!";
        await WithUsersAsync(async users =>
        {
            var admin = (await users.FindByEmailAsync(FieldTrackApiFactory.AdminEmail)).ShouldNotBeNull();
            admin.MustChangePassword = true;
            (await users.UpdateAsync(admin)).Succeeded.ShouldBeTrue();
        });

        var api = await FieldTrackApi.SignInReplacingTemporaryPasswordAsync(
            Fixture.Api.CreateClient(), FieldTrackApiFactory.AdminEmail, FieldTrackApiFactory.AdminPassword, final, CancellationToken);

        (await api.ListAssetsAsync(CancellationToken)).ShouldBeEmpty();
        await FieldTrackApi.SignInAsync(Fixture.Api.CreateClient(), FieldTrackApiFactory.AdminEmail, final, CancellationToken);
    }

    [Fact]
    public async Task Signing_in_with_a_temporary_password_already_replaced_signs_in_with_the_final_one_and_leaves_no_failed_attempt()
    {
        var api = await FieldTrackApi.SignInReplacingTemporaryPasswordAsync(
            Fixture.Api.CreateClient(), FieldTrackApiFactory.AdminEmail, "Old-temporary-1!", FieldTrackApiFactory.AdminPassword, CancellationToken);

        (await api.ListAssetsAsync(CancellationToken)).ShouldBeEmpty();
        // Restarts sign in this way every time; failed attempts that stayed counted would lock the Admin out.
        await WithUsersAsync(async users =>
            (await users.FindByEmailAsync(FieldTrackApiFactory.AdminEmail)).ShouldNotBeNull().AccessFailedCount.ShouldBe(0));
    }

    [Fact]
    public async Task An_unattended_simulator_whose_Admin_is_locked_out_signs_in_once_the_lockout_ends()
    {
        var environment = new Dictionary<string, string>
        {
            ["FIELDTRACK_ADMIN_PASSWORD"] = FieldTrackApiFactory.AdminPassword,
            ["FIELDTRACK_ADMIN_TEMPORARY_PASSWORD"] = "Old-temporary-1!",
        };
        await WithUsersAsync(async users => (await users.SetLockoutEndDateAsync(
            (await users.FindByEmailAsync(FieldTrackApiFactory.AdminEmail)).ShouldNotBeNull(), DateTimeOffset.MaxValue)).Succeeded.ShouldBeTrue());
        var time = new FakeTimeProvider();
        var error = new StringWriter();

        // A container platform restarts a simulator that exits, and each restart's failed sign-ins count towards a lockout,
        // so the simulator retries in-process: never sooner than the 15-minute lockout, and never gives up.
        var signIn = SimulatorCli.SignInAsync(
            Fixture.Api.CreateClient(), Options.Parse(["--email", FieldTrackApiFactory.AdminEmail]), error, CancellationToken, environment.GetValueOrDefault, time);
        (await EventuallyAsync(() => Task.FromResult(error.ToString()), log => log.Contains("Retrying in 16 minutes"))).ShouldContain("Retrying in 16 minutes");
        time.Advance(TimeSpan.FromMinutes(15));
        await Task.Delay(200, CancellationToken);
        signIn.IsCompleted.ShouldBeFalse();

        await WithUsersAsync(async users => (await users.SetLockoutEndDateAsync(
            (await users.FindByEmailAsync(FieldTrackApiFactory.AdminEmail)).ShouldNotBeNull(), null)).Succeeded.ShouldBeTrue());
        time.Advance(TimeSpan.FromMinutes(1));

        var api = await signIn.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken);
        (await api.ListAssetsAsync(CancellationToken)).ShouldBeEmpty();
        error.ToString().ShouldNotContain(FieldTrackApiFactory.AdminEmail);
    }

    [Fact]
    public async Task An_unattended_simulator_backs_off_while_its_sign_in_stays_refused()
    {
        var environment = new Dictionary<string, string>
        {
            ["FIELDTRACK_ADMIN_PASSWORD"] = "Wrong-password-1",
            ["FIELDTRACK_ADMIN_TEMPORARY_PASSWORD"] = "Wrong-password-2",
        };
        var time = new FakeTimeProvider();
        var error = new StringWriter();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);

        var signIn = SimulatorCli.SignInAsync(
            Fixture.Api.CreateClient(), Options.Parse(["--email", FieldTrackApiFactory.AdminEmail]), error, stop.Token, environment.GetValueOrDefault, time);
        await EventuallyAsync(() => Task.FromResult(error.ToString()), log => log.Contains("Retrying in 16 minutes"));
        time.Advance(TimeSpan.FromMinutes(16));

        (await EventuallyAsync(() => Task.FromResult(error.ToString()), log => log.Contains("Retrying in 32 minutes"))).ShouldContain("Retrying in 32 minutes");
        await stop.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => signIn);
    }

    [Fact]
    public async Task Signing_in_with_a_wrong_password_is_refused()
    {
        var exception = await Should.ThrowAsync<SimulatorException>(() =>
            FieldTrackApi.SignInAsync(Fixture.Api.CreateClient(), FieldTrackApiFactory.AdminEmail, "Wrong-password-1", CancellationToken));

        exception.Message.ShouldContain("401");
    }

    [Fact]
    public async Task A_Device_the_API_has_Archived_cannot_be_seeded_again()
    {
        await using var api = ApiWhereEverythingHasReadings();
        var seedApi = await FieldTrackApi.SignInAsync(api.CreateClient(), FieldTrackApiFactory.AdminEmail, FieldTrackApiFactory.AdminPassword, CancellationToken);
        await Seeder.SeedAsync(seedApi, Fleet(), TextWriter.Null, CancellationToken);
        using var admin = await AdminClientAsync(api);
        foreach (var device in await ListAsync(admin, "/devices"))
        {
            using var deleted = await admin.DeleteAsync($"/devices/{device.GetProperty("id").GetString()}", CancellationToken);
            deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var exception = await Should.ThrowAsync<SimulatorException>(() => Seeder.SeedAsync(seedApi, Fleet(), TextWriter.Null, CancellationToken));

        exception.Message.ShouldContain("Archived");
    }
}
