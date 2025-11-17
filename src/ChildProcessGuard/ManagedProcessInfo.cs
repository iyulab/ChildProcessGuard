using System.Diagnostics;

namespace ChildProcessGuard;

/// <summary>
/// Information about a managed process
/// </summary>
public class ManagedProcessInfo
{
    /// <summary>
    /// The managed process
    /// </summary>
    public Process Process { get; private set; }

    /// <summary>
    /// When the process was started
    /// </summary>
    public DateTime StartTime { get; private set; }

    /// <summary>
    /// The original file name used to start the process
    /// </summary>
    public string OriginalFileName { get; private set; }

    /// <summary>
    /// The original arguments used to start the process
    /// </summary>
    public string OriginalArguments { get; private set; }

    /// <summary>
    /// Whether the process has been assigned to a job object (Windows only)
    /// </summary>
    public bool IsJobAssigned { get; internal set; }

    /// <summary>
    /// The process group ID (Unix only)
    /// </summary>
    public int? ProcessGroupId { get; internal set; }

    /// <summary>
    /// Whether the process is still being managed
    /// </summary>
    public bool IsManaged { get; internal set; } = true;

    /// <summary>
    /// Additional metadata about the process
    /// </summary>
    public Dictionary<string, object> Metadata { get; private set; } = new Dictionary<string, object>();

    /// <summary>
    /// Whether the process has exited
    /// </summary>
    public bool HasExited
    {
        get
        {
            try
            {
                return Process.HasExited;
            }
            catch (InvalidOperationException)
            {
                // Process has been disposed
                return true;
            }
        }
    }

    /// <summary>
    /// The process ID
    /// </summary>
    public int Id => Process.Id;

    /// <summary>
    /// The process name
    /// </summary>
    public string ProcessName
    {
        get
        {
            try
            {
                return Process.ProcessName;
            }
            catch (InvalidOperationException)
            {
                // Process has exited or been disposed
                return "<disposed>";
            }
        }
    }

    /// <summary>
    /// The working directory used to start the process
    /// </summary>
    public string? WorkingDirectory { get; private set; }

    /// <summary>
    /// Environment variables that were set for the process
    /// </summary>
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; private set; }

    /// <summary>
    /// Initializes a new instance of ManagedProcessInfo
    /// </summary>
    /// <param name="process">The process to manage</param>
    /// <param name="originalFileName">The original file name</param>
    /// <param name="originalArguments">The original arguments</param>
    /// <param name="workingDirectory">The working directory</param>
    /// <param name="environmentVariables">The environment variables</param>
    public ManagedProcessInfo(Process process, string originalFileName, string originalArguments,
        string? workingDirectory = null, Dictionary<string, string>? environmentVariables = null)
    {
        Process = process ?? throw new ArgumentNullException(nameof(process));
        OriginalFileName = originalFileName ?? throw new ArgumentNullException(nameof(originalFileName));
        OriginalArguments = originalArguments ?? string.Empty;
        WorkingDirectory = workingDirectory;
        EnvironmentVariables = environmentVariables?.AsReadOnly();
        StartTime = DateTime.UtcNow;
    }

    /// <summary>
    /// Attempts to get the process exit code
    /// </summary>
    /// <returns>The exit code if available, null otherwise</returns>
    public int? GetExitCode()
    {
        try
        {
            return HasExited ? Process.ExitCode : (int?)null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts to get the process exit time
    /// </summary>
    /// <returns>The exit time if available, null otherwise</returns>
    public DateTime? GetExitTime()
    {
        try
        {
            return HasExited ? Process.ExitTime : (DateTime?)null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the process runtime duration
    /// </summary>
    /// <returns>The runtime duration</returns>
    public TimeSpan GetRuntime()
    {
        var endTime = GetExitTime() ?? DateTime.UtcNow;
        return endTime - StartTime;
    }

    /// <summary>
    /// Returns a string representation of the managed process info
    /// </summary>
    /// <returns>String representation</returns>
    public override string ToString()
    {
        var status = HasExited ? "Exited" : "Running";
        return $"{ProcessName} (PID: {Id}, Status: {status}, Runtime: {GetRuntime():hh\\:mm\\:ss})";
    }
}