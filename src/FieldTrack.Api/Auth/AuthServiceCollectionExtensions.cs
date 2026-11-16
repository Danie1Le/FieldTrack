using System.Threading.RateLimiting;
using FieldTrack.Infrastructure.Identity;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FieldTrack.Api.Auth;

/// <summary>Registers sign-in, JWT authentication, role policies and the sign-in rate limit (ADR 0002).</summary>
public static class AuthServiceCollectionExtensions
{
    /// <summary>The rate limiter policy on endpoints that check a password.</summary>
    public const string SignInRateLimit = "SignIn";

    /// <summary>
    /// Path prefix of the SignalR hubs. Browsers cannot set headers on WebSocket and SSE requests,
    /// so only here is the access token also accepted from the <c>access_token</c> query parameter.
    /// </summary>
    public const string HubsPath = "/hubs";

    /// <summary>Adds everything the API needs to sign users in and check their tokens and roles.</summary>
    public static IServiceCollection AddFieldTrackAuth(this IServiceCollection services)
    {
        services.AddOptions<AuthOptions>()
            .BindConfiguration(AuthOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(options => options.HasStrongSigningKey(), $"{AuthOptions.SectionName}:{nameof(AuthOptions.SigningKey)} must be base64 of at least 256 bits.")
            .ValidateOnStart();

        services.AddIdentityCore<FieldTrackUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<FieldTrackDbContext>()
            .AddSignInManager();
        services.AddScoped<TokenService>();
        services.AddSingleton(TimeProvider.System);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>>((jwt, auth) =>
            {
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = TokenService.Issuer,
                    ValidAudience = TokenService.Issuer,
                    IssuerSigningKey = auth.Value.CreateSigningKey(),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    NameClaimType = JwtRegisteredClaimNames.Email,
                    RoleClaimType = "role",
                    // The API issues and checks its own tokens on one clock, so expiry is exact.
                    ClockSkew = TimeSpan.Zero,
                };
                jwt.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        if (context.Request.Path.StartsWithSegments(HubsPath))
                        {
                            // Null when absent, and the handler then reads the Authorization header.
                            context.Token = context.Request.Query["access_token"];
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorizationBuilder()
            // Every endpoint requires a signed-in user unless it opts out with AllowAnonymous.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.Read, policy => policy.RequireAuthenticatedUser())
            .AddPolicy(Policies.Manage, policy => policy.RequireRole(Roles.Admin))
            .AddPolicy(Policies.AlertActions, policy => policy.RequireRole(Roles.Admin, Roles.Operator));

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(SignInRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = context.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.SignInPermitsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                }));
        });

        return services;
    }
}
