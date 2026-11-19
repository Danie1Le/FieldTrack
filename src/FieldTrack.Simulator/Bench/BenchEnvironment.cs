using System.Globalization;
using System.Runtime.InteropServices;

namespace FieldTrack.Simulator.Bench;

/// <summary>
/// What a result file says about the machine and stack it was measured on (spec #21): numbers mean little without the
/// git SHA, CPU, RAM, OS, Docker limits, Postgres settings and image tags next to them. Every probe that cannot answer
/// (no git checkout, no Docker CLI, no such compose project) leaves its field null or empty rather than failing the run.
/// </summary>
internal static class BenchEnvironment
{
    // The Postgres settings that decide how fast ingest commits and how much of the key index stays cached.
    private static readonly string[] _postgresSettings =
    [
        "server_version", "shared_buffers", "effective_cache_size", "work_mem", "maintenance_work_mem", "max_connections", "synchronous_commit",
        "fsync", "wal_level", "max_wal_size", "checkpoint_timeout", "track_commit_timestamp",
        // What the query benchmarks' plans depend on.
        "max_parallel_workers_per_gather", "jit", "random_page_cost",
    ];

    /// <summary>
    /// Captures the environment of a benchmark of the compose project <paramref name="composeProject"/> and its database; both are null for a
    /// remote platform, whose containers and settings are out of reach, so only this machine (the load generator) is described.
    /// </summary>
    public static async Task<object> CaptureAsync(string? composeProject, BenchDatabase? database, CancellationToken cancellationToken) => new
    {
        target = database is null ? "remote" : "local",
        gitSha = await TryAsync(() => Shell.RunAsync("git", cancellationToken, "rev-parse", "HEAD")),
        // Uncommitted changes mean the SHA alone does not identify the code that was measured.
        gitDirty = await GitDirtyAsync(cancellationToken),
        os = RuntimeInformation.OSDescription,
        cpu = new { model = CpuModel(), logicalCores = Environment.ProcessorCount },
        ramBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        dotnet = RuntimeInformation.FrameworkDescription,
        docker = database is null ? null : await TryAsync(() => DockerAsync(cancellationToken)),
        containers = composeProject is null ? [] : await TryAsync(() => ContainersAsync(composeProject, cancellationToken)) ?? [],
        postgres = database is null ? null : await TryAsync(() => database.SettingsAsync(_postgresSettings, cancellationToken)),
    };

    /// <summary>Whether the working tree has uncommitted changes; false when git cannot say.</summary>
    public static async Task<bool> GitDirtyAsync(CancellationToken cancellationToken) =>
        await TryAsync(() => Shell.RunAsync("git", cancellationToken, "status", "--porcelain")) is { Length: > 0 };

    private static async Task<T?> TryAsync<T>(Func<Task<T>> probe)
        where T : class
    {
        try
        {
            return await probe();
        }
        catch (Exception exception) when (exception is SimulatorException or Npgsql.NpgsqlException)
        {
            return null;
        }
    }

    // The engine's view: Docker Desktop's VM limits, not the host's.
    private static async Task<object> DockerAsync(CancellationToken cancellationToken)
    {
        var fields = (await Shell.RunAsync("docker", cancellationToken, "info", "--format", "{{.ServerVersion}}|{{.NCPU}}|{{.MemTotal}}|{{.OperatingSystem}}")).Split('|');
        return new { engineVersion = fields[0], cpus = int.Parse(fields[1], CultureInfo.InvariantCulture), memoryBytes = long.Parse(fields[2], CultureInfo.InvariantCulture), operatingSystem = fields[3] };
    }

    // Image tag as composed, image id as pulled, and the per-container CPU and memory limits (0 means unlimited).
    private static async Task<IReadOnlyList<object>> ContainersAsync(string composeProject, CancellationToken cancellationToken)
    {
        var ids = (await Shell.RunAsync("docker", cancellationToken, "ps", "--quiet", "--filter", $"label=com.docker.compose.project={composeProject}"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0)
        {
            return [];
        }

        const string Format = "{{index .Config.Labels \"com.docker.compose.service\"}}|{{.Config.Image}}|{{.Image}}|{{.HostConfig.Memory}}|{{.HostConfig.NanoCpus}}";
        var lines = await Shell.RunAsync("docker", cancellationToken, ["inspect", "--format", Format, .. ids]);
        return [.. lines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(line => line.Split('|')).Select(field => (object)new
        {
            service = field[0],
            image = field[1],
            imageId = field[2],
            memoryLimitBytes = long.Parse(field[3], CultureInfo.InvariantCulture),
            cpuLimit = long.Parse(field[4], CultureInfo.InvariantCulture) / 1e9,
        })];
    }

    private static string? CpuModel() =>
        OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")
        : File.Exists("/proc/cpuinfo") ? File.ReadLines("/proc/cpuinfo").FirstOrDefault(line => line.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim()
        : null;
}
