using System.Text.Json;
using System.Threading.Channels;
using FieldTrack.Api.Live;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;

namespace FieldTrack.IntegrationTests;

/// <summary>
/// A dashboard's connection to <see cref="LiveHub"/>: the access token travels in the <c>access_token</c> query
/// parameter, as browsers send it, and every pushed message is recorded.
/// </summary>
internal sealed class LiveClient : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly Channel<(string Name, JsonElement Payload)> _received = Channel.CreateUnbounded<(string, JsonElement)>();

    private LiveClient(HubConnection connection) => _connection = connection;

    /// <summary>Connects to the hub of <paramref name="api"/>, signed in with <paramref name="accessToken"/> when given.</summary>
    public static async Task<LiveClient> ConnectAsync(WebApplicationFactory<Program> api, string? accessToken)
    {
        var server = api.Server;
        var query = accessToken is null ? "" : $"?access_token={Uri.EscapeDataString(accessToken)}";
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, LiveHub.Path + query), options =>
            {
                // The test server has no real sockets; long polling sends the same query string on every request.
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
            })
            .Build();
        var client = new LiveClient(connection);
        foreach (var name in new[] { LiveHub.AssetsUpdated, LiveHub.AlertRaised, LiveHub.AlertUpdated })
        {
            connection.On<JsonElement>(name, payload => client._received.Writer.TryWrite((name, payload)));
        }

        try
        {
            await connection.StartAsync(TestContext.Current.CancellationToken);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        return client;
    }

    /// <summary>Waits up to 15 s for the next <paramref name="name"/> message that <paramref name="match"/> accepts, skipping others.</summary>
    public async Task<JsonElement> NextAsync(string name, Func<JsonElement, bool>? match = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            var (received, payload) = await _received.Reader.ReadAsync(timeout.Token);
            if (received == name && (match is null || match(payload)))
            {
                return payload;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
