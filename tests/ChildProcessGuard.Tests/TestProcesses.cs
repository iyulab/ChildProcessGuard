using System.Diagnostics;

namespace ChildProcessGuard.Tests;

/// <summary>
/// Child-process command lines shared by the test classes. "Test"/"ShortLived" children exit at
/// once; "LongRunning" children stay alive for ~30 seconds so the guardian has something to kill.
/// </summary>
internal static class TestProcesses
{
    public static string GetTestExecutable() => TestPlatform.IsWindows ? "cmd.exe" : "/bin/sh";

    public static string GetTestArguments() => TestPlatform.IsWindows ? "/c exit 0" : "-c \"exit 0\"";

    public static string GetShortLivedExecutable() => GetTestExecutable();

    public static string GetShortLivedArguments() => GetTestArguments();

    public static string GetLongRunningExecutable() => TestPlatform.IsWindows ? "ping" : "/bin/sleep";

    public static string GetLongRunningArguments() => TestPlatform.IsWindows ? "localhost -n 30" : "30";

    public static ProcessStartInfo GetTestProcessStartInfo() => new()
    {
        FileName = GetTestExecutable(),
        Arguments = GetTestArguments(),
        CreateNoWindow = true,
        UseShellExecute = false,
    };

    public static ProcessStartInfo GetLongRunningProcessStartInfo() => new()
    {
        FileName = GetLongRunningExecutable(),
        Arguments = GetLongRunningArguments(),
        CreateNoWindow = true,
        UseShellExecute = false,
    };

    /// <summary>
    /// Polls a condition until it holds or the timeout passes. The default timeout is generous because
    /// a loaded machine can take seconds to start or reap a process; a passing check returns at once.
    /// </summary>
    /// <returns>Whether the condition held</returns>
    public static bool WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultWaitTimeout);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                return false;

            Thread.Sleep(20);
        }

        return true;
    }

    /// <inheritdoc cref="WaitUntil"/>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultWaitTimeout);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                return false;

            await Task.Delay(20, Xunit.TestContext.Current.CancellationToken);
        }

        return true;
    }

    private static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Waits for a process to exit, failing instead of blocking forever if it does not.
    /// </summary>
    public static void WaitForExitOrFail(Process process, TimeSpan? timeout = null)
    {
        if (!process.WaitForExit((int)(timeout ?? DefaultWaitTimeout).TotalMilliseconds))
            throw new TimeoutException($"Process {process.Id} did not exit within the timeout");
    }

    /// <summary>
    /// Reads one line of a child's redirected standard output, failing instead of blocking forever
    /// if the child never writes it.
    /// </summary>
    public static string ReadLineOrFail(Process process, TimeSpan? timeout = null)
    {
        var read = process.StandardOutput.ReadLineAsync();
        if (!read.Wait(timeout ?? DefaultWaitTimeout))
            throw new TimeoutException($"Process {process.Id} wrote no line to standard output within the timeout");

        return read.Result ?? throw new InvalidOperationException($"Process {process.Id} closed standard output without writing a line");
    }

    public static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
