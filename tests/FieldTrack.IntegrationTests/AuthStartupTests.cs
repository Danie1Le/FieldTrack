using System.Net;
using FieldTrack.Api.Auth;
using FieldTrack.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static FieldTrack.IntegrationTests.FieldTrackApiFactory;

namespace FieldTrack.IntegrationTests;

public sealed class AuthStartupTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Startup_creates_no_second_Admin_when_one_exists()
    {
        await using var api = Fixture.Api.WithWebHostBuilder(builder => builder
            .UseSetting(UserSeeder.AdminEmailKey, "second-admin@fieldtrack.test")
            .UseSetting(UserSeeder.AdminPasswordKey, "Second-admin-pass-1"));
        using var client = api.CreateClient();

        using var second = await AuthClient.PostSignInAsync(client, "second-admin@fieldtrack.test", "Second-admin-pass-1");
        using var first = await AuthClient.PostSignInAsync(client, AdminEmail, AdminPassword);

        second.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Startup_leaves_an_Operator_alone_when_no_Admin_exists_and_the_Admin_email_is_theirs()
    {
        await using var scope = Fixture.Api.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<FieldTrackUser>>();
        var demoted = (await users.FindByEmailAsync(AdminEmail)).ShouldNotBeNull();
        await users.RemoveFromRoleAsync(demoted, Roles.Admin);
        await users.AddToRoleAsync(demoted, Roles.Operator);

        await UserSeeder.SeedAsync(Fixture.Api.Services);

        (await users.GetRolesAsync(demoted)).ShouldBe([Roles.Operator]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAA==")] // 128 bits
    public async Task Startup_fails_without_a_256_bit_signing_key(string signingKey)
    {
        await using var api = Fixture.Api.WithWebHostBuilder(builder => builder
            .UseSetting($"{AuthOptions.SectionName}:{nameof(AuthOptions.SigningKey)}", signingKey));

        Should.Throw<OptionsValidationException>(() => api.CreateClient());
    }
}
