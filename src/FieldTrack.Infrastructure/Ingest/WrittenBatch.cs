using FieldTrack.Domain.Telemetry;

namespace FieldTrack.Infrastructure.Ingest;

/// <summary>
/// What a committed ingest batch did besides storing its new Readings, for the ingest metrics to count once the
/// batch is safe (spec #21).
/// </summary>
/// <param name="Rejected">The reason of every Rejected message the batch stored.</param>
/// <param name="IdenticalDuplicates">Copies of an already stored Reading with the same content hash, dropped (ADR 0003).</param>
/// <param name="ConflictingDuplicates">Copies of an already stored Reading with a different content hash, dropped so the first copy wins.</param>
public sealed record WrittenBatch(IReadOnlyList<RejectReason> Rejected, int IdenticalDuplicates, int ConflictingDuplicates);
