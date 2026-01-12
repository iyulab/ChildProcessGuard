using System.Collections.ObjectModel;
using System.Diagnostics;

namespace ChildProcessGuard;

/// <summary>
/// Extension methods for ProcessGuardian
/// </summary>
public static class ProcessGuardianExtensions
{
    /// <summary>
    /// Starts multiple processes concurrently and returns them all
    /// </summary>
    /// <param name="guardian">The ProcessGuardian instance</param>
    /// <param name="processInfos">Collection of process information to start</param>
    /// <param name="maxConcurrency">Maximum number of processes to start concurrently</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of started processes</returns>
    public static async Task<List<Process>> StartProcessesBatchAsync(
        this ProcessGuardian guardian,
        IEnumerable<ProcessStartInfo> processInfos,
        int maxConcurrency = 4,
        CancellationToken cancellationToken = default)
    {
        if (guardian == null)
            throw new ArgumentNullException(nameof(guardian));

        var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var tasks = processInfos.Select(async startInfo =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                return guardian.StartProcessWithStartInfo(startInfo);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    /// <summary>
    /// Waits for all managed processes to exit
    /// </summary>
    /// <param name="guardian">The ProcessGuardian instance</param>
    /// <param name="timeout">Maximum time to wait</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if all processes exited within the timeout</returns>
    public static async Task<bool> WaitForAllProcessesAsync(
        this ProcessGuardian guardian,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (guardian == null)
            throw new ArgumentNullException(nameof(guardian));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            var processes = guardian.GetManagedProcesses();
            var tasks = processes
                .Where(p => !p.HasExited)
                .Select(p => p.Process.WaitForExitAsync(cts.Token));

            await Task.WhenAll(tasks);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Gets processes filtered by their status
    /// </summary>
    /// <param name="guardian">The ProcessGuardian instance</param>
    /// <param name="status">The status to filter by</param>
    /// <returns>Filtered list of process information</returns>
    public static IReadOnlyList<ManagedProcessInfo> GetProcessesByStatus(
        this ProcessGuardian guardian,
        ProcessStatus status)
    {
        if (guardian == null)
            throw new ArgumentNullException(nameof(guardian));

        var allProcesses = guardian.GetManagedProcesses();

        return status switch
        {
            ProcessStatus.Running => allProcesses.Where(p => !p.HasExited).ToList().AsReadOnly(),
            ProcessStatus.Exited => allProcesses.Where(p => p.HasExited).ToList().AsReadOnly(),
            ProcessStatus.All => allProcesses,
            _ => throw new ArgumentException($"Unknown process status: {status}", nameof(status))
        };
    }

    /// <summary>
    /// Gets processes that have been running longer than the specified duration
    /// </summary>
    /// <param name="guardian">The ProcessGuardian instance</param>
    /// <param name="minimumRuntime">Minimum runtime threshold</param>
    /// <returns>List of long-running processes</returns>
    public static IReadOnlyList<ManagedProcessInfo> GetLongRunningProcesses(
        this ProcessGuardian guardian,
        TimeSpan minimumRuntime)
    {
        if (guardian == null)
            throw new ArgumentNullException(nameof(guardian));

        return guardian.GetManagedProcesses()
            .Where(p => p.GetRuntime() >= minimumRuntime)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Terminates processes that match the specified predicate
    /// </summary>
    /// <param name="guardian">The ProcessGuardian instance</param>
    /// <param name="predicate">Predicate to match processes for termination</param>
    /// <param name="timeout">Maximum time to wait for each process to terminate</param>
    /// <returns>Number of processes terminated</returns>
    public static async Task<int> TerminateProcessesWhere(
        this ProcessGuardian guardian,
        Func<ManagedProcessInfo, bool> predicate,
        TimeSpan? timeout = null)
    {
        if (guardian == null)
            throw new ArgumentNullException(nameof(guardian));

        if (predicate == null)
            throw new ArgumentNullException(nameof(predicate));

        var processesToTerminate = guardian.GetManagedProcesses()
            .Where(predicate)
            .Where(p => !p.HasExited)
            .ToList();

        int terminatedCount = 0;
        var actualTimeout = timeout ?? guardian.Options.ProcessKillTimeout;

        foreach (var processInfo in processesToTerminate)
        {
            try
            {
                if (!processInfo.HasExited)
                {
                    processInfo.Process.KillProcessTree(entireProcessTree: true);

                    using var cts = new CancellationTokenSource(actualTimeout);
                    await processInfo.Process.WaitForExitAsync(cts.Token);

                    terminatedCount++;
                }
            }
            catch (OperationCanceledException)
            {
                // Process didn't terminate within timeout
            }
            catch (Exception)
            {
                // Error terminating process
            }
        }

        return terminatedCount;
    }

    /// <summary>
    /// Converts environment variables dictionary to read-only
    /// </summary>
    /// <param name="environmentVariables">Environment variables dictionary</param>
    /// <returns>Read-only dictionary</returns>
    internal static IReadOnlyDictionary<string, string> AsReadOnly(this Dictionary<string, string> environmentVariables)
    {
        return new ReadOnlyDictionary<string, string>(environmentVariables);
    }
}

/// <summary>
/// Process status enumeration for filtering
/// </summary>
public enum ProcessStatus
{
    /// <summary>
    /// Currently running processes
    /// </summary>
    Running,

    /// <summary>
    /// Exited processes
    /// </summary>
    Exited,

    /// <summary>
    /// All processes regardless of status
    /// </summary>
    All
}

/// <summary>
/// Builder pattern for creating ProcessGuardian instances
/// </summary>
public class ProcessGuardianBuilder
{
    private ProcessGuardianOptions _options = ProcessGuardianOptions.Default;

    /// <summary>
    /// Sets the process kill timeout
    /// </summary>
    /// <param name="timeout">Timeout duration</param>
    /// <returns>Builder instance</returns>
    public ProcessGuardianBuilder WithKillTimeout(TimeSpan timeout)
    {
        _options.ProcessKillTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Enables or disables detailed logging
    /// </summary>
    /// <param name="enabled">Whether to enable detailed logging</param>
    /// <returns>Builder instance</returns>
    public ProcessGuardianBuilder WithDetailedLogging(bool enabled = true)
    {
        _options.EnableDetailedLogging = enabled;
        return this;
    }

    /// <summary>
    /// Sets the maximum number of managed processes
    /// </summary>
    /// <param name="maxProcesses">Maximum number of processes</param>
    /// <returns>Builder instance</returns>
    public ProcessGuardianBuilder WithMaxProcesses(int maxProcesses)
    {
        _options.MaxManagedProcesses = maxProcesses;
        return this;
    }

    /// <summary>
    /// Enables or disables force kill on timeout
    /// </summary>
    /// <param name="enabled">Whether to enable force kill</param>
    /// <returns>Builder instance</returns>
    public ProcessGuardianBuilder WithForceKillOnTimeout(bool enabled = true)
    {
        _options.ForceKillOnTimeout = enabled;
        return this;
    }

    /// <summary>
    /// Enables or disables auto cleanup of disposed processes
    /// </summary>
    /// <param name="enabled">Whether to enable auto cleanup</param>
    /// <param name="interval">Cleanup interval</param>
    /// <returns>Builder instance</returns>
    public ProcessGuardianBuilder WithAutoCleanup(bool enabled = true, TimeSpan? interval = null)
    {
        _options.AutoCleanupDisposedProcesses = enabled;
        if (interval.HasValue)
        {
            _options.CleanupInterval = interval.Value;
        }
        return this;
    }

    /// <summary>
    /// Enables or disables process groups on Unix systems
    /// NOTE: This feature is currently not supported due to .NET API limitations.
    /// </summary>
    /// <param name="enabled">Whether to use process groups</param>
    /// <returns>Builder instance</returns>
    [Obsolete("Unix process groups are not currently supported due to .NET API limitations. This method has no effect.", false)]
    public ProcessGuardianBuilder WithProcessGroupsOnUnix(bool enabled = true)
    {
        _options.UseProcessGroupsOnUnix = enabled;
        return this;
    }

    /// <summary>
    /// Enables or disables throwing exceptions on process operation failures
    /// </summary>
    /// <param name="enabled">Whether to throw on failures</param>
    /// <returns>Builder instance</returns>
    public ProcessGuardianBuilder WithThrowOnFailure(bool enabled = true)
    {
        _options.ThrowOnProcessOperationFailure = enabled;
        return this;
    }

    /// <summary>
    /// Sets custom options
    /// </summary>
    /// <param name="options">Custom options</param>
    /// <returns>Builder instance</returns>
    public ProcessGuardianBuilder WithOptions(ProcessGuardianOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        return this;
    }

    /// <summary>
    /// Builds the ProcessGuardian instance
    /// </summary>
    /// <returns>Configured ProcessGuardian</returns>
    public ProcessGuardian Build()
    {
        return new ProcessGuardian(_options);
    }

    /// <summary>
    /// Creates a builder with high-performance settings
    /// </summary>
    /// <returns>Builder with high-performance configuration</returns>
    public static ProcessGuardianBuilder HighPerformance()
    {
        return new ProcessGuardianBuilder().WithOptions(ProcessGuardianOptions.HighPerformance);
    }

    /// <summary>
    /// Creates a builder with debug settings
    /// </summary>
    /// <returns>Builder with debug configuration</returns>
    public static ProcessGuardianBuilder Debug()
    {
        return new ProcessGuardianBuilder().WithOptions(ProcessGuardianOptions.Debug);
    }
}