namespace FieldTrack.Simulator;

/// <summary>Where and how fast a run publishes, and where it keeps its files.</summary>
/// <param name="BrokerHost">Host name of the MQTT broker.</param>
/// <param name="BrokerPort">Port of the MQTT broker.</param>
/// <param name="RatePerSecond">The target aggregate rate across all Devices, in Readings per second, or null for maximum mode: as fast as the broker acknowledges.</param>
/// <param name="Duration">How long to run, or null to run until cancelled.</param>
/// <param name="StatePath">The Boot counter file (<see cref="BootState"/>).</param>
/// <param name="ManifestPath">Where to write the <see cref="RunManifest"/>.</param>
/// <param name="Scenario">What to inject into which Device and when; null for nothing scripted.</param>
/// <param name="RandomInjection">How often to inject at random; null for never. Neither this nor a Scenario is allowed in maximum mode.</param>
internal sealed record RunOptions(
    string BrokerHost,
    int BrokerPort,
    double? RatePerSecond,
    TimeSpan? Duration,
    string StatePath,
    string ManifestPath,
    Scenario? Scenario = null,
    RandomRates? RandomInjection = null);

/// <summary>
/// Runs every Device of a fleet against the broker and writes the Manifest when it stops. Paced mode (a rate) spreads the sends
/// evenly across the Devices and injects the Scenario and random Faults; maximum mode (no rate) is a flood of Readings with
/// noise and no injections.
/// </summary>
internal static class FleetRunner
{
    private static readonly TimeSpan _drainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Raises each Device's Boot, then publishes until <see cref="RunOptions.Duration"/> passes or <paramref name="cancellationToken"/> is
    /// cancelled; either is a normal stop. Each Device publishes one Reading at a time and moves on only after the broker acknowledged it.
    /// </summary>
    /// <returns>The Manifest, which is also saved to <see cref="RunOptions.ManifestPath"/>.</returns>
    /// <exception cref="SimulatorException">Maximum mode was asked to inject, or the rate does not suit the fleet.</exception>
    public static async Task<RunManifest> RunAsync(FleetFile fleet, RunOptions options, TextWriter log, TimeProvider time, CancellationToken cancellationToken)
    {
        if (options.RatePerSecond is null && (options.Scenario is not null || options.RandomInjection is { Any: true }))
        {
            throw new SimulatorException("--rate max floods with noise and random-walk positions only; give it a rate to inject a Scenario or random Faults.");
        }

        Pacing? pacing = options.RatePerSecond is { } rate ? new Pacing(fleet.Devices.Count, rate) : null;
        var boots = BootState.Raise(options.StatePath, fleet.Devices.Select(device => device.Serial));
        var devices = fleet.Devices.Select(device => SimulatedDevice.Start(
            device, boots[device.Serial], options.Scenario, options.RandomInjection, () => BootState.Raise(options.StatePath, [device.Serial])[device.Serial])).ToList();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options.Duration is { } duration)
        {
            stop.CancelAfter(duration);
        }

        // A Reading in flight when the run stops gets a short grace to be acknowledged, so the Manifest does not
        // leave out one the broker already holds.
        using var drain = new CancellationTokenSource();
        using var _ = stop.Token.Register(() => drain.CancelAfter(_drainTimeout));

        var startedAt = time.GetUtcNow();
        var startTimestamp = time.GetTimestamp();
        RunManifest manifest;
        try
        {
            await Task.WhenAll(devices.Select((device, index) => RunDeviceAsync(device, index)));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The run is over, whether the duration passed or the developer cancelled it.
        }
        finally
        {
            // Written however the run ends: what was acknowledged is still the benchmark's ground truth.
            manifest = RunManifest.Of(startedAt, time.GetUtcNow(), options.RatePerSecond, devices);
            manifest.Write(options.ManifestPath);
        }

        return manifest;

        // A Device that fails stops the whole run rather than leave it running short-handed and silent.
        async Task RunDeviceAsync(SimulatedDevice device, int index)
        {
            try
            {
                await PublishAsync(device, index, pacing, options, log, time, startTimestamp, stop.Token, drain.Token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await log.WriteLineAsync($"{device.Serial}: stopped ({exception.Message}); stopping the run");
                await stop.CancelAsync();
                throw;
            }
        }
    }

    private static async Task PublishAsync(
        SimulatedDevice device, int index, Pacing? pacing, RunOptions options, TextWriter log, TimeProvider time, long startTimestamp, CancellationToken stop, CancellationToken drain)
    {
        // One connection per Device, as on the real fleet.
        await using var publisher = new ReconnectingPublisher(options.BrokerHost, options.BrokerPort, device.Serial, log);
        await Task.Delay(pacing?.StartOffset(index) ?? TimeSpan.Zero, time, stop);
        using var timer = pacing is { } paced ? new PeriodicTimer(paced.Interval, time) : null;
        do
        {
            device.Step(time.GetUtcNow(), time.GetElapsedTime(startTimestamp));
            while (device.NextToSend is { } payload)
            {
                await publisher.PublishAsync(device.Topic, payload, drain);
                device.Published();
            }
        }
        // Maximum mode has no timer: the next Reading follows as soon as the last one was acknowledged.
        while (timer is null ? !stop.IsCancellationRequested : await timer.WaitForNextTickAsync(stop));
    }
}
