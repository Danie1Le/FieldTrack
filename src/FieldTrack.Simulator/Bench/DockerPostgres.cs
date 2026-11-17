using System.Diagnostics;
using Npgsql;

namespace FieldTrack.Simulator.Bench;

/// <summary>
/// The compose stack's <c>postgres</c> container, restarted before a query benchmark's first run so that run starts with empty shared
/// buffers. The operating system's page cache (inside the Docker VM) is not flushed.
/// </summary>
internal sealed class DockerPostgres
{
    private const int DatabasePort = 5432;

    private static readonly TimeSpan _recoveryTimeout = TimeSpan.FromSeconds(120);

    private readonly string _container;
    private readonly NpgsqlDataSource _dataSource;

    private DockerPostgres(string container, NpgsqlDataSource dataSource) => (_container, _dataSource) = (container, dataSource);

    /// <summary>
    /// Finds the <c>postgres</c> container of the compose project <paramref name="composeProject"/> and checks that it is the database
    /// <paramref name="dataSource"/> reaches: it must publish its port 5432 on the port the data source names. Nothing is restarted before that
    /// holds, so a wrong <c>--compose-project</c> cannot restart another agent's database.
    /// </summary>
    /// <exception cref="SimulatorException">There is no such running container, or it does not serve <paramref name="dataSource"/>.</exception>
    public static async Task<DockerPostgres> FindAsync(string composeProject, NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        var id = await Shell.RunAsync(
            "docker", cancellationToken, "ps", "--quiet", "--filter", $"label=com.docker.compose.project={composeProject}", "--filter", "label=com.docker.compose.service=postgres");
        if (id.Length == 0)
        {
            throw new SimulatorException($"No running postgres container in compose project '{composeProject}'; check --compose-project.");
        }

        var container = id.Split('\n')[0].Trim();
        var port = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).Port;
        var published = await Shell.RunAsync("docker", cancellationToken, "port", container, $"{DatabasePort}/tcp");
        return DockerIngest.ServesPort(published, port)
            ? new DockerPostgres(container, dataSource)
            : throw new SimulatorException(
                $"The postgres container of compose project '{composeProject}' publishes {DatabasePort} as [{published.ReplaceLineEndings(", ")}], not on port {port} that --db points at. " +
                "Check --compose-project and --db: a query benchmark only restarts the database it measures.");
    }

    /// <summary>Restarts the container and returns once the database answers again, with no pooled connection left from before.</summary>
    /// <exception cref="SimulatorException">The database does not answer within 120 s.</exception>
    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        // Not cancellable: a restart that was started must finish, or the bench would leave the database down.
        await Shell.RunAsync("docker", CancellationToken.None, "restart", _container);
        _dataSource.Clear();

        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < _recoveryTimeout)
        {
            try
            {
                await using var command = _dataSource.CreateCommand("SELECT 1");
                await command.ExecuteScalarAsync(cancellationToken);
                return;
            }
            catch (NpgsqlException)
            {
                // Still starting (or the postgis image's one restart after init).
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }

        throw new SimulatorException($"The postgres container did not answer {_recoveryTimeout.TotalSeconds:0} s after it restarted.");
    }
}
