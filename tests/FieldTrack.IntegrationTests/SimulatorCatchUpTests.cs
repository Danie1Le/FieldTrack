using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Text.Json;
using FieldTrack.Simulator;
using MQTTnet;
using MQTTnet.Protocol;

namespace FieldTrack.IntegrationTests;

/// <summary>
/// A paced Device whose broker is down owes Readings (#105). It catches up, but only by one second's worth, and each Reading
/// it publishes is stamped when it was taken, never when it was due.
/// </summary>
public sealed class SimulatorCatchUpTests(FieldTrackFixture fixture) : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("fieldtrack-catchup-").FullName;

    [Fact(Timeout = 60_000)]
    public async Task After_a_broker_outage_a_Device_catches_up_by_at_most_a_second_and_stamps_each_Reading_when_taken()
    {
        const double rate = 20;
        var serial = $"CATCH-{Guid.NewGuid():N}";
        var fleet = FleetFile.Parse($$"""{ "devices": [ { "serial": "{{serial}}", "assetType": "Truck", "position": { "lat": 45.5, "lon": -122.6 } } ] }""", _directory);
        var received = new ConcurrentQueue<(long Seq, long Ts, byte[] Payload)>();
        using var subscriber = new MqttClientFactory().CreateMqttClient();
        subscriber.ApplicationMessageReceivedAsync += message =>
        {
            var payload = message.ApplicationMessage.Payload.ToArray();
            using var json = JsonDocument.Parse(payload);
            received.Enqueue((json.RootElement.GetProperty("seq").GetInt64(), json.RootElement.GetProperty("ts").GetInt64(), payload));
            return Task.CompletedTask;
        };
        await subscriber.ConnectAsync(
            new MqttClientOptionsBuilder().WithTcpServer(fixture.BrokerHost, fixture.BrokerPort).WithClientId($"test-sub-{Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        await subscriber.SubscribeAsync($"fieldtrack/v1/devices/{serial}/telemetry", MqttQualityOfServiceLevel.AtLeastOnce, TestContext.Current.CancellationToken);
        using var proxy = new TcpProxy(fixture.BrokerHost, fixture.BrokerPort);
        // A refused connection takes about 2 s to fail on Windows, which would blur the timing; a broker that resets each connection fails at once.
        var down = new TcpListener(IPAddress.Loopback, proxy.Port);
        down.Start();
        _ = Task.Run(async () =>
        {
            // Ends with a SocketException or an OperationCanceledException when the test stops the listener or ends.
            while (true)
            {
                using var connection = await down.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
                connection.LingerState = new LingerOption(true, 0);
            }
        }, TestContext.Current.CancellationToken);
        var logged = new StringWriter();
        var log = TextWriter.Synchronized(logged);
        string Logged()
        {
            lock (log)
            {
                return logged.ToString();
            }
        }

        var options = new RunOptions(MqttBroker.Tcp("127.0.0.1", proxy.Port), rate, TimeSpan.FromSeconds(5), Path.Combine(_directory, "state.json"), Path.Combine(_directory, "manifest.json"));

        // The broker is down for the first 2.5 s of a 5 s run: the first Reading is taken at once and cannot be delivered.
        // The run's clock has started once the first Reading's first attempt fails, which is when it logs that retry.
        var running = FleetRunner.RunAsync(fleet, options, log, TimeProvider.System, TestContext.Current.CancellationToken);
        while (!Logged().Contains("retrying"))
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        await Task.Delay(TimeSpan.FromSeconds(2.5), TestContext.Current.CancellationToken);
        down.Stop();
        var brokerBack = DateTimeOffset.UtcNow;
        proxy.Start();
        var manifest = await running;

        await Task.Delay(500, TestContext.Current.CancellationToken);
        var readings = received.ToArray();
        var first = readings.Single(reading => reading.Seq == 0);
        DateTimeOffset.FromUnixTimeMilliseconds(first.Ts).ShouldBeLessThan(manifest.StartedAt.AddSeconds(1), "the Reading was taken at the start and keeps its stamp through the retries");
        readings.Where(reading => reading.Seq > 0).ShouldAllBe(
            reading => DateTimeOffset.FromUnixTimeMilliseconds(reading.Ts) >= brokerBack.AddMilliseconds(-5), "the catch-up Readings are stamped when taken, after the broker came back");
        readings.GroupBy(reading => reading.Seq).ShouldAllBe(
            sends => sends.Select(reading => Convert.ToBase64String(reading.Payload)).Distinct().Count() == 1, "a Reading sent again carries the same bytes");
        // Unbounded catch-up would publish all 100 owed in 5 s; 2.5 s of broker plus one second's debt is 70 at most.
        var distinct = readings.Select(reading => reading.Seq).Distinct().Count();
        distinct.ShouldBeInRange(20, 75);
        manifest.Devices.Single().Ranges.Sum(range => range.LastSequence - range.FirstSequence + 1).ShouldBe(distinct);
        Logged().Split('\n').Count(line => line.Contains("skipped")).ShouldBe(1, "one log line for the skip");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
