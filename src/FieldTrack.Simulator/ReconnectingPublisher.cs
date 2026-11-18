using MQTTnet;
using MQTTnet.Protocol;

namespace FieldTrack.Simulator;

/// <summary>
/// One simulated Device's MQTT connection. MQTTnet 5 has no managed client, and the ingest spike's harness hung when its
/// client dropped, so this connects on demand, reconnects after any failure and sends a Reading again until the broker
/// acknowledges it (QoS 1). The retry sends the same bytes: if the first attempt did land, ingest drops the identical duplicate (ADR 0003).
/// </summary>
internal sealed class ReconnectingPublisher : IAsyncDisposable
{
    private static readonly TimeSpan _firstRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan _longestRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _keepAlive = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _disconnectTimeout = TimeSpan.FromSeconds(1);

    private readonly MqttBroker _broker;
    private readonly string _name;
    private readonly TextWriter _log;
    private IMqttClient? _client;

    /// <summary>A publisher to <paramref name="broker"/>; <paramref name="name"/> (the serial) names it in the log and its client id.</summary>
    public ReconnectingPublisher(MqttBroker broker, string name, TextWriter log) => (_broker, _name, _log) = (broker, name, log);

    /// <summary>
    /// Opens the connection ahead of the first Reading, so a run does not count the time a whole fleet takes to connect against its
    /// schedule. One attempt, and a failure is not reported: <see cref="PublishAsync"/> retries and logs.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_timeout);
        try
        {
            await ConnectedAsync(attempt.Token);
        }
        catch (Exception)
        {
            // The first Reading connects again and retries until it is acknowledged.
        }
    }

    /// <summary>
    /// Publishes <paramref name="payload"/> to <paramref name="topic"/> and returns once the broker acknowledged it, however many
    /// connections that takes. The retries stop only when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <exception cref="OperationCanceledException">The token was cancelled before the broker acknowledged.</exception>
    public async Task PublishAsync(string topic, byte[] payload, CancellationToken cancellationToken)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        var delay = _firstRetryDelay;
        while (true)
        {
            string failure;
            // MQTTnet's own timeout does not cover a broker that accepts TCP and then stays silent, so every attempt gets a deadline.
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(_timeout);
            try
            {
                var client = await ConnectedAsync(attempt.Token);
                var result = await client.PublishAsync(message, attempt.Token);
                if (result.IsSuccess)
                {
                    return;
                }

                failure = $"the broker refused the publish: {result.ReasonCode}";
            }
            catch (Exception exception)
            {
                // Cancelling can surface as any exception (MQTTnet wraps the aborted connect), and a connection error can land
                // just after the cancel; either way the caller asked to stop, so it sees a cancellation and not the error.
                cancellationToken.ThrowIfCancellationRequested();
                // Any other failure is retried on a new connection: the network and MQTTnet throw too many kinds to list.
                failure = exception is OperationCanceledException ? $"no answer within {_timeout.TotalSeconds:0} s" : exception.Message;
            }

            // The connection may be half-dead (the client has not noticed yet); start the next attempt on a fresh one.
            Discard();
            await _log.WriteLineAsync($"{_name}: publish failed ({failure}); retrying");
            await Task.Delay(delay, cancellationToken);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _longestRetryDelay.Ticks));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is { IsConnected: true } client)
        {
            try
            {
                using var deadline = new CancellationTokenSource(_disconnectTimeout);
                await client.DisconnectAsync(new MqttClientDisconnectOptions(), deadline.Token);
            }
            catch (Exception)
            {
                // A clean DISCONNECT is a courtesy; the connection is thrown away either way.
            }
        }

        Discard();
    }

    private async Task<IMqttClient> ConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsConnected: true } connected)
        {
            return connected;
        }

        Discard();
        var client = new MqttClientFactory().CreateMqttClient();
        try
        {
            var builder = new MqttClientOptionsBuilder()
                .WithConnectionUri(_broker.Address)
                // A new ClientId each time, so a connection the broker still thinks is open cannot block the new one.
                .WithClientId($"fieldtrack-sim-{_name}-{Guid.NewGuid():N}")
                .WithCleanStart()
                .WithTimeout(_timeout)
                // Short, so an idle connection to a broker that went silent is noticed before the next Reading.
                // It also keeps an idle WebSocket open through Container Apps ingress, which drops one after 240 s (#154).
                .WithKeepAlivePeriod(_keepAlive);
            if (_broker.Username is not null)
            {
                builder.WithCredentials(_broker.Username, _broker.Password);
            }

            var options = builder.Build();
            // MQTTnet returns a refused CONNACK instead of throwing.
            var result = await client.ConnectAsync(options, cancellationToken);
            if (result.ResultCode != MqttClientConnectResultCode.Success)
            {
                throw new InvalidOperationException($"the broker refused the connection: {result.ResultCode}");
            }
        }
        catch
        {
            client.Dispose();
            throw;
        }

        _client = client;
        return client;
    }

    private void Discard()
    {
        _client?.Dispose();
        _client = null;
    }
}
