using System.ComponentModel;
using System.Diagnostics;

namespace FieldTrack.Simulator.Bench;

/// <summary>Runs the command-line tools the bench leans on (git, docker).</summary>
internal static class Shell
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    /// <summary>The trimmed standard output of <paramref name="file"/> with <paramref name="arguments"/>.</summary>
    /// <exception cref="SimulatorException">The tool is not installed, fails, or does not finish in a minute.</exception>
    public static async Task<string> RunAsync(string file, CancellationToken cancellationToken, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            using var process = Process.Start(start) ?? throw new SimulatorException($"Cannot start {file}.");
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                return process.ExitCode == 0
                    ? (await output).Trim()
                    : throw new SimulatorException($"`{file} {string.Join(' ', arguments)}` failed: {(await error).Trim()}");
            }
            catch (OperationCanceledException)
            {
                // A cancelled or timed-out tool must not keep running behind the bench.
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                throw;
            }
        }
        catch (Win32Exception exception)
        {
            throw new SimulatorException($"Cannot run {file}: {exception.Message}", exception);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SimulatorException($"`{file} {string.Join(' ', arguments)}` did not finish in {_timeout.TotalSeconds:0} s.");
        }
    }
}
