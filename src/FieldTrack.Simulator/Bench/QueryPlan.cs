using System.Text.Json;

namespace FieldTrack.Simulator.Bench;

/// <summary>What one <c>EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)</c> run of a query says, boiled down for a result file.</summary>
/// <param name="PlanningMs">Time the planner took.</param>
/// <param name="ExecutionMs">Time the executor took.</param>
/// <param name="RowsReturned">Rows the top plan node produced.</param>
/// <param name="SharedHitBlocks">Blocks found in shared buffers.</param>
/// <param name="SharedReadBlocks">Blocks read from outside shared buffers (the operating system's cache or disk).</param>
/// <param name="NodeTypes">The kinds of plan node that ran, sorted: whether an index scan or a sequential scan did the work.</param>
/// <param name="RelationsScanned">How many Reading tables or partitions the plan scans (joined tables do not count): partition pruning shows as a small number.</param>
internal sealed record QueryPlan(
    double PlanningMs, double ExecutionMs, long RowsReturned, long SharedHitBlocks, long SharedReadBlocks, IReadOnlyList<string> NodeTypes, int RelationsScanned)
{
    /// <summary>Planning and execution: what a client waits for, less the network.</summary>
    public double TotalMs => PlanningMs + ExecutionMs;

    /// <summary>Whether an index scan (plain, index-only or bitmap) ran: false means the planner read the table in full.</summary>
    public bool UsedIndex => NodeTypes.Any(node => node.Contains("Index", StringComparison.Ordinal));

    /// <summary>Reads the output of <c>EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)</c>.</summary>
    public static QueryPlan Parse(string explainJson)
    {
        using var document = JsonDocument.Parse(explainJson);
        var root = document.RootElement[0];
        var plan = root.GetProperty("Plan");
        var nodeTypes = new SortedSet<string>(StringComparer.Ordinal);
        var relations = new HashSet<string>();
        Walk(plan);

        return new QueryPlan(
            root.GetProperty("Planning Time").GetDouble(), root.GetProperty("Execution Time").GetDouble(), plan.GetProperty("Actual Rows").GetInt64(),
            plan.GetProperty("Shared Hit Blocks").GetInt64(), plan.GetProperty("Shared Read Blocks").GetInt64(), [.. nodeTypes], relations.Count);

        void Walk(JsonElement node)
        {
            nodeTypes.Add(node.GetProperty("Node Type").GetString()!);
            if (node.TryGetProperty("Relation Name", out var relation) && relation.GetString()!.StartsWith("readings", StringComparison.Ordinal))
            {
                relations.Add(relation.GetString()!);
            }

            if (node.TryGetProperty("Plans", out var children))
            {
                foreach (var child in children.EnumerateArray())
                {
                    Walk(child);
                }
            }
        }
    }
}

/// <summary>A query's timing: its first run, then several more, summarised by the median (the headline) and the spread.</summary>
/// <param name="Cold">The first run.</param>
/// <param name="LastWarm">The last of the runs after it: what a warm run reads (buffers hit, not read).</param>
/// <param name="WarmMs">Planning plus execution time of each warm run.</param>
/// <param name="WarmMedianMs">Median of <paramref name="WarmMs"/>.</param>
/// <param name="WarmMinMs">Fastest warm run.</param>
/// <param name="WarmMaxMs">Slowest warm run.</param>
internal sealed record QueryTiming(QueryPlan Cold, QueryPlan LastWarm, IReadOnlyList<double> WarmMs, double WarmMedianMs, double WarmMinMs, double WarmMaxMs)
{
    /// <summary>The timing of <paramref name="cold"/> followed by <paramref name="warm"/>.</summary>
    public static QueryTiming Of(QueryPlan cold, IReadOnlyList<QueryPlan> warm)
    {
        var totals = warm.Select(run => run.TotalMs).ToList();
        return new QueryTiming(cold, warm[^1], totals, BenchStats.Median(totals), totals.Min(), totals.Max());
    }
}
