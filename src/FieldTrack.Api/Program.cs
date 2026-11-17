using FieldTrack.Api;
using FieldTrack.Api.Auth;
using FieldTrack.Api.Health;
using FieldTrack.Api.Ingest;
using FieldTrack.Api.Maintenance;
using FieldTrack.Infrastructure.Messaging;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

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

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    // Development and integration tests migrate on startup; deployed environments migrate as a release step.
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<FieldTrackDbContext>().Database.MigrateAsync();
}

await UserSeeder.SeedAsync(app.Services);

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// The API docs and health are public in every environment (spec #21: Scalar for the public API docs).
app.MapOpenApi().AllowAnonymous();
app.MapScalarApiReference().AllowAnonymous();
app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = HealthReportWriter.WriteAsync }).AllowAnonymous();
app.MapControllers();

await app.RunAsync();

/// <summary>The API host entry point; public so integration tests can start it.</summary>
public partial class Program;
