using System.Globalization;
using MQTTnet;

namespace FieldTrack.Simulator.Bench;

/// <summary>
/// Mosquitto's own counts, read from its <c>$SYS</c> topics (refreshed every <c>sys_interval</c>; <c>bench/docker-compose.bench.yml</c> sets one second, and <see cref="SettleAsync"/> assumes it): how many
/// PUBLISH messages the broker has received, the ground truth a crash benchmark scores stored Readings against, and how many messages it
/// holds for subscribers that have not acknowledged them yet, the backlog a sustained run must keep flat.
/// </summary>
internal sealed class BrokerCounters : IAsyncDisposable
{
    private const string Received = "$SYS/broker/publish/messages/received";
    private const string Stored = "$SYS/broker/store/messages/count";
    private const string Retained = "$SYS/broker/retained messages/count";

    private static readonly TimeSpan _refresh = TimeSpan.FromSeconds(1);

    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();
    private readonly Dictionary<string, long> _values = [];
    private readonly Lock _gate = new();

    private BrokerCounters()
    {
    }

    /// <summary>PUBLISH messages the broker has received since it started, as of the last refresh.</summary>
    public long PublishesReceived => Value(Received);

    /// <summary>
    /// Messages the broker holds for a subscriber: queued or in flight, not yet acknowledged. The store also holds every retained message
    /// (the <c>$SYS</c> topics themselves), which are not a backlog.
    /// </summary>
    public long Backlog => Value(Stored) - Value(Retained);

    /// <summary>Connects to the broker and waits for its first counts.</summary>
    /// <exception cref="SimulatorException">The broker does not publish <c>$SYS</c> counts (is <c>sys_interval</c> set?).</exception>
    public static async Task<BrokerCounters> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var counters = new BrokerCounters();
        counters._client.ApplicationMessageReceivedAsync += args =>
        {
            if (long.TryParse(args.ApplicationMessage.ConvertPayloadToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                lock (counters._gate)
                {
                    counters._values[args.ApplicationMessage.Topic] = value;
                }
            }

            return Task.CompletedTask;
        };
        await counters._client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(host, port).WithCleanStart().Build(), cancellationToken);
        await counters._client.SubscribeAsync(
            new MqttClientSubscribeOptionsBuilder().WithTopicFilter(Received).WithTopicFilter(Stored).WithTopicFilter(Retained).Build(), cancellationToken);

        for (var waited = TimeSpan.Zero; counters.Missing(); waited += TimeSpan.FromMilliseconds(100))
        {
            if (waited > TimeSpan.FromSeconds(5))
            {
                throw new SimulatorException($"The broker at {host}:{port} publishes no $SYS counts; start the stack with bench/docker-compose.bench.yml (it sets sys_interval).");
            }

            await Task.Delay(100, cancellationToken);
        }

        return counters;
    }

    /// <summary>Waits for the counts to refresh twice, so they include everything that happened before the call.</summary>
    public static Task SettleAsync(CancellationToken cancellationToken) => Task.Delay(_refresh * 2.5, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_client.IsConnected)
        {
            await _client.DisconnectAsync();
        }

        _client.Dispose();
    }

    private long Value(string topic)
    {
        lock (_gate)
        {
            return _values[topic];
        }
    }

    private bool Missing()
    {
        lock (_gate)
        {
            return !_values.ContainsKey(Received) || !_values.ContainsKey(Stored) || !_values.ContainsKey(Retained);
        }
    }
}
