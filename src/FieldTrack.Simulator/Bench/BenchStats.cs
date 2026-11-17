namespace FieldTrack.Simulator.Bench;

/// <summary>The few statistics the bench judges a run by.</summary>
internal static class BenchStats
{
    // A backlog that starts climbing late leaves the last third's median low; the last few samples show it.
    private const int TailSamples = 10;

    /// <summary>The middle value of <paramref name="values"/>, or the mean of the middle two.</summary>
    public static double Median(IReadOnlyList<double> values) => Percentile(values, 0.5);

    /// <summary>The value at <paramref name="fraction"/> (0 to 1) of <paramref name="values"/>, interpolating between neighbours like SQL <c>percentile_cont</c>.</summary>
    public static double Percentile(IReadOnlyList<double> values, double fraction)
    {
        var sorted = values.Order().ToList();
        var rank = fraction * (sorted.Count - 1);
        var below = (int)Math.Floor(rank);
        return sorted[below] + ((sorted[(int)Math.Ceiling(rank)] - sorted[below]) * (rank - below));
    }

    /// <summary>
    /// Whether a backlog sampled over a run stayed level: the median of its last third, and the median of its last
    /// <see cref="TailSamples"/> samples, are at most <paramref name="tolerance"/> above the median of its first third. A burst in the middle
    /// that drains again, or a start-up queue, is flat; steady growth, or growth that begins near the end, is not.
    /// </summary>
    /// <exception cref="ArgumentException">There are fewer than three samples, so no thirds to compare.</exception>
    public static bool IsFlat(IReadOnlyList<double> samples, double tolerance)
    {
        if (samples.Count < 3)
        {
            throw new ArgumentException("A trend needs at least three samples.", nameof(samples));
        }

        var third = samples.Count / 3;
        var limit = Median([.. samples.Take(third)]) + tolerance;
        return Median([.. samples.Skip(samples.Count - third)]) <= limit && Median([.. samples.Skip(samples.Count - Math.Min(TailSamples, third))]) <= limit;
    }
}
