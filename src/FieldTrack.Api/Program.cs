using FieldTrack.Api.Health;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFieldTrackDatabase(builder.Configuration);
builder.Services.AddOptions<BrokerOptions>()
    .BindConfiguration(BrokerOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<FieldTrackDbContext>("database")
    .AddCheck<BrokerHealthCheck>("broker", timeout: TimeSpan.FromSeconds(5));
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
