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
    /// Whether to periodically sweep exited processes out of the managed list.
    /// Exited processes are normally removed as soon as they exit; the sweep is a fallback for
    /// processes whose exit notification was never delivered.
    /// </summary>
    public bool AutoCleanupDisposedProcesses { get; set; } = true;

    /// <summary>
    /// Interval of the fallback sweep for exited processes
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
    /// Custom log action. If null, logs are written to Console.WriteLine when EnableDetailedLogging is true.
    /// </summary>
    public Action<string>? LogAction { get; set; }

    /// <summary>
    /// Custom close request for the graceful termination stage. If set, it is called first when a
    /// managed process is terminated, and should ask the process to exit in a way the process
    /// understands — closing its standard input, writing a quit command, or calling a shutdown
    /// endpoint — without waiting for it to exit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Return <c>true</c> when the request was delivered: the guardian then waits up to
    /// <see cref="ProcessKillTimeout"/> for the process to exit, and forces the process tree to
    /// terminate if it is still running and <see cref="ForceKillOnTimeout"/> is set. The built-in
    /// request (<c>SIGTERM</c> to the process tree on Unix, <c>CloseMainWindow</c> on Windows) is not
    /// sent in that case. Return <c>false</c> to fall back to the built-in request.
    /// </para>
    /// <para>
    /// An exception thrown by the callback is treated as <c>false</c> and, unless the process has
    /// exited in the meantime, reported through the <see cref="ProcessGuardian.ProcessError"/> event. The callback can be called from a thread-pool thread, for
    /// several processes at once, and during <see cref="ProcessGuardian.Dispose()"/>.
    /// </para>
    /// </remarks>
    public Func<ManagedProcessInfo, bool>? CloseRequest { get; set; }

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

    /// <summary>
    /// Creates a deep copy of this options instance
    /// </summary>
    /// <returns>A new ProcessGuardianOptions with the same values</returns>
    public ProcessGuardianOptions Clone()
    {
        return new ProcessGuardianOptions
        {
            ProcessKillTimeout = this.ProcessKillTimeout,
            EnableDetailedLogging = this.EnableDetailedLogging,
            ForceKillOnTimeout = this.ForceKillOnTimeout,
            MaxManagedProcesses = this.MaxManagedProcesses,
            AutoCleanupDisposedProcesses = this.AutoCleanupDisposedProcesses,
            CleanupInterval = this.CleanupInterval,
            ThrowOnProcessOperationFailure = this.ThrowOnProcessOperationFailure,
            LogAction = this.LogAction,
            CloseRequest = this.CloseRequest
        };
    }
}