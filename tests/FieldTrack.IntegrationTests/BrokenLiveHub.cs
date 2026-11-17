using FieldTrack.Api.Live;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace FieldTrack.IntegrationTests;

/// <summary>
/// A <see cref="LiveHub"/> whose broadcasts run <paramref name="sendAll"/> instead of reaching clients, so tests can
/// make a push throw or hang. Register it as the <see cref="HubLifetimeManager{THub}"/> singleton.
/// </summary>
internal sealed class BrokenLiveHub(Func<CancellationToken, Task> sendAll)
    : DefaultHubLifetimeManager<LiveHub>(NullLogger<DefaultHubLifetimeManager<LiveHub>>.Instance)
{
    private readonly TaskCompletionSource _sent = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes on the first broadcast, so a test knows the push reached the broken part.</summary>
    public Task Sent => _sent.Task;

    /// <summary>A push that throws.</summary>
    public static BrokenLiveHub Throwing() => new(_ => throw new InvalidOperationException("Push failed on purpose."));

    /// <summary>A push that waits until cancelled, like a dashboard whose socket stopped reading.</summary>
    public static BrokenLiveHub Stalled() => new(cancellationToken => Task.Delay(Timeout.Infinite, cancellationToken));

    /// <inheritdoc />
    public override Task SendAllAsync(string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        _sent.TrySetResult();
        return sendAll(cancellationToken);
    }
}
