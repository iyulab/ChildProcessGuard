using System.Runtime.InteropServices;

namespace ChildProcessGuard.Tests;

/// <summary>
/// Platform checks that compile on every test target, including .NET Framework,
/// where <c>System.OperatingSystem.IsWindows()</c> does not exist.
/// </summary>
internal static class TestPlatform
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
}
