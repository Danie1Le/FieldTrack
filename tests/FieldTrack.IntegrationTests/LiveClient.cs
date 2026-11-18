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
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private LiveClient(HubConnection connection) => _connection = connection;

    /// <summary>Completes when the server ends the connection.</summary>
    public Task Closed => _closed.Task;

    /// <summary>
    /// Connects to the hub of <paramref name="api"/>, signed in with <paramref name="accessToken"/> when given. Long
    /// polling by default; <paramref name="webSockets"/> keeps one long-lived socket, as browsers do, so the token is
    /// checked only at connect.
    /// </summary>
    public static async Task<LiveClient> ConnectAsync(WebApplicationFactory<Program> api, string? accessToken, bool webSockets = false)
    {
        var server = api.Server;
        var query = accessToken is null ? "" : $"?access_token={Uri.EscapeDataString(accessToken)}";
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, LiveHub.Path + query), options =>
            {
                if (webSockets)
                {
                    options.Transports = HttpTransportType.WebSockets;
                    options.SkipNegotiation = true;
                    options.WebSocketFactory = (context, cancellationToken) =>
                        new ValueTask<System.Net.WebSockets.WebSocket>(server.CreateWebSocketClient().ConnectAsync(context.Uri, cancellationToken));
                }
                else
                {
                    // Long polling sends the same query string on every request.
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                }
            })
            .Build();
        var client = new LiveClient(connection);
        foreach (var name in new[] { LiveHub.AssetsUpdated, LiveHub.AssetsRemoved, LiveHub.AlertRaised, LiveHub.AlertUpdated })
        {
            connection.On<JsonElement>(name, payload => client._received.Writer.TryWrite((name, payload)));
        }

        foreach (var name in new[] { LiveHub.GeofencesChanged, LiveHub.AlertRulesChanged, LiveHub.AssetGroupsChanged })
        {
            connection.On(name, () => client._received.Writer.TryWrite((name, default)));
        }

        connection.Closed += _ =>
        {
            client._closed.TrySetResult();
            return Task.CompletedTask;
        };

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
        using var timeout = FifteenSeconds();
        while (true)
        {
            var (received, payload) = await _received.Reader.ReadAsync(timeout.Token);
            if (received == name && (match is null || match(payload)))
            {
                return payload;
            }
        }
    }

    /// <summary>Waits up to 15 s for the next message of any kind.</summary>
    public async Task<(string Name, JsonElement Payload)> NextAsync()
    {
        using var timeout = FifteenSeconds();
        return await _received.Reader.ReadAsync(timeout.Token);
    }

    /// <summary>Returns the first message that arrives within <paramref name="wait"/>, or null when none does.</summary>
    public async Task<(string Name, JsonElement Payload)?> AnyWithinAsync(TimeSpan wait)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(wait);
        try
        {
            return await _received.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static CancellationTokenSource FifteenSeconds()
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        return timeout;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
