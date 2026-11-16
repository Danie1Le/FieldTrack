using FieldTrack.Api.Health;
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
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    // Development and integration tests migrate on startup; deployed environments migrate as a release step.
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<FieldTrackDbContext>().Database.MigrateAsync();
}

// The API docs are public in every environment (spec #21: Scalar for the public API docs).
app.MapOpenApi();
app.MapScalarApiReference();
app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = HealthReportWriter.WriteAsync });

await app.RunAsync();

/// <summary>The API host entry point; public so integration tests can start it.</summary>
public partial class Program;
