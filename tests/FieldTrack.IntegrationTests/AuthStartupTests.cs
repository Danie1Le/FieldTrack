using System.Net;
using FieldTrack.Api.Auth;
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
