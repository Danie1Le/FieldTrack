using FieldTrack.Domain.Telemetry;

namespace FieldTrack.Infrastructure.Ingest;

/// <summary>What one committed ingest batch changed: the worker counts the rejects and pushes the rest live (ADR 0005).</summary>
/// <param name="Rejected">The reason of every Rejected message stored.</param>
/// <param name="AssetIds">The Assets in use that got new Readings, so a new position and maybe a new Asset Status.</param>
/// <param name="RaisedAlertIds">The Alerts raised, including any raised and resolved within the batch.</param>
/// <param name="ResolvedAlertIds">The Active Alerts the batch resolved.</param>
public sealed record WrittenBatch(
    IReadOnlyList<RejectReason> Rejected,
    IReadOnlyCollection<Guid> AssetIds,
    IReadOnlyCollection<Guid> RaisedAlertIds,
    IReadOnlyCollection<Guid> ResolvedAlertIds);
