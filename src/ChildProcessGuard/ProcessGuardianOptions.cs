namespace ChildProcessGuard;

/// <summary>
/// Configuration options for ProcessGuardian
/// </summary>
public class ProcessGuardianOptions
{
    /// <summary>
    /// Maximum time to wait for processes to terminate gracefully before force killing
    /// </summary>
    public TimeSpan ProcessKillTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether to enable detailed logging of process operations
    /// </summary>
    public bool EnableDetailedLogging { get; set; } = false;

    /// <summary>
    /// Whether to force kill processes if they don't terminate within the timeout
    /// </summary>
    public bool ForceKillOnTimeout { get; set; } = true;

    /// <summary>
    /// Maximum number of processes that can be managed simultaneously
    /// </summary>
    public int MaxManagedProcesses { get; set; } = 100;

    /// <summary>
    /// Whether to automatically clean up disposed processes from the managed list
    /// </summary>
    public bool AutoCleanupDisposedProcesses { get; set; } = true;

    /// <summary>
    /// Interval for checking and cleaning up disposed processes
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether to use process groups on Unix systems for better process tree management.
    /// NOTE: Currently not supported due to .NET API limitations. Process groups must be set
    /// from within the child process, but .NET does not provide a pre-spawn hook.
    /// This option is reserved for future use when .NET provides the necessary API.
    /// Manual process tree tracking is used instead on Unix systems.
    /// </summary>
    [Obsolete("Unix process groups are not currently supported due to .NET API limitations. This option has no effect.", false)]
    public bool UseProcessGroupsOnUnix { get; set; } = false;

    /// <summary>
    /// Whether to throw exceptions on process operation failures
    /// </summary>
    public bool ThrowOnProcessOperationFailure { get; set; } = false;

    /// <summary>
    /// Creates a default configuration
    /// </summary>
    /// <returns>Default ProcessGuardianOptions</returns>
    public static ProcessGuardianOptions Default => new();

    /// <summary>
    /// Creates a configuration optimized for high-throughput scenarios
    /// </summary>
    /// <returns>High-performance ProcessGuardianOptions</returns>
    public static ProcessGuardianOptions HighPerformance => new()
    {
        ProcessKillTimeout = TimeSpan.FromSeconds(10),
        AutoCleanupDisposedProcesses = true,
        CleanupInterval = TimeSpan.FromMinutes(1),
        MaxManagedProcesses = 1000,
        ForceKillOnTimeout = true
    };

    /// <summary>
    /// Creates a configuration with detailed logging enabled
    /// </summary>
    /// <returns>Debug ProcessGuardianOptions</returns>
    public static ProcessGuardianOptions Debug => new()
    {
        EnableDetailedLogging = true,
        ProcessKillTimeout = TimeSpan.FromMinutes(1),
        ThrowOnProcessOperationFailure = true,
        AutoCleanupDisposedProcesses = true,
        CleanupInterval = TimeSpan.FromSeconds(30)
    };
}