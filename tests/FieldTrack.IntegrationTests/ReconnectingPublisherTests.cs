using System.Collections.Concurrent;
using System.Text;
using FieldTrack.Simulator;
using MQTTnet;
using MQTTnet.Protocol;

namespace FieldTrack.IntegrationTests;

/// <summary>
/// The ingest spike's harness hung when its MQTT client dropped (#29). These tests cut the connection and take the broker away,
/// and expect publishing to carry on.
/// </summary>
public sealed class ReconnectingPublisherTests(FieldTrackFixture fixture) : IDisposable
{
    private readonly ConcurrentQueue<string> _received = new();
    private readonly List<IDisposable> _disposables = [];

    private async Task SubscribeAsync(string topic)
    {
        var subscriber = new MqttClientFactory().CreateMqttClient();
        _disposables.Add(subscriber);
        subscriber.ApplicationMessageReceivedAsync += message =>
        {
            _received.Enqueue(Encoding.UTF8.GetString(message.ApplicationMessage.Payload));
            return Task.CompletedTask;
        };
        await subscriber.ConnectAsync(
            new MqttClientOptionsBuilder().WithTcpServer(fixture.BrokerHost, fixture.BrokerPort).WithClientId($"test-sub-{Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        await subscriber.SubscribeAsync(topic, MqttQualityOfServiceLevel.AtLeastOnce, TestContext.Current.CancellationToken);
    }

    private async Task<string[]> ReceivedWhenAsync(Func<string[], bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!done([.. _received]) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return [.. _received];
    }

    [Fact(Timeout = 30_000)]
    public async Task Publishing_carries_on_after_the_connection_is_cut()
    {
        var topic = $"fieldtrack/v1/devices/RECON-{Guid.NewGuid():N}/telemetry";
        await SubscribeAsync(topic);
        using var proxy = new TcpProxy(fixture.BrokerHost, fixture.BrokerPort);
        proxy.Start();
        await using var publisher = new ReconnectingPublisher("127.0.0.1", proxy.Port, "recon", TextWriter.Null);
        await publisher.PublishAsync(topic, "before"u8.ToArray(), TestContext.Current.CancellationToken);

        proxy.SeverConnections();
        await publisher.PublishAsync(topic, "after"u8.ToArray(), TestContext.Current.CancellationToken);

        var received = await ReceivedWhenAsync(messages => messages.Contains("before") && messages.Contains("after"));
        received.ShouldContain("before");
        received.ShouldContain("after");
    }

    [Fact(Timeout = 30_000)]
    public async Task Publishing_waits_for_a_broker_that_is_down_and_then_goes_through()
    {
        var topic = $"fieldtrack/v1/devices/RECON-{Guid.NewGuid():N}/telemetry";
        await SubscribeAsync(topic);
        using var proxy = new TcpProxy(fixture.BrokerHost, fixture.BrokerPort);
        await using var publisher = new ReconnectingPublisher("127.0.0.1", proxy.Port, "recon", TextWriter.Null);

        var publishing = publisher.PublishAsync(topic, "late"u8.ToArray(), TestContext.Current.CancellationToken);
        await Task.Delay(700, TestContext.Current.CancellationToken);
        publishing.IsCompleted.ShouldBeFalse();
        proxy.Start();
        await publishing.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        (await ReceivedWhenAsync(messages => messages.Contains("late"))).ShouldContain("late");
    }

    [Fact(Timeout = 30_000)]
    public async Task Cancelling_ends_the_retries()
    {
        using var proxy = new TcpProxy(fixture.BrokerHost, fixture.BrokerPort);
        await using var publisher = new ReconnectingPublisher("127.0.0.1", proxy.Port, "recon", TextWriter.Null);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var publishing = publisher.PublishAsync("fieldtrack/v1/devices/RECON/telemetry", "never"u8.ToArray(), stop.Token);
        await stop.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => publishing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }
}
