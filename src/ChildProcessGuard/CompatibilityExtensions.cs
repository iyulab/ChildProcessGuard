using System.ComponentModel;
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

                // Small delay between checks to avoid excessive CPU usage
                // Also gives the OS time to reap the process after a kill signal
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        // Event-based wait for Windows
        var tcs = new TaskCompletionSource<bool>();

        void ProcessExited(object? sender, EventArgs e) => tcs.TrySetResult(true);

        process.EnableRaisingEvents = true;
        process.Exited += ProcessExited;

        try
        {
            if (process.HasExited)
            {
                tcs.TrySetResult(true);
            }

            using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
            {
                await tcs.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            process.Exited -= ProcessExited;
        }
    }

    /// <summary>
    /// Kills the process and optionally its entire process tree (compatibility method)
    /// </summary>
    /// <param name="process">The process to kill</param>
    /// <param name="entireProcessTree">Whether to kill the entire process tree</param>
    public static void KillProcessTree(this Process process, bool entireProcessTree = true)
    {
        try
        {
            if (process.HasExited)
                return;
        }
        catch (InvalidOperationException)
        {
            // Includes ObjectDisposedException
            return;
        }

        if (!entireProcessTree)
        {
            try { process.Kill(); } catch { }
            return;
        }

#if NET5_0_OR_GREATER
        // Prefer the runtime's own implementation; the portable fallback below enumerates
        // descendants itself (ToolHelp32 on Windows, /proc on Linux, libproc on macOS).
        try
        {
            process.Kill(entireProcessTree: true);
            return;
        }
        catch (InvalidOperationException)
        {
            // Process already exited
            return;
        }
        catch
        {
            // Fall through to the portable implementation
        }
#endif

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                KillProcessTreeWindows(process.Id);
            }
            else
            {
                SignalProcessTreeUnix(process.Id, NativeMethods.SIGKILL);
            }
        }
        catch
        {
            // Fallback to simple kill
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
                // Process already exited or disposed
            }
        }
    }

    /// <summary>
    /// Asks the process and its descendants to terminate gracefully. On Unix this sends SIGTERM to
    /// the process tree; on Windows it posts a close message to the main window, which only reaches
    /// processes that have one.
    /// </summary>
    /// <returns>True if a termination request was delivered to the root process</returns>
    internal static bool RequestTermination(this Process process)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return process.CloseMainWindow();
        }

        return SignalProcessTreeUnix(process.Id, NativeMethods.SIGTERM);
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
        var parentChildMap = new Dictionary<int, List<int>>();
        IntPtr snapshot = IntPtr.Zero;

        try
        {
            snapshot = NativeMethods.CreateToolhelp32Snapshot(
                NativeMethods.SnapshotFlags.Process, 0);

            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
                return new List<int>();

            var entry = new NativeMethods.PROCESSENTRY32
            {
                dwSize = (uint)Marshal.SizeOf(typeof(NativeMethods.PROCESSENTRY32))
            };

            if (!NativeMethods.Process32First(snapshot, ref entry))
                return new List<int>();

            // Build parent-child map from single snapshot
            do
            {
                var pid = (int)entry.th32ProcessID;
                var ppid = (int)entry.th32ParentProcessID;

                if (!parentChildMap.ContainsKey(ppid))
                    parentChildMap[ppid] = new List<int>();

                parentChildMap[ppid].Add(pid);
            } while (NativeMethods.Process32Next(snapshot, ref entry));
        }
        catch
        {
            return new List<int>();
        }
        finally
        {
            if (snapshot != IntPtr.Zero && snapshot != new IntPtr(-1))
            {
                NativeMethods.CloseHandle(snapshot);
            }
        }

        // Traverse tree from parentId
        var result = new List<int>();
        CollectDescendants(parentId, parentChildMap, result);
        return result;
    }

    private static void CollectDescendants(int parentId, Dictionary<int, List<int>> map, List<int> result)
    {
        if (!map.TryGetValue(parentId, out var children))
            return;

        foreach (var childId in children)
        {
            result.Add(childId);
            CollectDescendants(childId, map, result);
        }
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
    /// Sends a signal to a process and to every descendant known at the time of the call.
    /// Descendants are enumerated before the root is signalled so that they are still reachable
    /// through their parent pid; a descendant re-parented afterwards is not affected.
    /// </summary>
    /// <param name="rootProcessId">Process ID of the tree root</param>
    /// <param name="signal">Signal number to send</param>
    /// <returns>True if the signal was delivered to the root process</returns>
    internal static bool SignalProcessTreeUnix(int rootProcessId, int signal)
    {
        var descendants = GetDescendantProcessIdsUnix(rootProcessId);

        var delivered = NativeMethods.SendSignal(rootProcessId, signal) == 0;

        foreach (var pid in descendants)
        {
            NativeMethods.SendSignal(pid, signal);
        }

        return delivered;
    }

    /// <summary>
    /// Gets the IDs of every descendant of a process on Unix, walked from a single snapshot of the
    /// process table (Linux: /proc; macOS: libproc), the same sources the runtime uses.
    /// </summary>
    /// <param name="rootProcessId">Process ID of the tree root</param>
    /// <returns>Descendant process IDs, parents before their children; empty if the table cannot be read</returns>
    private static List<int> GetDescendantProcessIdsUnix(int rootProcessId)
    {
        var descendants = new List<int>();

        Dictionary<int, List<int>> childrenByParent;
        try
        {
            childrenByParent = GetChildrenByParentUnix();
        }
        catch
        {
            return descendants;
        }

        var visited = new HashSet<int> { rootProcessId };
        var pending = new Queue<int>();
        pending.Enqueue(rootProcessId);

        while (pending.Count > 0)
        {
            if (!childrenByParent.TryGetValue(pending.Dequeue(), out var children))
                continue;

            foreach (var child in children)
            {
                if (visited.Add(child))
                {
                    descendants.Add(child);
                    pending.Enqueue(child);
                }
            }
        }

        return descendants;
    }

    private static Dictionary<int, List<int>> GetChildrenByParentUnix()
    {
        var parentPids = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? EnumerateParentPidsMacOS()
            : EnumerateParentPidsProcFs();

        var childrenByParent = new Dictionary<int, List<int>>();
        foreach (var (pid, parentPid) in parentPids)
        {
            if (!childrenByParent.TryGetValue(parentPid, out var children))
            {
                children = new List<int>();
                childrenByParent[parentPid] = children;
            }

            children.Add(pid);
        }

        return childrenByParent;
    }

    private static IEnumerable<(int Pid, int ParentPid)> EnumerateParentPidsProcFs()
    {
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var pid))
                continue;

            var parentPid = ReadParentPidFromProcStat(pid);
            if (parentPid >= 0)
                yield return (pid, parentPid);
        }
    }

    private static IEnumerable<(int Pid, int ParentPid)> EnumerateParentPidsMacOS()
    {
        var size = Marshal.SizeOf<NativeMethods.proc_bsdinfo>();

        foreach (var pid in ListAllPidsMacOS())
        {
            // Fails for processes that exited since the pid list was taken; those are skipped.
            if (pid > 0 && NativeMethods.proc_pidinfo(pid, NativeMethods.PROC_PIDTBSDINFO, 0, out var info, size) == size)
                yield return (pid, (int)info.pbi_ppid);
        }
    }

    private static int[] ListAllPidsMacOS()
    {
        // proc_listallpids returns the number of pids written; a full buffer means the table may
        // have grown since it was sized, so retry with a larger one.
        var capacity = Math.Max(NativeMethods.proc_listallpids(null, 0), 0) + 64;

        while (true)
        {
            var buffer = new int[capacity];
            var count = NativeMethods.proc_listallpids(buffer, buffer.Length * sizeof(int));
            if (count < 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            if (count < buffer.Length)
            {
                Array.Resize(ref buffer, count);
                return buffer;
            }

            capacity *= 2;
        }
    }

    /// <summary>
    /// Reads the parent process ID from /proc/[pid]/stat.
    /// </summary>
    /// <param name="processId">Process ID</param>
    /// <returns>Parent process ID, or -1 if the process no longer exists or the file cannot be parsed</returns>
    private static int ReadParentPidFromProcStat(int processId)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{processId}/stat");

            // The comm field (2nd field) is enclosed in parentheses and can contain
            // spaces and parentheses. Find the last ')' to skip it safely.
            var closeParen = stat.LastIndexOf(')');
            if (closeParen >= 0 && closeParen + 2 < stat.Length)
            {
                // After ')' comes: " state ppid ..."
                var rest = stat.Substring(closeParen + 2).TrimStart();
                var parts = rest.Split(' ');
                // parts[0] = state, parts[1] = ppid
                if (parts.Length >= 2 && int.TryParse(parts[1], out var parentId))
                {
                    return parentId;
                }
            }
        }
        catch
        {
            // The process exited, or /proc is not readable
        }

        return -1;
    }
}