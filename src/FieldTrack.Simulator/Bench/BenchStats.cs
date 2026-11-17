namespace FieldTrack.Simulator.Bench;

/// <summary>The few statistics the bench judges a run by.</summary>
internal static class BenchStats
{
    /// <summary>The middle value of <paramref name="values"/>, or the mean of the middle two.</summary>
    public static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
    }

    /// <summary>
    /// Whether a backlog sampled over a run stayed level: the median of its last third is at most <paramref name="tolerance"/> above the
    /// median of its first third. A burst in the middle that drains again, or a start-up queue, is flat; steady growth is not.
    /// </summary>
    /// <exception cref="ArgumentException">There are fewer than three samples, so no thirds to compare.</exception>
    public static bool IsFlat(IReadOnlyList<double> samples, double tolerance)
    {
        if (samples.Count < 3)
        {
            throw new ArgumentException("A trend needs at least three samples.", nameof(samples));
        }

        var third = samples.Count / 3;
        return Median([.. samples.Skip(samples.Count - third)]) <= Median([.. samples.Take(third)]) + tolerance;
    }
}
