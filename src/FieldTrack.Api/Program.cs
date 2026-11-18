using FieldTrack.Api;
using FieldTrack.Api.Auth;
using FieldTrack.Api.Health;
using FieldTrack.Api.Ingest;
using FieldTrack.Api.Live;
using FieldTrack.Api.Maintenance;
using FieldTrack.Api.Observability;
using FieldTrack.Infrastructure.Messaging;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddFieldTrackObservability();

builder.Services.AddFieldTrackDatabase(builder.Configuration);
builder.Services.AddOptions<BrokerOptions>()
    .BindConfiguration(BrokerOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<FieldTrackDbContext>("database")
    .AddCheck<BrokerHealthCheck>("broker");
// Bound every check so an unreachable dependency reports Unhealthy before a probe gives up.
// AddDbContextCheck takes no timeout, so set it on the registrations.
builder.Services.PostConfigure<HealthCheckServiceOptions>(options =>
{
    foreach (var registration in options.Registrations)
    {
        registration.Timeout = TimeSpan.FromSeconds(5);
    }
});
builder.Services.AddFieldTrackAuth();
builder.Services.AddHostedService<DatabaseMaintenanceJob>();
builder.Services.AddOptions<IngestOptions>()
    .BindConfiguration(IngestOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddHostedService<IngestWorker>();
// The request-start and request-finished lines include the query string, where SignalR clients
// send their access token. Silence them whatever the configured levels say (#23: no tokens in logs).
// A provider-specific level outranks a provider-less filter, so pin the category for every provider
// that configuration names too.
builder.Services.PostConfigure<LoggerFilterOptions>(options =>
{
    foreach (var provider in options.Rules.Select(rule => rule.ProviderName).Append(null).Distinct().ToList())
    {
        options.Rules.Add(new LoggerFilterRule(provider, "Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning, filter: null));
    }
});
// Enums travel as one member name (e.g. Asset Type "Truck"); numbers and comma-joined names are refused, so only
// defined values get in.
builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new EnumNameConverter()));
builder.Services.AddOpenApi();
builder.Services.AddSignalR()
    .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new EnumNameConverter()));
builder.Services.AddSingleton<IngestMetrics>();
builder.Services.AddSingleton<LivePush>();
// In Azure a browser's request reaches the API through two proxies (ADR 0006): the dashboard's nginx, which sends the
// client's address as the only X-Forwarded-For entry, and the API's own Container Apps ingress, which appends nginx's
// address and connects from inside the environment. So believe at most two entries, and each only while the address
// in front of it is a trusted proxy: a client cannot push its own claim past nginx, and a peer outside the trusted
// networks is never believed. Loopback stays trusted (the ASP.NET Core default).
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    options.ForwardLimit = 2;
    foreach (var network in builder.Configuration.GetSection(Program.TrustedProxyNetworksKey).Get<string[]>() ?? [])
    {
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
    }
});

var app = builder.Build();

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>(Program.MigrateOnStartupKey))
{
    // Development, integration tests and the Azure demo migrate on startup; other deployments migrate as a release step.
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<FieldTrackDbContext>().Database.MigrateAsync();
}

await UserSeeder.SeedAsync(app.Services);

app.UseForwardedHeaders();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// The API docs and health are public in every environment (spec #21: Scalar for the public API docs).
app.MapOpenApi().AllowAnonymous();
app.MapScalarApiReference().AllowAnonymous();
app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = HealthReportWriter.WriteAsync }).AllowAnonymous();
app.MapControllers();
// ADR 0002: a connection ends when its access token does, so a revoked user is cut off within the token's life;
// the dashboard reconnects with a fresh token.
app.MapHub<LiveHub>(LiveHub.Path, options => options.CloseOnAuthenticationExpiration = true);

await app.RunAsync();

/// <summary>The API host entry point; public so integration tests can start it.</summary>
public partial class Program
{
    /// <summary>
    /// Configuration key that makes a host outside Development create and migrate its database at startup; a failed
    /// migration stops the host. The Azure demo sets it: it has no release pipeline to migrate from and runs a single API
    /// replica (ADR 0006). A revision rollover briefly runs two, and EF Core's migration lock serializes their migrations.
    /// </summary>
    public const string MigrateOnStartupKey = "Database:MigrateOnStartup";

    /// <summary>
    /// Configuration key listing the networks (CIDR) of the reverse proxies in front of the API, whose X-Forwarded-For
    /// it believes so the sign-in rate limit is per client rather than per proxy (#169). Unset, the header is ignored.
    /// </summary>
    public const string TrustedProxyNetworksKey = "ForwardedHeaders:TrustedNetworks";
}
