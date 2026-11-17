namespace FieldTrack.Domain.Alerts;

/// <summary>
/// What the ingest worker's 1 s sweep (ADR 0005) knows about one Asset's Device when it checks the Asset's Offline
/// rules; the time of the sweep comes from the <see cref="TimeProvider"/>.
/// </summary>
/// <param name="AttachedAt">When the Asset's open Attachment began.</param>
/// <param name="LastReceivedAt">The Device's latest Received time; null before its first Reading.</param>
/// <param name="CaughtUpAt">
/// When the worker first caught up with the broker backlog after it started; null until then. Readings still queued
/// in the broker would end the silence, so no Offline Alert is raised before this, nor for a rule's seconds after it.
/// </param>
public sealed record SweepTick(DateTimeOffset AttachedAt, DateTimeOffset? LastReceivedAt, DateTimeOffset? CaughtUpAt);
