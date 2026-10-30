using System.Diagnostics;
using AwesomeAssertions;
using Xunit;
using static ChildProcessGuard.Tests.TestProcesses;

namespace ChildProcessGuard.Tests;

/// <summary>
/// What happens to the children when the process that owns the guardian ends. The owner is a separate
/// process (<c>ChildProcessGuard.OwnerHarness</c>), because the guardian disposed inside the test
/// process would only show the ordinary <see cref="ProcessGuardian.Dispose()"/> path.
/// </summary>
[Trait("Category", "Process")]
public sealed class OwnerDeathTests : IDisposable
{
    private static readonly string HarnessDirectory = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "ChildProcessGuard.OwnerHarness", "bin", "harness"));

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cpg-owner-" + Guid.NewGuid().ToString("N"));
    private readonly List<int> _pids = new();
    private Process? _owner;

    public OwnerDeathTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        // Whatever a test left behind must not outlive it: this would otherwise leave sleepers on the machine
        foreach (var pid in _pids)
            Kill(pid);

        if (_owner != null)
        {
            try { _owner.KillProcessTree(); } catch { }
            _owner.Dispose();
        }

        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public async Task OwnerExit_TerminatesChildAndGrandchild()
    {
        var pidFile = Path.Combine(_directory, "pid");
        StartOwner(pidFile, "exit");
        WaitForChildTree(pidFile);

        // Let the owner return from Main: the process-exit hook has to end the children
        File.WriteAllText(pidFile + ".go", "");
        WaitForExitOrFail(_owner!);

        (await WaitUntilAsync(() => _pids.All(pid => !IsProcessRunning(pid)))).Should().BeTrue(
            "the children of an owner that exits should end with it");
    }

    [Fact(SkipUnless = nameof(TestPlatform.IsWindows), SkipType = typeof(TestPlatform), Skip = "Requires Windows")]
    public async Task OwnerKilled_OnWindows_TerminatesChildAndGrandchild()
    {
        var pidFile = Path.Combine(_directory, "pid");
        StartOwner(pidFile, "wait");
        WaitForChildTree(pidFile);

        // No managed code runs in the owner after this: only the job object can end the children
        _owner!.Kill();
        WaitForExitOrFail(_owner);

        (await WaitUntilAsync(() => _pids.All(pid => !IsProcessRunning(pid)))).Should().BeTrue(
            "the job object ends the children of an owner that was killed");
    }

    private void StartOwner(string pidFile, string mode)
    {
        var harness = Path.Combine(HarnessDirectory, "ChildProcessGuard.OwnerHarness.dll");
        File.Exists(harness).Should().BeTrue($"the owner harness should have been built to {harness}");

        _owner = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet",
            Arguments = $"\"{harness}\" \"{pidFile}\" {mode}",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
    }

    /// <summary>
    /// Waits until the owner has reported its child and the child has a child of its own, and remembers
    /// both so they can be checked afterwards and cleaned up.
    /// </summary>
    private void WaitForChildTree(string pidFile)
    {
        WaitUntil(() => File.Exists(pidFile)).Should().BeTrue("the owner should report its child");
        var childId = int.Parse(File.ReadAllText(pidFile).Trim());
        _pids.Add(childId);

        List<int> descendants = new();
        WaitUntil(() =>
        {
            descendants = TestPlatform.IsWindows
                ? CompatibilityExtensions.GetDescendantProcessIdsWindows(childId)
                : CompatibilityExtensions.GetDescendantProcessIdsUnix(childId);
            return descendants.Count > 0;
        }).Should().BeTrue("the child should have a child of its own");
        _pids.AddRange(descendants);
    }

    private static void Kill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
        }
        catch
        {
            // Already gone
        }
    }
}
