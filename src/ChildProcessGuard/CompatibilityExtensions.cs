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

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Not delegated to the runtime on Windows: Process.Kill(entireProcessTree) opens every
                // process on the machine to find descendants, which takes seconds on a busy system.
                KillProcessTreeWindows(process.Id);
                return;
            }

#if NET5_0_OR_GREATER
            try
            {
                if (Environment.GetEnvironmentVariable("CPG_TRACE_SIGNALS") == "1")
                    Console.Error.WriteLine($"[cpg-signal] runtime Kill(entireProcessTree) self={Process.GetCurrentProcess().Id} root={process.Id} exited={process.HasExited}");
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

            SignalProcessTreeUnix(process.Id, NativeMethods.SIGKILL);
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
    /// Kills a process tree on Windows. Descendants are taken from one snapshot before anything is
    /// killed, and the root is killed first so that it cannot start further children.
    /// </summary>
    /// <param name="processId">Process ID of the tree root</param>
    private static void KillProcessTreeWindows(int processId)
    {
        var descendants = GetDescendantProcessIdsWindows(processId);

        KillProcessNative(processId);

        foreach (var descendantId in descendants)
        {
            KillProcessNative(descendantId);
        }
    }

    /// <summary>
    /// Gets the IDs of every descendant of a process on Windows from one ToolHelp32 snapshot.
    /// Windows keeps a process's recorded parent pid after the parent exits, so the pid may since
    /// have been reused by an unrelated process; a child is only accepted if it was created after
    /// its parent, which is the check the runtime applies as well.
    /// </summary>
    /// <param name="rootProcessId">Process ID of the tree root</param>
    /// <returns>Descendant process IDs, parents before their children</returns>
    internal static List<int> GetDescendantProcessIdsWindows(int rootProcessId)
    {
        var creationTimes = new Dictionary<int, long>();
        long CreationTime(int pid)
        {
            if (!creationTimes.TryGetValue(pid, out var time))
            {
                time = GetCreationTimeWindows(pid);
                creationTimes[pid] = time;
            }

            return time;
        }

        return WalkDescendants(rootProcessId, GetChildrenByParentWindows(), (parentId, childId) =>
        {
            var parentCreated = CreationTime(parentId);
            return parentCreated != 0 && CreationTime(childId) >= parentCreated;
        });
    }

    private static Dictionary<int, List<int>> GetChildrenByParentWindows()
    {
        var childrenByParent = new Dictionary<int, List<int>>();
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.SnapshotFlags.Process, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            return childrenByParent;

        try
        {
            var entry = new NativeMethods.PROCESSENTRY32
            {
                dwSize = (uint)Marshal.SizeOf(typeof(NativeMethods.PROCESSENTRY32))
            };

            if (!NativeMethods.Process32First(snapshot, ref entry))
                return childrenByParent;

            do
            {
                AddChild(childrenByParent, (int)entry.th32ParentProcessID, (int)entry.th32ProcessID);
            } while (NativeMethods.Process32Next(snapshot, ref entry));
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        return childrenByParent;
    }

    /// <summary>
    /// Gets the creation time of a process as a FILETIME value.
    /// </summary>
    /// <returns>The creation time, or 0 if the process cannot be opened (exited or access denied)</returns>
    private static long GetCreationTimeWindows(int processId)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessAccessFlags.QueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
            return 0;

        try
        {
            return NativeMethods.GetProcessTimes(handle, out var creationTime, out _, out _, out _) ? creationTime : 0;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
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

        // TEMPORARY diagnosis (CPG_TRACE_SIGNALS): log every signal and its result.
        var trace = Environment.GetEnvironmentVariable("CPG_TRACE_SIGNALS") == "1";
        int Send(int pid)
        {
            if (trace && !IsDescendantOfCurrentProcessForTrace(pid))
            {
                string blockedName;
                try { using var p = Process.GetProcessById(pid); blockedName = p.ProcessName; } catch { blockedName = "?"; }
                Console.Error.WriteLine($"[cpg-signal] BLOCKED foreign target self={Process.GetCurrentProcess().Id} root={rootProcessId} pid={pid} {blockedName} signal={signal} descendants=[{string.Join(",", descendants)}] stack: " + new StackTrace(1, false).ToString().Replace(Environment.NewLine, " | "));
                return -1;
            }

            var rc = NativeMethods.SendSignal(pid, signal);
            if (trace)
            {
                var errno = rc == 0 ? 0 : Marshal.GetLastWin32Error();
                string name;
                try { using var p = Process.GetProcessById(pid); name = p.ProcessName; } catch { name = "?"; }
                Console.Error.WriteLine($"[cpg-signal] self={Process.GetCurrentProcess().Id} root={rootProcessId} kill({pid} {name}, {signal}) rc={rc} errno={errno} descendants=[{string.Join(",", descendants)}]");
                if (errno == 1)
                    Console.Error.WriteLine("[cpg-signal] EPERM stack: " + new StackTrace(1, false).ToString().Replace(Environment.NewLine, " | "));
            }
            return rc;
        }

        var delivered = Send(rootProcessId) == 0;

        foreach (var pid in descendants)
        {
            Send(pid);
        }

        return delivered;
    }

    // TEMPORARY diagnosis: true if pid descends from the current process in a fresh snapshot.
    private static bool IsDescendantOfCurrentProcessForTrace(int pid)
    {
        var self = Process.GetCurrentProcess().Id;
        var parentOf = new Dictionary<int, int>();
        foreach (var pair in GetChildrenByParentUnix())
            foreach (var child in pair.Value)
                parentOf[child] = pair.Key;

        for (var current = pid; current > 1 && parentOf.TryGetValue(current, out var parent); current = parent)
        {
            if (parent == self)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Gets the IDs of every descendant of a process on Unix, walked from a single snapshot of the
    /// process table (Linux: /proc; macOS: libproc), the same sources the runtime uses.
    /// </summary>
    /// <param name="rootProcessId">Process ID of the tree root</param>
    /// <returns>Descendant process IDs, parents before their children; empty if the table cannot be read</returns>
    internal static List<int> GetDescendantProcessIdsUnix(int rootProcessId)
    {
        Dictionary<int, List<int>> childrenByParent;
        try
        {
            childrenByParent = GetChildrenByParentUnix();
        }
        catch
        {
            return new List<int>();
        }

        // Unix re-parents orphans to init (or a subreaper), so a recorded parent pid always names
        // a live parent and needs no further check.
        return WalkDescendants(rootProcessId, childrenByParent, (_, _) => true);
    }

    /// <summary>
    /// Walks a parent-to-children map breadth-first from a root, following only the links that
    /// <paramref name="isChildOf"/> accepts. Each process is visited once, so a cycle in the map
    /// (possible with stale parent pids) cannot loop.
    /// </summary>
    private static List<int> WalkDescendants(int rootProcessId, Dictionary<int, List<int>> childrenByParent, Func<int, int, bool> isChildOf)
    {
        var descendants = new List<int>();
        var visited = new HashSet<int> { rootProcessId };
        var pending = new Queue<int>();
        pending.Enqueue(rootProcessId);

        while (pending.Count > 0)
        {
            var parentId = pending.Dequeue();
            if (!childrenByParent.TryGetValue(parentId, out var children))
                continue;

            foreach (var childId in children)
            {
                if (!visited.Contains(childId) && isChildOf(parentId, childId))
                {
                    visited.Add(childId);
                    descendants.Add(childId);
                    pending.Enqueue(childId);
                }
            }
        }

        return descendants;
    }

    private static void AddChild(Dictionary<int, List<int>> childrenByParent, int parentId, int childId)
    {
        if (parentId == childId)
            return; // the idle/system entries on Windows list themselves as their own parent

        if (!childrenByParent.TryGetValue(parentId, out var children))
        {
            children = new List<int>();
            childrenByParent[parentId] = children;
        }

        children.Add(childId);
    }

    private static Dictionary<int, List<int>> GetChildrenByParentUnix()
    {
        var parentPids = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? EnumerateParentPidsMacOS()
            : EnumerateParentPidsProcFs();

        var childrenByParent = new Dictionary<int, List<int>>();
        foreach (var (pid, parentPid) in parentPids)
        {
            AddChild(childrenByParent, parentPid, pid);
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