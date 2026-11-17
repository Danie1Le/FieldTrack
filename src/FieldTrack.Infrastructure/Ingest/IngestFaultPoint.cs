namespace FieldTrack.Infrastructure.Ingest;

/// <summary>
/// A point in an ingest batch where a crash leaves a different amount of work done, so crash tests can prove each one
/// loses nothing and stores nothing twice (ADR 0004, ADR 0005).
/// </summary>
public enum IngestFaultPoint
{
    /// <summary>The batch is copied into the stage tables; nothing is stored yet.</summary>
    AfterCopy,

    /// <summary>Readings are stored and Alert Rules evaluated, but the transaction has not committed.</summary>
    AfterEvaluation,

    /// <summary>The batch has committed, but none of its messages is acknowledged.</summary>
    AfterCommit,
}
