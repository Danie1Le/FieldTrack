using System.Globalization;
using System.Net;
using FieldTrack.Api.Auth;
using FieldTrack.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using static FieldTrack.IntegrationTests.FieldTrackApiFactory;

namespace FieldTrack.IntegrationTests;

public sealed class SignInTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Admin_signs_in_with_email_and_password_and_gets_a_15_minute_access_token_and_a_refresh_token()
    {
        using var client = Fixture.Api.CreateClient();

        var tokens = await AuthClient.SignInAsync(client, AdminEmail, AdminPassword);

        var accessToken = new JsonWebToken(tokens.AccessToken);
        accessToken.Alg.ShouldBe("HS256");
        (accessToken.ValidTo - accessToken.IssuedAt).ShouldBe(TimeSpan.FromMinutes(15));
        AuthClient.RolesOf(tokens.AccessToken).ShouldBe(["Admin"]);
        accessToken.GetClaim("email").Value.ShouldBe(AdminEmail);
        tokens.AccessTokenExpiresAt.ShouldBe(new DateTimeOffset(accessToken.ValidTo, TimeSpan.Zero));
        tokens.RefreshToken.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Wrong_password_is_refused()
    {
        using var client = Fixture.Api.CreateClient();

        using var response = await AuthClient.PostSignInAsync(client, AdminEmail, "Wrong-pass-1");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_out()
    {
        using var client = Fixture.Api.CreateClient();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var failed = await AuthClient.PostSignInAsync(client, AdminEmail, "Wrong-pass-1");
        }

        using var response = await AuthClient.PostSignInAsync(client, AdminEmail, AdminPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Four_wrong_passwords_do_not_lock_the_account_out()
    {
        using var client = Fixture.Api.CreateClient();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var failed = await AuthClient.PostSignInAsync(client, AdminEmail, "Wrong-pass-1");
        }

        using var response = await AuthClient.PostSignInAsync(client, AdminEmail, AdminPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Sign_in_attempts_beyond_the_rate_limit_are_refused_with_429()
    {
        await using var api = Fixture.Api.WithWebHostBuilder(builder =>
            builder.UseSetting($"{AuthOptions.SectionName}:{nameof(AuthOptions.SignInPermitsPerMinute)}", "2"));
        using var client = api.CreateClient();

        using var first = await AuthClient.PostSignInAsync(client, AdminEmail, AdminPassword);
        using var second = await AuthClient.PostSignInAsync(client, AdminEmail, AdminPassword);
        using var third = await AuthClient.PostSignInAsync(client, AdminEmail, AdminPassword);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Behind_the_dashboard_proxy_each_client_address_gets_its_own_sign_in_rate_limit()
    {
        await using var api = BehindProxy();
        // nginx sends the client's address; the API's own ingress appends nginx's (10.0.0.7) and connects from 10.0.0.5.
        using var client = ClientFrom(api, peer: "10.0.0.5", forwardedFor: "203.0.113.1, 10.0.0.7");
        using var otherClient = ClientFrom(api, peer: "10.0.0.5", forwardedFor: "203.0.113.2, 10.0.0.7");

        using var first = await AuthClient.PostSignInAsync(client, AdminEmail, AdminPassword);
        using var second = await AuthClient.PostSignInAsync(client, AdminEmail, AdminPassword);
        using var other = await AuthClient.PostSignInAsync(otherClient, AdminEmail, AdminPassword);
        using var third = await AuthClient.PostSignInAsync(client, AdminEmail, AdminPassword);

        other.StatusCode.ShouldBe(HttpStatusCode.OK);
        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Theory]
    // A peer outside the trusted networks: someone reaching the API directly.
    [InlineData("198.51.100.9", "{0}")]
    // A trusted peer, but the varying address sits beyond the two proxy hops: a client's own claim.
    [InlineData("10.0.0.5", "{0}, 203.0.113.50, 10.0.0.7")]
    // Every hop is trusted, so only ForwardLimit = 2 stops the walk at 10.0.0.8; without it the varying address is believed.
    [InlineData("10.0.0.5", "{0}, 10.0.0.8, 10.0.0.7")]
    public async Task X_Forwarded_For_that_no_trusted_proxy_vouches_for_does_not_escape_the_sign_in_rate_limit(string peer, string chain)
    {
        await using var api = BehindProxy();
        using var first = ClientFrom(api, peer, string.Format(CultureInfo.InvariantCulture, chain, "192.0.2.1"));
        using var second = ClientFrom(api, peer, string.Format(CultureInfo.InvariantCulture, chain, "192.0.2.2"));
        using var third = ClientFrom(api, peer, string.Format(CultureInfo.InvariantCulture, chain, "192.0.2.3"));

        using var one = await AuthClient.PostSignInAsync(first, AdminEmail, AdminPassword);
        using var two = await AuthClient.PostSignInAsync(second, AdminEmail, AdminPassword);
        using var three = await AuthClient.PostSignInAsync(third, AdminEmail, AdminPassword);

        two.StatusCode.ShouldBe(HttpStatusCode.OK);
        three.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    // Two sign-ins a minute, behind proxies in 10.0.0.0/8, the way the Azure demo configures its private ranges.
    private WebApplicationFactory<Program> BehindProxy() =>
        Fixture.Api.WithWebHostBuilder(builder => builder
            .UseSetting($"{AuthOptions.SectionName}:{nameof(AuthOptions.SignInPermitsPerMinute)}", "2")
            .UseSetting($"{Program.TrustedProxyNetworksKey}:0", "10.0.0.0/8")
            .ConfigureTestServices(services => services.AddSingleton<IStartupFilter, PeerFromHeader>()));

    private static HttpClient ClientFrom(WebApplicationFactory<Program> api, string peer, string forwardedFor)
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Add(PeerFromHeader.Header, peer);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
        return client;
    }

    [Fact]
    public async Task Unknown_email_is_refused_like_a_wrong_password()
    {
        using var client = Fixture.Api.CreateClient();

        using var response = await AuthClient.PostSignInAsync(client, "nobody@fieldtrack.test", AdminPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unknown_email_and_locked_out_account_cost_a_password_hash_like_a_wrong_password()
    {
        var hasher = new CountingPasswordHasher();
        await using var api = Fixture.Api.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IPasswordHasher<FieldTrackUser>>(hasher)));
        using var client = api.CreateClient();

        using var unknown = await AuthClient.PostSignInAsync(client, "nobody@fieldtrack.test", AdminPassword);
        var afterUnknown = hasher.Verifications;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var failed = await AuthClient.PostSignInAsync(client, AdminEmail, "Wrong-pass-1");
        }

        var afterLockout = hasher.Verifications;
        using var lockedOut = await AuthClient.PostSignInAsync(client, AdminEmail, AdminPassword);

        afterUnknown.ShouldBe(1);
        lockedOut.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (hasher.Verifications - afterLockout).ShouldBe(1);
    }

    /// <summary>The test server has no TCP connection; this gives each request the peer address its test header names.</summary>
    private sealed class PeerFromHeader : IStartupFilter
    {
        public const string Header = "X-Test-Peer";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers[Header].ToString());
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    /// <summary>Identity's hasher, counting how often a password is checked.</summary>
    private sealed class CountingPasswordHasher : IPasswordHasher<FieldTrackUser>
    {
        private readonly PasswordHasher<FieldTrackUser> _hasher = new();
        private int _verifications;

        public int Verifications => _verifications;

        public string HashPassword(FieldTrackUser user, string password) => _hasher.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(FieldTrackUser user, string hashedPassword, string providedPassword)
        {
            Interlocked.Increment(ref _verifications);
            return _hasher.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }
}
