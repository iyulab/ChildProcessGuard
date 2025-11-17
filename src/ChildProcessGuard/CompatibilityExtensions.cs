using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ChildProcessGuard;

/// <summary>
/// Extension methods to provide .NET 5+ functionality for .NET Standard 2.1
/// </summary>
internal static class CompatibilityExtensions
{
    /// <summary>
    /// Asynchronously waits for the process to exit (compatibility method for .NET Standard 2.1)
    /// </summary>
    /// <param name="process">The process to wait for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task that completes when the process exits</returns>
    public static async Task WaitForExitAsync(this Process process, CancellationToken cancellationToken = default)
    {
        if (process.HasExited)
            return;

        // On Unix, the Exited event may not fire reliably when processes are killed via signals
        // Poll HasExited on Unix as a fallback
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Poll-based wait for Unix systems
            while (!process.HasExited)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(50, cancellationToken);
            }
            return;
        }

        // Event-based wait for Windows
        var tcs = new TaskCompletionSource<bool>();

        void ProcessExited(object sender, EventArgs e) => tcs.TrySetResult(true);

        process.EnableRaisingEvents = true;
        process.Exited += ProcessExited;

        if (process.HasExited)
        {
            tcs.TrySetResult(true);
        }

        // Handle cancellation
        cancellationToken.Register(() =>
        {
            process.Exited -= ProcessExited;
            tcs.TrySetCanceled(cancellationToken);
        });

        await tcs.Task;
    }

    /// <summary>
    /// Kills the process and optionally its entire process tree (compatibility method)
    /// </summary>
    /// <param name="process">The process to kill</param>
    /// <param name="entireProcessTree">Whether to kill the entire process tree</param>
    public static void KillProcessTree(this Process process, bool entireProcessTree = true)
    {
        if (process.HasExited)
            return;

        if (!entireProcessTree)
        {
            process.Kill();
            return;
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                KillProcessTreeWindows(process.Id);
            }
            else
            {
                KillProcessTreeUnix(process.Id);
            }
        }
        catch
        {
            // Fallback to simple kill
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
    }

    /// <summary>
    /// Kills process tree on Windows using native ToolHelp32 API
    /// </summary>
    /// <param name="processId">Process ID to kill</param>
    private static void KillProcessTreeWindows(int processId)
    {
        try
        {
            // Get all child processes recursively
            var childProcessIds = GetChildProcessIdsNative(processId);
            
            // Kill children first (depth-first)
            foreach (var childId in childProcessIds)
            {
                KillProcessNative(childId);
            }
            
            // Kill the parent process
            KillProcessNative(processId);
        }
        catch
        {
            // Fallback to direct kill
            try
            {
                var process = Process.GetProcessById(processId);
                process.Kill();
            }
            catch
            {
                // Process might have already exited
            }
        }
    }

    /// <summary>
    /// Gets child process IDs using native ToolHelp32 API
    /// </summary>
    /// <param name="parentId">Parent process ID</param>
    /// <returns>List of child process IDs</returns>
    private static List<int> GetChildProcessIdsNative(int parentId)
    {
        var childIds = new List<int>();
        IntPtr snapshot = IntPtr.Zero;

        try
        {
            snapshot = NativeMethods.CreateToolhelp32Snapshot(
                NativeMethods.SnapshotFlags.Process,
                0);

            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            {
                return childIds;
            }

            var entry = new NativeMethods.PROCESSENTRY32
            {
                dwSize = (uint)Marshal.SizeOf(typeof(NativeMethods.PROCESSENTRY32))
            };

            if (!NativeMethods.Process32First(snapshot, ref entry))
            {
                return childIds;
            }

            do
            {
                if (entry.th32ParentProcessID == parentId)
                {
                    childIds.Add((int)entry.th32ProcessID);
                    // Recursively get grandchildren
                    childIds.AddRange(GetChildProcessIdsNative((int)entry.th32ProcessID));
                }
            } while (NativeMethods.Process32Next(snapshot, ref entry));
        }
        catch
        {
            // Return what we have so far
        }
        finally
        {
            if (snapshot != IntPtr.Zero && snapshot != new IntPtr(-1))
            {
                NativeMethods.CloseHandle(snapshot);
            }
        }

        return childIds;
    }

    /// <summary>
    /// Kills a process using native TerminateProcess API
    /// </summary>
    /// <param name="processId">Process ID to kill</param>
    private static void KillProcessNative(int processId)
    {
        IntPtr processHandle = IntPtr.Zero;
        try
        {
            processHandle = NativeMethods.OpenProcess(
                NativeMethods.ProcessAccessFlags.Terminate,
                false,
                processId);

            if (processHandle != IntPtr.Zero)
            {
                NativeMethods.TerminateProcess(processHandle, 1);
            }
        }
        catch
        {
            // Ignore errors - process may have already exited
        }
        finally
        {
            if (processHandle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(processHandle);
            }
        }
    }

    /// <summary>
    /// Kills process tree on Unix systems
    /// </summary>
    /// <param name="processId">Process ID to kill</param>
    private static void KillProcessTreeUnix(int processId)
    {
        try
        {
            // Try to get process group and kill the group
            var pgid = NativeMethods.GetProcessGroup(processId);
            if (pgid > 0)
            {
                NativeMethods.KillProcessGroup(pgid, NativeMethods.SIGTERM);

                // Wait a bit, then force kill if needed
                Task.Delay(100).Wait();

                try
                {
                    var process = Process.GetProcessById(processId);
                    if (!process.HasExited)
                    {
                        NativeMethods.KillProcessGroup(pgid, NativeMethods.SIGKILL);
                    }
                }
                catch
                {
                    // Process might have exited
                }
            }
            else
            {
                // Fallback to kill just the process
                var process = Process.GetProcessById(processId);
                process.Kill();
            }
        }
        catch
        {
            // Final fallback
            try
            {
                var process = Process.GetProcessById(processId);
                process.Kill();
            }
            catch
            {
                // Process might have already exited
            }
        }
    }

    /// <summary>
    /// Gets child processes of a given process (helper method)
    /// </summary>
    /// <param name="parentId">Parent process ID</param>
    /// <returns>List of child process IDs</returns>
    public static List<int> GetChildProcessIds(int parentId)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return GetChildProcessIdsNative(parentId);
        }
        else
        {
            // Unix: Use /proc filesystem for better performance
            return GetChildProcessIdsUnix(parentId);
        }
    }

    /// <summary>
    /// Gets child processes on Unix using /proc filesystem
    /// </summary>
    /// <param name="parentId">Parent process ID</param>
    /// <returns>List of child process IDs</returns>
    private static List<int> GetChildProcessIdsUnix(int parentId)
    {
        var childIds = new List<int>();

        try
        {
            var allProcesses = Process.GetProcesses();

            foreach (var process in allProcesses)
            {
                try
                {
                    if (GetParentProcessId(process.Id) == parentId)
                    {
                        childIds.Add(process.Id);
                        // Recursively get grandchildren
                        childIds.AddRange(GetChildProcessIdsUnix(process.Id));
                    }
                }
                catch
                {
                    // Skip processes we can't access
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // Return empty list on error
        }

        return childIds;
    }

    /// <summary>
    /// Gets the parent process ID (platform-specific implementation)
    /// </summary>
    /// <param name="processId">Process ID</param>
    /// <returns>Parent process ID or -1 if not found</returns>
    private static int GetParentProcessId(int processId)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return GetParentProcessIdWindows(processId);
        }
        else
        {
            return GetParentProcessIdUnix(processId);
        }
    }

    /// <summary>
    /// Gets parent process ID on Windows using native NT API
    /// </summary>
    /// <param name="processId">Process ID</param>
    /// <returns>Parent process ID</returns>
    private static int GetParentProcessIdWindows(int processId)
    {
        IntPtr processHandle = IntPtr.Zero;
        try
        {
            processHandle = NativeMethods.OpenProcess(
                NativeMethods.ProcessAccessFlags.QueryInformation,
                false,
                processId);

            if (processHandle == IntPtr.Zero)
            {
                return -1;
            }

            var pbi = new NativeMethods.PROCESS_BASIC_INFORMATION();
            int returnLength;
            int status = NativeMethods.NtQueryInformationProcess(
                processHandle,
                NativeMethods.ProcessBasicInformation,
                ref pbi,
                Marshal.SizeOf(pbi),
                out returnLength);

            if (status == 0) // STATUS_SUCCESS
            {
                return pbi.InheritedFromUniqueProcessId.ToInt32();
            }
        }
        catch
        {
            // Ignore errors - will return -1
        }
        finally
        {
            if (processHandle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(processHandle);
            }
        }

        return -1;
    }

    /// <summary>
    /// Gets parent process ID on Unix systems
    /// </summary>
    /// <param name="processId">Process ID</param>
    /// <returns>Parent process ID</returns>
    private static int GetParentProcessIdUnix(int processId)
    {
        try
        {
            var statFile = $"/proc/{processId}/stat";
            if (File.Exists(statFile))
            {
                var stat = File.ReadAllText(statFile);
                var parts = stat.Split(' ');
                if (parts.Length >= 4 && int.TryParse(parts[3], out var parentId))
                {
                    return parentId;
                }
            }
        }
        catch
        {
            // Ignore errors
        }

        return -1;
    }
}