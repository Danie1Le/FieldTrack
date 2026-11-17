using FieldTrack.Domain.Telemetry;

namespace FieldTrack.Infrastructure.Ingest;

/// <summary>
/// What a committed ingest batch did besides storing its new Readings: the worker counts the metrics (spec #21) and
/// pushes the changes live (ADR 0005) once the batch is safe.
/// </summary>
/// <param name="Rejected">The reason of every Rejected message the batch stored.</param>
/// <param name="IdenticalDuplicates">Copies of an already stored Reading with the same content hash, dropped (ADR 0003).</param>
/// <param name="ConflictingDuplicates">Copies of an already stored Reading with a different content hash, dropped so the first copy wins.</param>
/// <param name="AssetIds">The Assets in use that got new Readings, so a new position and maybe a new Asset Status.</param>
/// <param name="RaisedAlertIds">The Alerts raised, including any raised and resolved within the batch.</param>
/// <param name="ResolvedAlertIds">The Active Alerts the batch resolved.</param>
public sealed record WrittenBatch(
    IReadOnlyList<RejectReason> Rejected,
    int IdenticalDuplicates,
    int ConflictingDuplicates,
    IReadOnlyCollection<Guid> AssetIds,
    IReadOnlyCollection<Guid> RaisedAlertIds,
    IReadOnlyCollection<Guid> ResolvedAlertIds);
