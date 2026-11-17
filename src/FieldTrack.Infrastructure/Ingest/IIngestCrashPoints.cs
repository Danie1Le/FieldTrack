namespace FieldTrack.Infrastructure.Ingest;

/// <summary>
/// The test-only hook into the ingest pipeline (spec #21, Testing Decisions): ingest calls it at every
/// <see cref="IngestCrashPoint"/> of every batch, so a crash test can throw there. Production registers none.
/// </summary>
public interface IIngestCrashPoints
{
    /// <summary>Called when a batch reaches <paramref name="point"/>; throwing fails the batch there.</summary>
    void Reached(IngestCrashPoint point);
}
