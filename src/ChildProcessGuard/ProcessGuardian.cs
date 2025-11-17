using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ChildProcessGuard;

/// <summary>
/// A cross-platform process management library that ensures child processes
/// automatically terminate when the parent process exits unexpectedly.
/// Enhanced version with improved error handling, logging, and cross-platform support.
/// </summary>
public class ProcessGuardian : IDisposable, IAsyncDisposable
{
    private readonly ConcurrentDictionary<int, ManagedProcessInfo> _managedProcesses = new();
    private readonly ProcessGuardianOptions _options;
    private readonly Timer? _cleanupTimer;
    private IntPtr _jobHandle = IntPtr.Zero;
    private int _disposed = 0; // 0 = not disposed, 1 = disposed (thread-safe)
    private readonly bool _isWindows;
    private readonly bool _isUnix;
    private readonly SemaphoreSlim _operationSemaphore;
    private bool _useManualProcessTracking = false; // Fallback when Job Object fails

    /// <summary>
    /// Event raised when a process error occurs
    /// </summary>
    public event EventHandler<ProcessErrorEventArgs>? ProcessError;

    /// <summary>
    /// Event raised when a process lifecycle event occurs
    /// </summary>
    public event EventHandler<ProcessLifecycleEventArgs>? ProcessLifecycleEvent;

    /// <summary>
    /// Event raised when a cleanup operation completes
    /// </summary>
    public event EventHandler<CleanupEventArgs>? CleanupCompleted;

    /// <summary>
    /// Gets the number of currently managed processes
    /// </summary>
    public int ManagedProcessCount => _managedProcesses.Count;

    /// <summary>
    /// Gets whether the guardian is disposed
    /// </summary>
    public bool IsDisposed => _disposed != 0;

    /// <summary>
    /// Gets the configuration options
    /// </summary>
    public ProcessGuardianOptions Options => _options;

    /// <summary>
    /// Initializes a new instance of the ProcessGuardian class with default options.
    /// </summary>
    public ProcessGuardian() : this(ProcessGuardianOptions.Default)
    {
    }

    /// <summary>
    /// Initializes a new instance of the ProcessGuardian class with specified options.
    /// </summary>
    /// <param name="options">Configuration options</param>
    public ProcessGuardian(ProcessGuardianOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        _isUnix = NativeMethods.IsUnixPlatform();
        _operationSemaphore = new SemaphoreSlim(_options.MaxManagedProcesses, _options.MaxManagedProcesses);

        // Register process exit event handler
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        Console.CancelKeyPress += OnCancelKeyPress;

        // Initialize platform-specific features
        if (_isWindows)
        {
            InitializeWindowsJobObject();
        }

        // Start cleanup timer if auto cleanup is enabled
        if (_options.AutoCleanupDisposedProcesses)
        {
            _cleanupTimer = new Timer(PerformCleanup, null, _options.CleanupInterval, _options.CleanupInterval);
        }

        LogMessage("ProcessGuardian initialized", LogLevel.Information);
    }

    /// <summary>
    /// Starts a new process and registers it for management.
    /// </summary>
    /// <param name="fileName">The path to the program to execute</param>
    /// <param name="arguments">The command line arguments</param>
    /// <param name="workingDirectory">The working directory</param>
    /// <param name="environmentVariables">A dictionary of environment variables</param>
    /// <returns>The started process</returns>
    public Process StartProcess(string fileName, string arguments = "", string? workingDirectory = null,
        Dictionary<string, string>? environmentVariables = null)
    {
        ThrowIfDisposed();

        var processStartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = false
        };

        // Set working directory
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            processStartInfo.WorkingDirectory = workingDirectory;
        }

        // Set environment variables
        if (environmentVariables != null)
        {
            foreach (var kvp in environmentVariables)
            {
                processStartInfo.Environment[kvp.Key] = kvp.Value;
            }
        }

        return StartProcessWithStartInfo(processStartInfo);
    }

    /// <summary>
    /// Starts a new process asynchronously and registers it for management.
    /// </summary>
    /// <param name="fileName">The path to the program to execute</param>
    /// <param name="arguments">The command line arguments</param>
    /// <param name="workingDirectory">The working directory</param>
    /// <param name="environmentVariables">A dictionary of environment variables</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The started process</returns>
    public async Task<Process> StartProcessAsync(string fileName, string arguments = "",
        string? workingDirectory = null, Dictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        await _operationSemaphore.WaitAsync(cancellationToken);
        try
        {
            return StartProcess(fileName, arguments, workingDirectory, environmentVariables);
        }
        finally
        {
            _operationSemaphore.Release();
        }
    }

    /// <summary>
    /// Starts a new process with the provided ProcessStartInfo and registers it for management.
    /// This allows full control over all ProcessStartInfo properties including Verb, CreateNoWindow, etc.
    /// </summary>
    /// <param name="startInfo">The ProcessStartInfo instance to use</param>
    /// <returns>The started process</returns>
    public Process StartProcessWithStartInfo(ProcessStartInfo startInfo)
    {
        ThrowIfDisposed();

        if (startInfo == null)
            throw new ArgumentNullException(nameof(startInfo));

        if (_managedProcesses.Count >= _options.MaxManagedProcesses)
        {
            throw new InvalidOperationException($"Maximum number of managed processes ({_options.MaxManagedProcesses}) reached");
        }

        Process? process = null;
        try
        {
            process = new Process { StartInfo = startInfo };
            process.Start();

            var processInfo = new ManagedProcessInfo(
                process,
                startInfo.FileName,
                startInfo.Arguments,
                startInfo.WorkingDirectory,
                startInfo.Environment.Count > 0 ? new Dictionary<string, string>(startInfo.Environment) : null
            );

            // Add to managed processes
            if (!_managedProcesses.TryAdd(process.Id, processInfo))
            {
                throw new InvalidOperationException($"Process with ID {process.Id} is already being managed");
            }

            // Platform-specific setup
            SetupPlatformSpecificProcessManagement(processInfo);

            // Set up process exit event handler
            process.EnableRaisingEvents = true;
            process.Exited += (sender, e) => OnProcessExited(processInfo);

            OnProcessLifecycleEvent(processInfo, ProcessLifecycleEventType.ProcessStarted);
            LogMessage($"Started process: {processInfo}", LogLevel.Information);

            return process;
        }
        catch (Exception ex)
        {
            // Clean up on failure
            if (process != null)
            {
                _managedProcesses.TryRemove(process.Id, out _);
                try { process.Dispose(); } catch { }
            }

            OnProcessError("StartProcess", ex);

            if (_options.ThrowOnProcessOperationFailure)
                throw;

            throw new InvalidOperationException($"Failed to start process: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Terminates all managed child processes.
    /// </summary>
    /// <param name="timeout">Maximum time to wait for graceful termination</param>
    /// <returns>Number of processes successfully terminated</returns>
    public async Task<int> KillAllProcessesAsync(TimeSpan? timeout = null)
    {
        // Allow cleanup even during disposal - don't throw ObjectDisposedException
        timeout ??= _options.ProcessKillTimeout;
        var startTime = DateTime.UtcNow;
        var processesToKill = _managedProcesses.Values.ToList();
        int successCount = 0;
        int failureCount = 0;

        LogMessage($"Terminating {processesToKill.Count} managed processes", LogLevel.Information);

        // First, try graceful termination
        var tasks = processesToKill.Select(async processInfo =>
        {
            try
            {
                if (!processInfo.HasExited)
                {
                    await TerminateProcessAsync(processInfo, timeout.Value);
                    Interlocked.Increment(ref successCount);
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failureCount);
                OnProcessError("KillProcess", ex, processInfo.Id);
            }
        });

        await Task.WhenAll(tasks);

        // Clear the managed processes collection
        _managedProcesses.Clear();

        var duration = DateTime.UtcNow - startTime;
        OnCleanupCompleted(successCount, failureCount, duration);
        LogMessage($"Process termination completed: {successCount} successful, {failureCount} failed", LogLevel.Information);

        return successCount;
    }

    /// <summary>
    /// Terminates all managed child processes synchronously.
    /// </summary>
    public void KillAllProcesses()
    {
        try
        {
            KillAllProcessesAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            OnProcessError("KillAllProcesses", ex);
            if (_options.ThrowOnProcessOperationFailure)
                throw;
        }
    }

    /// <summary>
    /// Removes a specific process from the management list.
    /// </summary>
    /// <param name="process">The process to remove</param>
    /// <returns>True if the process was removed, false if it wasn't being managed</returns>
    public bool RemoveProcess(Process process)
    {
        ThrowIfDisposed();

        if (process == null)
            throw new ArgumentNullException(nameof(process));

        if (_managedProcesses.TryRemove(process.Id, out var processInfo))
        {
            processInfo.IsManaged = false;
            OnProcessLifecycleEvent(processInfo, ProcessLifecycleEventType.ProcessRemoved);
            LogMessage($"Removed process from management: {processInfo}", LogLevel.Information);

            // Dispose the process if it has exited
            try
            {
                if (process.HasExited)
                {
                    process.Dispose();
                }
            }
            catch (InvalidOperationException)
            {
                // Process was already disposed
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets information about a managed process by its ID.
    /// </summary>
    /// <param name="processId">The process ID</param>
    /// <returns>Process information if found, null otherwise</returns>
    public ManagedProcessInfo? GetProcessInfo(int processId)
    {
        ThrowIfDisposed();
        return _managedProcesses.TryGetValue(processId, out var processInfo) ? processInfo : null;
    }

    /// <summary>
    /// Returns a read-only list of all currently managed processes.
    /// </summary>
    /// <returns>A list of managed process information</returns>
    public IReadOnlyList<ManagedProcessInfo> GetManagedProcesses()
    {
        ThrowIfDisposed();
        return _managedProcesses.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// Gets statistics about managed processes.
    /// </summary>
    /// <returns>Process statistics</returns>
    public ProcessStatistics GetStatistics()
    {
        ThrowIfDisposed();

        var processes = _managedProcesses.Values.ToList();
        var runningCount = processes.Count(p => !p.HasExited);
        var exitedCount = processes.Count(p => p.HasExited);
        var totalMemoryUsage = GetTotalMemoryUsage(processes);

        return new ProcessStatistics
        {
            TotalProcesses = processes.Count,
            RunningProcesses = runningCount,
            ExitedProcesses = exitedCount,
            TotalMemoryUsage = totalMemoryUsage,
            AverageRuntime = processes.Count > 0 ?
                TimeSpan.FromTicks((long)processes.Average(p => p.GetRuntime().Ticks)) :
                TimeSpan.Zero
        };
    }

    /// <summary>
    /// Releases all resources used by the ProcessGuardian.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return; // Already disposed

        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Asynchronously releases all resources used by the ProcessGuardian.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return; // Already disposed

        await DisposeAsyncCore().ConfigureAwait(false);
        DisposeUnmanagedResources();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the managed and unmanaged resources used by the ProcessGuardian.
    /// </summary>
    /// <param name="disposing">True if disposing managed resources</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Dispose managed resources
            UnregisterEventHandlers();
            _cleanupTimer?.Dispose();

            try
            {
                KillAllProcesses();
                _managedProcesses.Clear(); // Ensure all processes are removed
            }
            catch (Exception ex)
            {
                OnProcessError("Dispose", ex);
            }

            _operationSemaphore?.Dispose();
        }

        // Always dispose unmanaged resources
        DisposeUnmanagedResources();

        LogMessage("ProcessGuardian disposed", LogLevel.Information);
    }

    /// <summary>
    /// Asynchronous disposal core implementation for managed resources.
    /// </summary>
    protected virtual async ValueTask DisposeAsyncCore()
    {
        // Dispose managed resources asynchronously
        UnregisterEventHandlers();
        _cleanupTimer?.Dispose();

        try
        {
            await KillAllProcessesAsync().ConfigureAwait(false);
            _managedProcesses.Clear(); // Ensure all processes are removed
        }
        catch (Exception ex)
        {
            OnProcessError("DisposeAsync", ex);
        }

        _operationSemaphore?.Dispose();
    }

    /// <summary>
    /// Disposes unmanaged resources (Windows Job Object handle).
    /// </summary>
    private void DisposeUnmanagedResources()
    {
        if (_jobHandle != IntPtr.Zero)
        {
            try
            {
                NativeMethods.CloseHandle(_jobHandle);
            }
            catch
            {
                // Suppress exceptions in disposal to prevent app crash
            }
            finally
            {
                _jobHandle = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// Destructor
    /// </summary>
    ~ProcessGuardian()
    {
        Dispose(false);
    }

    #region Private Methods

    private void SetupPlatformSpecificProcessManagement(ManagedProcessInfo processInfo)
    {
        if (_isWindows && _jobHandle != IntPtr.Zero && !_useManualProcessTracking)
        {
            try
            {
                if (NativeMethods.AssignProcessToJobObject(_jobHandle, processInfo.Process.Handle))
                {
                    processInfo.IsJobAssigned = true;
                    OnProcessLifecycleEvent(processInfo, ProcessLifecycleEventType.JobObjectAssigned);
                    LogMessage($"Process {processInfo.Id} assigned to job object", LogLevel.Debug);
                }
                else
                {
                    var error = NativeMethods.GetLastError();
                    LogMessage($"Failed to assign process {processInfo.Id} to job object (Error: {error}). Using manual tracking for this process.", LogLevel.Warning);
                    _useManualProcessTracking = true;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Job Object assignment failed for process {processInfo.Id}: {ex.Message}. Using manual tracking.", LogLevel.Warning);
                _useManualProcessTracking = true;
                OnProcessError("AssignProcessToJobObject", ex, processInfo.Id);
            }
        }
        else if (_isUnix)
        {
            // NOTE: Process groups cannot be set from parent process due to .NET API limitations.
            // setpgid() must be called from within the child process itself, but .NET does not
            // provide a pre-spawn hook for this purpose. Manual process tree tracking is used instead.
            LogMessage($"Using manual process tree tracking for Unix process {processInfo.Id}", LogLevel.Debug);
        }
        else if (_isWindows && _useManualProcessTracking)
        {
            LogMessage($"Using manual process tree tracking for process {processInfo.Id}", LogLevel.Debug);
        }
    }

    private async Task TerminateProcessAsync(ManagedProcessInfo processInfo, TimeSpan timeout)
    {
        if (processInfo.HasExited)
            return;

        try
        {
            var process = processInfo.Process;
            LogMessage($"Terminating process: {processInfo}", LogLevel.Debug);

            // Try graceful termination first
            if (_isWindows)
            {
                process.CloseMainWindow();
            }
            else
            {
                // Unix: Attempt graceful termination
                // Note: Process groups are not used due to .NET API limitations
                process.CloseMainWindow();
            }

            // Wait for graceful termination
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
                OnProcessLifecycleEvent(processInfo, ProcessLifecycleEventType.ProcessExited);
                return;
            }
            catch (OperationCanceledException)
            {
                // Timeout occurred, force termination
            }

            // Force termination if graceful termination failed
            if (_options.ForceKillOnTimeout && !process.HasExited)
            {
                LogMessage($"Force killing process: {processInfo}", LogLevel.Debug);

                // Kill the entire process tree
                // Windows: Uses Job Object (if available) or manual tree enumeration
                // Unix: Uses Process.Kill with entireProcessTree flag (signals entire tree)
                process.KillProcessTree(entireProcessTree: true);

                OnProcessLifecycleEvent(processInfo, ProcessLifecycleEventType.ProcessTerminated);
            }
        }
        catch (Exception ex)
        {
            OnProcessError("TerminateProcess", ex, processInfo.Id);
            throw;
        }
    }

    private void InitializeWindowsJobObject()
    {
        if (!_isWindows)
            return;

        try
        {
            _jobHandle = NativeMethods.CreateJobObject(IntPtr.Zero, null);

            if (_jobHandle == IntPtr.Zero)
            {
                var error = NativeMethods.GetLastError();
                LogMessage($"Failed to create job object (Error: {error}). Using manual process tracking.", LogLevel.Warning);
                _useManualProcessTracking = true;
                return;
            }

            var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };

            int infoSize = Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr extendedInfoPtr = Marshal.AllocHGlobal(infoSize);

            try
            {
                Marshal.StructureToPtr(info, extendedInfoPtr, false);
                if (!NativeMethods.SetInformationJobObject(_jobHandle,
                    NativeMethods.JobObjectInfoType.ExtendedLimitInformation,
                    extendedInfoPtr, (uint)infoSize))
                {
                    var error = NativeMethods.GetLastError();
                    LogMessage($"Failed to set job object information (Error: {error}). Using manual process tracking.", LogLevel.Warning);
                    NativeMethods.CloseHandle(_jobHandle);
                    _jobHandle = IntPtr.Zero;
                    _useManualProcessTracking = true;
                    return;
                }

                LogMessage("Windows Job Object initialized successfully", LogLevel.Debug);
            }
            finally
            {
                Marshal.FreeHGlobal(extendedInfoPtr);
            }
        }
        catch (Exception ex)
        {
            LogMessage($"Job Object initialization failed: {ex.Message}. Using manual process tracking.", LogLevel.Warning);
            _useManualProcessTracking = true;
            OnProcessError("InitializeWindowsJobObject", ex);
            
            if (_jobHandle != IntPtr.Zero)
            {
                try { NativeMethods.CloseHandle(_jobHandle); } catch { }
                _jobHandle = IntPtr.Zero;
            }

            if (_options.ThrowOnProcessOperationFailure)
                throw;
        }
    }

    private void PerformCleanup(object? state)
    {
        if (_disposed != 0)
            return;

        try
        {
            var startTime = DateTime.UtcNow;
            var processesToRemove = new List<int>();
            int cleanedUp = 0;
            int failed = 0;

            foreach (var kvp in _managedProcesses)
            {
                var processInfo = kvp.Value;
                try
                {
                    if (processInfo.HasExited)
                    {
                        processesToRemove.Add(kvp.Key);
                        processInfo.Process.Dispose();
                        cleanedUp++;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    OnProcessError("AutoCleanup", ex, kvp.Key);
                }
            }

            // Remove disposed processes
            foreach (var processId in processesToRemove)
            {
                _managedProcesses.TryRemove(processId, out _);
            }

            var duration = DateTime.UtcNow - startTime;
            if (cleanedUp > 0 || failed > 0)
            {
                OnCleanupCompleted(cleanedUp, failed, duration);
                LogMessage($"Auto cleanup completed: {cleanedUp} cleaned, {failed} failed", LogLevel.Debug);
            }
        }
        catch (Exception ex)
        {
            OnProcessError("AutoCleanupTimer", ex);
        }
    }

    private long GetTotalMemoryUsage(List<ManagedProcessInfo> processes)
    {
        long totalMemory = 0;
        foreach (var processInfo in processes)
        {
            try
            {
                if (!processInfo.HasExited)
                {
                    totalMemory += processInfo.Process.WorkingSet64;
                }
            }
            catch (Exception)
            {
                // Ignore memory access errors
            }
        }
        return totalMemory;
    }

    private void OnProcessExited(ManagedProcessInfo processInfo)
    {
        OnProcessLifecycleEvent(processInfo, ProcessLifecycleEventType.ProcessExited);
        LogMessage($"Process exited: {processInfo}", LogLevel.Information);
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        try
        {
            Dispose();
        }
        catch (Exception ex)
        {
            OnProcessError("ProcessExit", ex);
        }
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        try
        {
            Dispose();
        }
        catch (Exception ex)
        {
            OnProcessError("CancelKeyPress", ex);
        }
    }

    private void UnregisterEventHandlers()
    {
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        Console.CancelKeyPress -= OnCancelKeyPress;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed != 0)
            throw new ObjectDisposedException(nameof(ProcessGuardian));
    }

    private void OnProcessError(string operation, Exception exception, int? processId = null)
    {
        LogMessage($"Process error in {operation}: {exception.Message}", LogLevel.Error);
        ProcessError?.Invoke(this, new ProcessErrorEventArgs(operation, exception, processId));
    }

    private void OnProcessLifecycleEvent(ManagedProcessInfo processInfo, ProcessLifecycleEventType eventType)
    {
        ProcessLifecycleEvent?.Invoke(this, new ProcessLifecycleEventArgs(processInfo, eventType));
    }

    private void OnCleanupCompleted(int cleaned, int failed, TimeSpan duration)
    {
        CleanupCompleted?.Invoke(this, new CleanupEventArgs(cleaned, failed, duration));
    }

    private void LogMessage(string message, LogLevel level)
    {
        if (!_options.EnableDetailedLogging && level == LogLevel.Debug)
            return;

        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var logLevel = level.ToString().ToUpperInvariant();
        Console.WriteLine($"[{timestamp}] [{logLevel}] ProcessGuardian: {message}");
    }

    #endregion
}

/// <summary>
/// Statistics about managed processes
/// </summary>
public class ProcessStatistics
{
    /// <summary>
    /// Total number of managed processes
    /// </summary>
    public int TotalProcesses { get; set; }

    /// <summary>
    /// Number of currently running processes
    /// </summary>
    public int RunningProcesses { get; set; }

    /// <summary>
    /// Number of exited processes
    /// </summary>
    public int ExitedProcesses { get; set; }

    /// <summary>
    /// Total memory usage of all running processes
    /// </summary>
    public long TotalMemoryUsage { get; set; }

    /// <summary>
    /// Average runtime of all processes
    /// </summary>
    public TimeSpan AverageRuntime { get; set; }
}

/// <summary>
/// Log levels for internal logging
/// </summary>
internal enum LogLevel
{
    Debug,
    Information,
    Warning,
    Error
}