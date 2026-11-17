namespace FieldTrack.Simulator.Bench;

/// <summary>
/// The duplicate-suppression benchmark (spec #21, ADR 0003): publish N Readings, then republish the identical bytes, and check that N are
/// stored and the Reading key table refused every one of the N copies. Ingest does not expose its duplicate counters outside the
/// process, so the refusals are derived: every message the broker received is stored, stored as a Rejected message, or dropped on a key
/// conflict, so conflicts = broker received - stored - rejected.
/// </summary>
internal static class DuplicateBench
{
    // Device times step by this much: tight, so a run's Readings sit in a window of their own, and within MaxAhead of the clock.
    private static readonly TimeSpan _step = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan _storeTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Publishes about <paramref name="count"/> Readings (spread evenly over the fleet), republishes them and writes the result file.</summary>
    public static async Task<BenchOutcome> RunAsync(FleetFile fleet, BenchTarget target, int count, TextWriter log, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var perDevice = Math.Max(1, count / fleet.Devices.Count);
        var published = perDevice * fleet.Devices.Count;
        var boots = BootState.Raise(target.StatePath, fleet.Devices.Select(device => device.Serial));
        var payloads = fleet.Devices.Select(device => Payloads(device, boots[device.Serial], started, perDevice + 1)).ToList();
        // One more Reading, published last: MQTT delivers in order, so once it is stored every copy before it has been seen.
        var sentinel = payloads[0][^1];
        var window = (From: started, To: started + (_step * (perDevice + 2)));

        await using var counters = await BrokerCounters.ConnectAsync(target.BrokerHost, target.BrokerPort, cancellationToken);
        await BrokerCounters.SettleAsync(cancellationToken);
        var (receivedBefore, rejectedBefore) = (counters.PublishesReceived, await target.Database.CountRejectedAsync(cancellationToken));
        var storedBefore = await target.Database.CountReadingsAsync(window.From - BenchDatabase.Padding, window.To + BenchDatabase.Padding, cancellationToken);

        await log.WriteLineAsync($"publishing {published} Readings");
        await PublishAsync(fleet, target, payloads.Select(device => device.Take(perDevice).ToList()).ToList(), cancellationToken);
        var firstPass = await StoredAsync(published, cancellationToken);

        await log.WriteLineAsync($"republishing the same {published} payloads");
        await PublishAsync(fleet, target, payloads.Select(device => device.Take(perDevice).ToList()).ToList(), cancellationToken);
        await PublishAsync(fleet, target, [[sentinel], .. fleet.Devices.Skip(1).Select(_ => new List<byte[]>())], cancellationToken);
        var stored = await StoredAsync(published + 1, cancellationToken) - 1;
        await BrokerCounters.SettleAsync(cancellationToken);

        var received = counters.PublishesReceived - receivedBefore - 1;
        var rejected = await target.Database.CountRejectedAsync(cancellationToken) - rejectedBefore;
        var conflicts = received - stored - rejected;

        return await target.WriteResultAsync(
            "duplicates",
            started,
            new { devices = fleet.Devices.Count, readings = published },
            new { published, republished = published, brokerReceived = received, storedAfterFirstPublish = firstPass, stored, rejected, keyConflicts = conflicts },
            pass: stored == published && conflicts == published && rejected == 0,
            cancellationToken);

        async Task<long> StoredAsync(long expected, CancellationToken token)
        {
            var deadline = DateTimeOffset.UtcNow + _storeTimeout;
            while (true)
            {
                var stored = await target.Database.CountReadingsAsync(window.From - BenchDatabase.Padding, window.To + BenchDatabase.Padding, token) - storedBefore;
                if (stored >= expected || DateTimeOffset.UtcNow > deadline)
                {
                    return stored;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), token);
            }
        }
    }

    // The bytes the Device sends for its Readings 0 to count - 1: building them once is what makes the second publish identical.
    private static List<byte[]> Payloads(FleetDevice spec, int boot, DateTimeOffset start, int count)
    {
        var device = SimulatedDevice.Start(spec, boot);
        var payloads = new List<byte[]>(count);
        for (var i = 0; i < count; i++)
        {
            device.Step(start + (_step * i), _step * i);
            payloads.Add(device.NextToSend!);
            device.Published();
        }

        return payloads;
    }

    private static Task PublishAsync(FleetFile fleet, BenchTarget target, List<List<byte[]>> payloadsByDevice, CancellationToken cancellationToken) =>
        Task.WhenAll(fleet.Devices.Select(async (spec, index) =>
        {
            await using var publisher = new ReconnectingPublisher(target.BrokerHost, target.BrokerPort, spec.Serial, TextWriter.Null);
            foreach (var payload in payloadsByDevice[index])
            {
                await publisher.PublishAsync(SimulatedDevice.TopicFor(spec.Serial), payload, cancellationToken);
            }
        }));
}
