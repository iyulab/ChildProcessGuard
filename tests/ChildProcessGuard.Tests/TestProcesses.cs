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
