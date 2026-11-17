using System.Diagnostics;
using System.Globalization;

namespace FieldTrack.Simulator.Bench;

/// <summary>What one <c>docker kill</c> of the ingest container did.</summary>
/// <param name="Container">Id of the container that was killed.</param>
/// <param name="WentDown">Whether the measured stack's <c>/health</c> stopped answering after the kill. False means the kill did not crash the stack being measured.</param>
/// <param name="BackUpAfter">How long from the kill until <c>/health</c> answered again.</param>
internal sealed record KillResult(string Container, bool WentDown, TimeSpan BackUpAfter);

/// <summary>
/// The ingest worker as the compose stack runs it: inside the <c>api</c> container. Killing it is <c>docker kill</c> (SIGKILL: no shutdown
/// hook, no flush, no acknowledgement), the crash the benchmark must survive without losing or doubling a Reading (ADR 0004).
/// </summary>
internal sealed class DockerIngest
{
    private const int ApiContainerPort = 8080;

    private static readonly TimeSpan _recoveryTimeout = TimeSpan.FromSeconds(90);

    // A killed container stops answering at once; this long without a failed /health means the kill did not hit the stack being measured.
    private static readonly TimeSpan _downTimeout = TimeSpan.FromSeconds(5);

    private readonly string _container;
    private readonly HttpClient _health;

    private DockerIngest(string container, HttpClient health) => (_container, _health) = (container, health);

    /// <summary>
    /// Finds the <c>api</c> container of the compose project <paramref name="composeProject"/> and checks that it is the one <paramref name="api"/>
    /// reaches: it must publish its port 8080 on the port <paramref name="api"/> names. Nothing is killed before that holds, so a wrong
    /// <c>--compose-project</c> cannot take down another agent's stack.
    /// </summary>
    /// <exception cref="SimulatorException">There is no such running container, or it does not serve <paramref name="api"/>.</exception>
    public static async Task<DockerIngest> FindAsync(string composeProject, Uri api, CancellationToken cancellationToken)
    {
        var id = await Shell.RunAsync(
            "docker", cancellationToken, "ps", "--quiet", "--filter", $"label=com.docker.compose.project={composeProject}", "--filter", "label=com.docker.compose.service=api");
        if (id.Length == 0)
        {
            throw new SimulatorException($"No running api container in compose project '{composeProject}'; check --compose-project.");
        }

        var container = id.Split('\n')[0].Trim();
        var published = await Shell.RunAsync("docker", cancellationToken, "port", container, $"{ApiContainerPort}/tcp");
        if (!ServesPort(published, api.Port))
        {
            throw new SimulatorException(
                $"The api container of compose project '{composeProject}' publishes {ApiContainerPort} as [{published.ReplaceLineEndings(", ")}], not on port {api.Port} that --api points at. " +
                "Check --compose-project and --api: a crash run only kills the container of the stack it measures.");
        }

        return new DockerIngest(container, new HttpClient { BaseAddress = api, Timeout = TimeSpan.FromSeconds(2) });
    }

    /// <summary>Whether <paramref name="dockerPort"/> (the output of <c>docker port</c>: one <c>host:port</c> per line) lists host port <paramref name="port"/>.</summary>
    internal static bool ServesPort(string dockerPort, int port) =>
        dockerPort.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => int.TryParse(line[(line.LastIndexOf(':') + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var published) && published == port);

    /// <summary>SIGKILLs the container, checks the measured stack went down, starts the container again and returns once it is healthy.</summary>
    /// <returns>What the kill did; <see cref="KillResult.WentDown"/> is false when <c>/health</c> never failed.</returns>
    /// <exception cref="SimulatorException">The container is not healthy again within 90 s.</exception>
    public async Task<KillResult> KillAndRestartAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        bool wentDown;
        try
        {
            // Not cancellable: a kill that was sent must be followed by the start below, or the rest of the run would measure a dead stack.
            await Shell.RunAsync("docker", CancellationToken.None, "kill", _container);
            wentDown = await WaitForDownAsync(cancellationToken);
        }
        finally
        {
            await Shell.RunAsync("docker", CancellationToken.None, "start", _container);
        }

        while (clock.Elapsed < _recoveryTimeout)
        {
            if (await IsHealthyAsync(cancellationToken))
            {
                return new KillResult(_container, wentDown, clock.Elapsed);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new SimulatorException($"The api container was not healthy {_recoveryTimeout.TotalSeconds:0} s after it restarted.");
    }

    private async Task<bool> WaitForDownAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < _downTimeout)
        {
            if (!await IsHealthyAsync(cancellationToken))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        return false;
    }

    private async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _health.GetAsync("/health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
