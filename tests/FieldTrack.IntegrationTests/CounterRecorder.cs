using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;

namespace FieldTrack.IntegrationTests;

/// <summary>
/// Records what one host adds to one counter. Only meters from that host's <see cref="IMeterFactory"/> count,
/// so other hosts in the test run cannot add to it.
/// </summary>
internal sealed class CounterRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<(long Value, Dictionary<string, object?> Tags)> _measurements = new();

    public CounterRecorder(IServiceProvider services, string meterName, string instrumentName)
    {
        var meters = services.GetRequiredService<IMeterFactory>();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Scope == meters && instrument.Meter.Name == meterName && instrument.Name == instrumentName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            _measurements.Enqueue((value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value))));
        _listener.Start();
    }

    /// <summary>Every value added so far, with its tags.</summary>
    public IReadOnlyList<(long Value, Dictionary<string, object?> Tags)> Measurements => [.. _measurements];

    public void Dispose() => _listener.Dispose();
}
