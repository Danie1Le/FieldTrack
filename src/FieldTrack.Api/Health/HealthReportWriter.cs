using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FieldTrack.Api.Health;

/// <summary>
/// Writes a health report as JSON with the overall status and one status per check, e.g.
/// <c>{"status":"Healthy","checks":{"database":"Healthy","broker":"Healthy"}}</c>.
/// Failure details stay in the logs, not in this public response.
/// </summary>
public static class HealthReportWriter
{
    /// <summary>Response writer for <c>MapHealthChecks</c>.</summary>
    public static Task WriteAsync(HttpContext context, HealthReport report) =>
        context.Response.WriteAsJsonAsync(
            new
            {
                Status = report.Status.ToString(),
                Checks = report.Entries.ToDictionary(entry => entry.Key, entry => entry.Value.Status.ToString()),
            },
            context.RequestAborted);
}
