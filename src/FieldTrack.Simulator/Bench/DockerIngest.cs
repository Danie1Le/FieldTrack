namespace FieldTrack.Simulator.Bench;

/// <summary>
/// The ingest worker as the compose stack runs it: inside the <c>api</c> container. Killing it is <c>docker kill</c> (SIGKILL: no shutdown
/// hook, no flush, no acknowledgement), the crash the benchmark must survive without losing or doubling a Reading (ADR 0004).
/// </summary>
internal sealed class DockerIngest
{
    private static readonly TimeSpan _recoveryTimeout = TimeSpan.FromSeconds(90);

    private readonly string _container;
    private readonly HttpClient _health;

    private DockerIngest(string container, HttpClient health) => (_container, _health) = (container, health);

    /// <summary>Finds the <c>api</c> container of the compose project <paramref name="composeProject"/>; <paramref name="api"/> is where its health endpoint answers.</summary>
    /// <exception cref="SimulatorException">There is no such running container.</exception>
    public static async Task<DockerIngest> FindAsync(string composeProject, Uri api, CancellationToken cancellationToken)
    {
        var id = await Shell.RunAsync(
            "docker", cancellationToken, "ps", "--quiet", "--filter", $"label=com.docker.compose.project={composeProject}", "--filter", "label=com.docker.compose.service=api");
        return id.Length == 0
            ? throw new SimulatorException($"No running api container in compose project '{composeProject}'; check --compose-project.")
            : new DockerIngest(id.Split('\n')[0].Trim(), new HttpClient { BaseAddress = api, Timeout = TimeSpan.FromSeconds(2) });
    }

    /// <summary>SIGKILLs the container, starts it again and returns once it is healthy.</summary>
    /// <exception cref="SimulatorException">The container is not healthy again within 90 s.</exception>
    public async Task KillAndRestartAsync(CancellationToken cancellationToken)
    {
        await Shell.RunAsync("docker", cancellationToken, "kill", _container);
        // Once killed it must come back whatever else happens, or the rest of the run would measure a dead stack.
        await Shell.RunAsync("docker", CancellationToken.None, "start", _container);

        var started = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - started < _recoveryTimeout)
        {
            if (await IsHealthyAsync(cancellationToken))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new SimulatorException($"The api container was not healthy {_recoveryTimeout.TotalSeconds:0} s after it restarted.");
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
