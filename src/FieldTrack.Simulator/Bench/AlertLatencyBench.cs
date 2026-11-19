using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;

namespace FieldTrack.Simulator.Bench;

/// <summary>
/// The alert latency benchmark (spec #21, stories 51 and 89): one process publishes Readings that breach a Threshold rule and listens on SignalR
/// as an Operator, so no clock synchronisation is needed. Each Device repeats: publish a breach, wait for the <c>AlertRaised</c> push, publish a
/// Reading that clears it, wait for the Resolve push. It passes when every Alert arrived and the p95 of receive minus publish is under
/// <see cref="P95LimitMs"/>. The total is split at the Alert's <c>CreatedAt</c> into server and push time.
/// </summary>
internal static class AlertLatencyBench
{
    /// <summary>The p95 of publish to receive that the benchmark must stay under, in milliseconds.</summary>
    public const double P95LimitMs = 500;

    // Spec #21 Threshold semantics: breach above 105, clear below 100. A Reading of 120 breaches, one of 85 clears.
    private const double Threshold = 105;
    private const double Hysteresis = 5;
    private const double FaultTempC = 120;
    private const double NormalTempC = 85;
    private const string HubPath = "hubs/live";

    private static readonly TimeSpan _pushTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Runs about <paramref name="trials"/> Alerts (spread over the first <paramref name="devices"/> Devices of <paramref name="fleet"/>, which must be seeded)
    /// and writes the result file. The Devices publish to <paramref name="broker"/>. The server and push split uses <paramref name="clockOffset"/>
    /// when given (a cloud database is out of reach), otherwise the offset measured on the database. <paramref name="configureHub"/> adjusts the
    /// SignalR connection (the integration tests reach an in-process server).
    /// </summary>
    public static async Task<BenchOutcome> RunAsync(
        FleetFile fleet, BenchTarget target, MqttBroker broker, TimeSpan? clockOffset, FieldTrackApi api, string operatorToken, Uri apiAddress, int trials, int devices,
        Action<HttpConnectionOptions>? configureHub, TextWriter log, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var used = fleet.Devices.Take(devices).ToList();
        var assets = await AlertBenchSupport.AssetsBySerialAsync(api, fleet, cancellationToken);
        await AlertBenchSupport.CreateThresholdRulesAsync(api, used.Select(device => assets[device.Serial]), "Bench alert latency", Threshold, Hysteresis, cancellationToken);
        var boots = BootState.Raise(target.StatePath, used.Select(device => device.Serial));

        var raised = new ConcurrentDictionary<(Guid Asset, long Sequence), TaskCompletionSource<(JsonElement Alert, DateTimeOffset ReceivedAt)>>();
        var resolved = new ConcurrentDictionary<Guid, TaskCompletionSource>();
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(apiAddress, $"{HubPath}?access_token={Uri.EscapeDataString(operatorToken)}"), options => configureHub?.Invoke(options))
            .Build();
        hub.On<JsonElement>("AlertRaised", alert =>
        {
            var receivedAt = DateTimeOffset.UtcNow;
            if (alert.GetProperty("reading") is { ValueKind: JsonValueKind.Object } reading)
            {
                raised.GetOrAdd((alert.GetProperty("assetId").GetGuid(), reading.GetProperty("sequenceNumber").GetInt64()), _ => NewSource<(JsonElement, DateTimeOffset)>())
                    .TrySetResult((alert, receivedAt));
            }
        });
        hub.On<JsonElement>("AlertUpdated", alert =>
        {
            if (alert.GetProperty("status").GetString() == "Resolved")
            {
                resolved.GetOrAdd(alert.GetProperty("id").GetGuid(), _ => NewSource()).TrySetResult();
            }
        });
        await hub.StartAsync(cancellationToken);

        var timings = new ConcurrentBag<AlertTiming>();
        var perDevice = (int)Math.Ceiling(trials / (double)used.Count);
        await log.WriteLineAsync($"{used.Count} Devices, {perDevice} Alerts each");
        await Task.WhenAll(used.Select(device => RunDeviceAsync(device.Serial, assets[device.Serial], boots[device.Serial], perDevice, broker, raised, resolved, timings, cancellationToken)));

        var offset = clockOffset ?? await target.Database.ClockOffsetAsync(cancellationToken);
        var latency = AlertLatency.Of([.. timings], offset);
        var missed = (perDevice * used.Count) - timings.Count;
        return await target.WriteResultAsync(
            "alert-latency",
            started,
            new { devices = used.Count, alertsPerDevice = perDevice, thresholdC = Threshold, hysteresisC = Hysteresis, faultReadingC = FaultTempC, p95LimitMs = P95LimitMs },
            new
            {
                alertsExpected = perDevice * used.Count,
                alertsReceived = timings.Count,
                missed,
                latencyMs = latency,
                // Total is receive minus publish on one clock. Server and push are split at the platform's CreatedAt, corrected by this offset.
                clockOffsetMs = offset.TotalMilliseconds,
                clockOffsetSource = clockOffset is null ? "database" : "--clock-offset-ms",
                basis = "publish starts before the PUBLISH is sent and includes broker, ingest batching, rule evaluation, commit, PUBACK and the SignalR push",
            },
            pass: missed == 0 && latency is { Total.P95Ms: < P95LimitMs },
            cancellationToken);
    }

    private static async Task RunDeviceAsync(
        string serial, Guid assetId, int boot, int count, MqttBroker broker,
        ConcurrentDictionary<(Guid Asset, long Sequence), TaskCompletionSource<(JsonElement Alert, DateTimeOffset ReceivedAt)>> raised,
        ConcurrentDictionary<Guid, TaskCompletionSource> resolved, ConcurrentBag<AlertTiming> timings, CancellationToken cancellationToken)
    {
        await using var publisher = new ReconnectingPublisher(broker, serial, TextWriter.Null);
        await publisher.ConnectAsync(cancellationToken);
        long sequence = 0;

        // The first Reading is the baseline: a rule's unknown state counts as clear.
        await PublishAsync(NormalTempC);
        for (var i = 0; i < count; i++)
        {
            var breach = sequence;
            var (publishedAt, _) = await PublishAsync(FaultTempC);
            var received = raised.GetOrAdd((assetId, breach), _ => NewSource<(JsonElement, DateTimeOffset)>());
            if (await Task.WhenAny(received.Task, Task.Delay(_pushTimeout, cancellationToken)) != received.Task)
            {
                return;
            }

            var (alert, receivedAt) = await received.Task;
            timings.Add(new AlertTiming(publishedAt, alert.GetProperty("createdAt").GetDateTimeOffset(), receivedAt));

            // The next breach raises only once this Alert is Resolved.
            await PublishAsync(NormalTempC);
            var done = resolved.GetOrAdd(alert.GetProperty("id").GetGuid(), _ => NewSource());
            if (await Task.WhenAny(done.Task, Task.Delay(_pushTimeout, cancellationToken)) != done.Task)
            {
                return;
            }
        }

        async Task<(DateTimeOffset PublishedAt, long Sequence)> PublishAsync(double engineTempC)
        {
            var now = DateTimeOffset.UtcNow;
            var seq = sequence++;
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new { boot, seq, ts = now.ToUnixTimeMilliseconds(), lat = 52.0, lon = 4.0, engineTempC }, _json);
            await publisher.PublishAsync(SimulatedDevice.TopicFor(serial), payload, cancellationToken);
            return (now, seq);
        }
    }

    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewSource<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
