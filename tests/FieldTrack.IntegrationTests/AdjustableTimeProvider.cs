namespace FieldTrack.IntegrationTests;

/// <summary>The system clock moved by <see cref="Offset"/>, so tests can issue tokens in the past.</summary>
internal sealed class AdjustableTimeProvider : TimeProvider
{
    public TimeSpan Offset { get; set; }

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;
}
