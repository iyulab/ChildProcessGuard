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
    /// Whether the managed processes keep running after the guardian is disposed or its owner exits.
    /// The default, <c>false</c>, is the point of the library: the children end with their owner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a process that starts a long-lived child and then exits on purpose — a launcher that hands
    /// over to the application it started, or an updater that restarts it. While the guardian lives
    /// nothing changes: the processes are tracked, <see cref="ProcessGuardian.KillAllProcessesAsync"/>
    /// and <see cref="ProcessGuardian.TerminateProcessAsync(int, TimeSpan?)"/> terminate them with the
    /// same two-stage sequence, and <see cref="CloseRequest"/> applies.
    /// </para>
    /// <para>
    /// <see cref="ProcessGuardian.Dispose()"/>, the exit of the owning process and Ctrl+C release the
    /// processes instead of terminating them. On Windows the job object is created without
    /// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>, so closing it leaves its processes running; this has
    /// to be decided when the guardian is created, because a process cannot leave a job once it is in
    /// one. On Unix nothing signals the processes on disposal.
    /// </para>
    /// <para>
    /// The guarantee that gives the library its name is gone for such a guardian: if the owner crashes
    /// or is killed, its children are not cleaned up, and the caller is responsible for them.
    /// </para>
    /// </remarks>
    public bool ChildrenOutliveOwner { get; set; } = false;

#if NET6_0_OR_GREATER
    /// <summary>
    /// Whether a <c>SIGTERM</c> sent to the owning process ends the managed processes before the process
    /// exits. Only available in the <c>net10.0</c> build, and only has an effect on Unix. The default is
    /// <c>false</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SIGTERM</c> is the signal that systemd, Docker and Kubernetes send to stop a process, and
    /// .NET does not raise <see cref="AppDomain.ProcessExit"/> for it, so without this option the children
    /// of a process stopped that way keep running. With it, the guardian registers a <c>SIGTERM</c> handler
    /// that disposes it exactly as the process-exit hook would, and then leaves the process to end the
    /// way it would have without the handler. A <c>SIGKILL</c> or a crash still runs no code, and the
    /// children survive it on Unix.
    /// </para>
    /// <para>
    /// It is off by default because it ends the children the moment the signal arrives. An application that
    /// keeps working after <c>SIGTERM</c> until it has drained — a generic host, for example — and wants
    /// its children to run meanwhile should dispose the guardian from its own shutdown instead. The handler
    /// waits for the termination to finish, which can take up to <see cref="ProcessKillTimeout"/>, so an
    /// orchestrator's stop grace period should be longer than that.
    /// </para>
    /// </remarks>
    public bool CleanupOnSigterm { get; set; } = false;
#endif

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
            CloseRequest = this.CloseRequest,
            ChildrenOutliveOwner = this.ChildrenOutliveOwner,
#if NET6_0_OR_GREATER
            CleanupOnSigterm = this.CleanupOnSigterm
#endif
        };
    }
}