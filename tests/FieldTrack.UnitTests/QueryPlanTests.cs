using FieldTrack.Simulator.Bench;

namespace FieldTrack.UnitTests;

public sealed class QueryPlanTests
{
    // A trimmed `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` of a count over two partitions, each scanned by an index.
    private const string Explain = """
        [{
          "Plan": {
            "Node Type": "Aggregate", "Actual Rows": 1,
            "Shared Hit Blocks": 120, "Shared Read Blocks": 7,
            "Plans": [
              { "Node Type": "Append", "Actual Rows": 5, "Shared Hit Blocks": 120, "Shared Read Blocks": 7,
                "Plans": [
                  { "Node Type": "Bitmap Heap Scan", "Relation Name": "readings_20260101", "Actual Rows": 3,
                    "Plans": [ { "Node Type": "Bitmap Index Scan", "Index Name": "readings_20260101_location_idx", "Actual Rows": 3 } ] },
                  { "Node Type": "Index Scan", "Relation Name": "readings_20260102", "Index Name": "readings_20260102_asset_id_device_time_idx", "Actual Rows": 2 }
                ] }
            ]
          },
          "Planning Time": 0.25,
          "Execution Time": 4.5
        }]
        """;

    [Fact]
    public void A_plan_reads_back_its_timings_buffers_and_what_it_scanned()
    {
        var plan = QueryPlan.Parse(Explain);

        plan.PlanningMs.ShouldBe(0.25);
        plan.ExecutionMs.ShouldBe(4.5);
        plan.TotalMs.ShouldBe(4.75);
        plan.SharedHitBlocks.ShouldBe(120);
        plan.SharedReadBlocks.ShouldBe(7);
        plan.RowsReturned.ShouldBe(1);
        plan.RelationsScanned.ShouldBe(2);
        plan.NodeTypes.ShouldBe(["Aggregate", "Append", "Bitmap Heap Scan", "Bitmap Index Scan", "Index Scan"]);
    }

    [Fact]
    public void A_plan_knows_whether_an_index_ran()
    {
        QueryPlan.Parse(Explain).UsedIndex.ShouldBeTrue();
        QueryPlan.Parse("""
            [{ "Plan": { "Node Type": "Aggregate", "Actual Rows": 1, "Shared Hit Blocks": 1, "Shared Read Blocks": 0,
                         "Plans": [ { "Node Type": "Seq Scan", "Relation Name": "readings", "Actual Rows": 3 } ] },
               "Planning Time": 0.1, "Execution Time": 1 }]
            """).UsedIndex.ShouldBeFalse();
    }

    [Fact]
    public void A_summary_of_runs_gives_the_cold_run_and_the_median_of_the_warm_ones()
    {
        var cold = QueryPlan.Parse(Explain);
        var warm = Enumerable.Range(1, 10).Select(ms => cold with { ExecutionMs = ms, PlanningMs = 0 }).ToList();

        var timing = QueryTiming.Of(cold, warm);

        timing.Cold.ShouldBe(cold);
        timing.LastWarm.ShouldBe(warm[^1]);
        timing.WarmMs.ShouldBe(Enumerable.Range(1, 10).Select(ms => (double)ms));
        timing.WarmMedianMs.ShouldBe(5.5);
        timing.WarmMinMs.ShouldBe(1);
        timing.WarmMaxMs.ShouldBe(10);
    }
}
