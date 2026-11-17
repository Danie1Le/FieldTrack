namespace FieldTrack.Simulator.Bench;

/// <summary>One Alert's journey for the latency bench: published, created by the platform, received by the listener.</summary>
/// <param name="PublishedAt">When the bench published the Reading that breached, on this machine's clock.</param>
/// <param name="CreatedAt">The Alert's <c>CreatedAt</c>, on the platform's clock.</param>
/// <param name="ReceivedAt">When the SignalR <c>AlertRaised</c> message arrived, on this machine's clock.</param>
internal sealed record AlertTiming(DateTimeOffset PublishedAt, DateTimeOffset CreatedAt, DateTimeOffset ReceivedAt);

/// <summary>
/// Publish-to-receive latency of Alerts (spec #21, story 51), split at the Alert's <c>CreatedAt</c> into the time the platform took to raise it
/// (<see cref="Server"/>) and the time it took to reach the browser (<see cref="Push"/>). The total needs no clock correction because one
/// process publishes and listens; the split crosses to the platform's clock once, so it is corrected by the measured offset.
/// </summary>
/// <param name="Total">Received minus published.</param>
/// <param name="Server">Published to <c>CreatedAt</c>: batching, evaluation and commit.</param>
/// <param name="Push">From <c>CreatedAt</c> to received: the push after the PUBACKs.</param>
internal sealed record AlertLatency(LatencySummary Total, LatencySummary Server, LatencySummary Push)
{
    /// <summary>The distributions of <paramref name="timings"/>, or null when there are none; <paramref name="databaseClockOffset"/> is the platform's clock minus this machine's.</summary>
    public static AlertLatency? Of(IReadOnlyList<AlertTiming> timings, TimeSpan databaseClockOffset) =>
        timings.Count == 0
            ? null
            : new(
                LatencySummary.Of([.. timings.Select(timing => (timing.ReceivedAt - timing.PublishedAt).TotalMilliseconds)])!,
                LatencySummary.Of([.. timings.Select(timing => (timing.CreatedAt - databaseClockOffset - timing.PublishedAt).TotalMilliseconds)])!,
                LatencySummary.Of([.. timings.Select(timing => (timing.ReceivedAt - (timing.CreatedAt - databaseClockOffset)).TotalMilliseconds)])!);
}

/// <summary>One stretch of silence from a Device: a Power off or Coverage gap from the Manifest.</summary>
/// <param name="Serial">The Device.</param>
/// <param name="Start">When the window began.</param>
/// <param name="End">When it ended.</param>
/// <param name="LastReceived">The platform time of the last Reading received before the silence took hold.</param>
internal sealed record Dropout(string Serial, DateTimeOffset Start, DateTimeOffset End, DateTimeOffset LastReceived);

/// <summary>
/// How well Offline Alerts matched the dropouts (spec #21, story 39). A dropout is long when it outlasts the rule's seconds by
/// <see cref="LongMargin"/>; only long dropouts must raise an Alert, and any Alert that matches none is a false alarm. A dropout that outlasts the
/// rule's seconds by less than that is in between: the rule may fire for it or not, so an Alert inside it is ignored, not a false alarm.
/// </summary>
/// <param name="LongDropouts">Dropouts that must raise an Alert.</param>
/// <param name="Detected">Long dropouts whose Alert was created within the last Received time plus the rule's seconds plus <see cref="Tolerance"/>.</param>
/// <param name="Late">Long dropouts whose Alert came later than that.</param>
/// <param name="Missed">Long dropouts without an Alert.</param>
/// <param name="FalseAlarms">Alerts that matched no long dropout.</param>
internal sealed record OfflineScore(int LongDropouts, int Detected, int Late, int Missed, int FalseAlarms)
{
    /// <summary>How much later than the rule's seconds an Alert may come and still count as detected.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

    /// <summary>How far past the rule's seconds a dropout must last to be long: the sweep needs time to notice, so a dropout shorter than this is not owed an Alert.</summary>
    public static readonly TimeSpan LongMargin = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Scores <paramref name="alerts"/> (Device and platform creation time, already on the dropouts' clock) against <paramref name="dropouts"/>.
    /// An Alert matches the earliest long dropout of its Device that it was created inside (up to <see cref="Tolerance"/> after it ends); each
    /// Alert matches at most one.
    /// </summary>
    public static OfflineScore Of(IReadOnlyList<Dropout> dropouts, IReadOnlyList<(string Serial, DateTimeOffset CreatedAt)> alerts, TimeSpan offlineAfter)
    {
        var unmatched = alerts.OrderBy(alert => alert.CreatedAt).ToList();
        var (detected, late, missed) = (0, 0, 0);
        var longDropouts = dropouts.Where(dropout => dropout.End - dropout.Start >= offlineAfter + LongMargin).ToList();
        foreach (var dropout in longDropouts)
        {
            var match = unmatched.FindIndex(alert => alert.Serial == dropout.Serial && alert.CreatedAt >= dropout.Start && alert.CreatedAt <= dropout.End + Tolerance);
            if (match < 0)
            {
                missed++;
                continue;
            }

            if (unmatched[match].CreatedAt <= dropout.LastReceived + offlineAfter + Tolerance)
            {
                detected++;
            }
            else
            {
                late++;
            }

            unmatched.RemoveAt(match);
        }

        // The band owes nothing and forbids nothing: an Alert inside one is the rule rightly firing before an Alert was owed.
        foreach (var dropout in dropouts.Where(dropout => dropout.End - dropout.Start >= offlineAfter && dropout.End - dropout.Start < offlineAfter + LongMargin))
        {
            unmatched.RemoveAll(alert => alert.Serial == dropout.Serial && alert.CreatedAt >= dropout.Start && alert.CreatedAt <= dropout.End + Tolerance);
        }

        return new(longDropouts.Count, detected, late, missed, unmatched.Count);
    }
}

/// <summary>The hysteresis A/B (spec #21, story 36): the same Readings judged by a rule with no Hysteresis and one with.</summary>
internal static class HysteresisScore
{
    /// <summary>
    /// How many of <paramref name="alerts"/> (Device and Device time raised) fall outside every Fault window of their Device, <c>[start, end)</c>:
    /// the Alerts a threshold hovering on noise raised without any Fault behind them.
    /// </summary>
    public static int FalseAlerts(
        IEnumerable<(string Serial, DateTimeOffset RaisedAt)> alerts, IReadOnlyDictionary<string, IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)>> faults) =>
        alerts.Count(alert => !(faults.GetValueOrDefault(alert.Serial) ?? []).Any(window => alert.RaisedAt >= window.Start && alert.RaisedAt < window.End));

    /// <summary>
    /// How many Fault windows (<c>[start, end)</c>) had one of <paramref name="alerts"/> of their Device open at some point: raised before the window ended
    /// and not resolved (<c>null</c>) before it began. An Alert raised earlier by noise and still open covers a Fault, because a Hysteresis rule does not
    /// raise a second one. These are the real Faults the rule did not lose.
    /// </summary>
    public static int FaultWindowsCaught(
        IEnumerable<(string Serial, DateTimeOffset RaisedAt, DateTimeOffset? ResolvedAt)> alerts,
        IReadOnlyDictionary<string, IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)>> faults)
    {
        var all = alerts.ToList();
        return faults.Sum(device => device.Value.Count(window =>
            all.Any(alert => alert.Serial == device.Key && alert.RaisedAt < window.End && (alert.ResolvedAt is null || alert.ResolvedAt > window.Start))));
    }

    /// <summary>
    /// Whether the A/B shows what it is for: there are Fault windows to lose, noise made the 0-Hysteresis arm raise false Alerts, the Hysteresis arm
    /// raised no more of them, and it caught as many Fault windows. Without a Fault window a rule that never raises would pass.
    /// </summary>
    public static bool Passes(int faultWindows, int falseWithout, int falseWith, int caughtWithout, int caughtWith) =>
        faultWindows > 0 && falseWithout > 0 && falseWith <= falseWithout && caughtWith >= caughtWithout;

    /// <summary>The share of the <paramref name="without"/> false Alerts that <paramref name="with"/> removed, in percent; null when there were none to remove.</summary>
    public static double? ReductionPercent(int without, int with) => without == 0 ? null : 100.0 * (without - with) / without;
}
