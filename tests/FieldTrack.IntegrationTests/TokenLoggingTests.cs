using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using static FieldTrack.IntegrationTests.FieldTrackApiFactory;

namespace FieldTrack.IntegrationTests;

public sealed class TokenLoggingTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Passwords_and_tokens_never_appear_in_logs_even_at_Trace_level()
    {
        var logs = new CapturingLoggerProvider();
        await using var api = Fixture.Api.WithWebHostBuilder(builder => builder
            .UseSetting("Logging:LogLevel:Default", "Trace")
            .UseSetting("Logging:LogLevel:Microsoft.AspNetCore", "Trace")
            // A provider-specific level outranks every provider-less rule, so it must not bring the token back.
            .UseSetting($"Logging:{CapturingLoggerProvider.Alias}:LogLevel:Default", "Trace")
            .ConfigureLogging(logging => logging.AddProvider(logs)));
        using var client = api.CreateClient();

        var signedIn = await AuthClient.AuthorizeAsync(client, AdminEmail, AdminPassword);
        using var refreshed = await AuthClient.PostRefreshAsync(client, signedIn.RefreshToken);
        var tokens = await AuthClient.ReadTokensAsync(refreshed);
        using var hub = await client.GetAsync($"/hubs/fleet?access_token={tokens.AccessToken}", CancellationToken);

        logs.Entries.ShouldNotBeEmpty();
        string[] secrets = [AdminPassword, signedIn.AccessToken, signedIn.RefreshToken, tokens.AccessToken, tokens.RefreshToken];
        foreach (var secret in secrets)
        {
            logs.Entries.Where(entry => entry.Contains(secret, StringComparison.Ordinal)).ShouldBeEmpty();
        }
    }

    /// <summary>Keeps every log message, its structured values and exception as text.</summary>
    [ProviderAlias(Alias)]
    private sealed class CapturingLoggerProvider : ILoggerProvider, ILogger
    {
        public const string Alias = "Capture";

        public ConcurrentQueue<string> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
            Entries.Enqueue($"{formatter(state, exception)} {string.Join(' ', values.Select(value => value.Value))} {exception}");
        }

        public void Dispose()
        {
        }
    }
}
