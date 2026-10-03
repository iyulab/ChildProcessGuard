// These tests only run on Unix; the .NET Framework test target is Windows-only and lacks
// ProcessStartInfo.ArgumentList, so the whole class is excluded there.
#if !NETFRAMEWORK
using System.Diagnostics;
using AwesomeAssertions;
using Xunit;
using static ChildProcessGuard.Tests.TestProcesses;

namespace ChildProcessGuard.Tests;

/// <summary>
/// Unix/Linux-specific tests for Process Group functionality
/// </summary>
public class UnixSpecificTests : IDisposable
{
    private ProcessGuardian? _guardian;

    public void Dispose()
    {
        _guardian?.Dispose();
    }

    [Fact(SkipWhen = nameof(TestPlatform.IsWindows), SkipType = typeof(TestPlatform), Skip = "Requires a Unix platform")]
    public void ProcessGuardianInitialization_OnUnix_ShouldSucceed()
    {
        // Arrange & Act
        _guardian = new ProcessGuardian();

        // Assert
        _guardian.Should().NotBeNull();
        _guardian.IsDisposed.Should().BeFalse();
    }

    [Fact(SkipWhen = nameof(TestPlatform.IsWindows), SkipType = typeof(TestPlatform), Skip = "Requires a Unix platform")]
    public void ProcessStart_OnUnix_ShouldUseManualTracking()
    {
        // Arrange
        _guardian = new ProcessGuardian();

        // Act
        var process = _guardian.StartProcess("/bin/sleep", "10");

        // Assert
        var processInfo = _guardian.GetProcessInfo(process.Id);
        processInfo.Should().NotBeNull();
        processInfo!.IsManaged.Should().BeTrue();
    }

    [Fact(SkipWhen = nameof(TestPlatform.IsWindows), SkipType = typeof(TestPlatform), Skip = "Requires a Unix platform")]
    public async Task GracefulTermination_OnUnix_DeliversSIGTERM()
    {
        // Arrange - a child that exits cleanly on SIGTERM; SIGKILL would leave a non-zero exit code
        _guardian = new ProcessGuardian();
        var process = StartShell("trap 'exit 0' TERM; sleep 60 & wait");
        await Task.Delay(200, TestContext.Current.CancellationToken); // let the shell install its trap

        // Act
        var terminatedCount = await _guardian.KillAllProcessesAsync(TimeSpan.FromSeconds(5));

        // Assert
        terminatedCount.Should().Be(1);
        process.WaitForExit(10000).Should().BeTrue();
        process.ExitCode.Should().Be(0, "the child should have exited from its SIGTERM trap, not from SIGKILL");
    }

    [Fact(SkipWhen = nameof(TestPlatform.IsWindows), SkipType = typeof(TestPlatform), Skip = "Requires a Unix platform")]
    public async Task ForcedTermination_OnUnix_KillsDescendants_AndSparesTheCaller()
    {
        // Arrange - root and grandchild shells both ignore SIGTERM; the grandchild's pid is reported on stdout
        // (the grandchild's loop is bounded: a survivor holding the test output pipe must not hang the run)
        _guardian = new ProcessGuardian(new ProcessGuardianOptions
        {
            ProcessKillTimeout = TimeSpan.FromMilliseconds(300),
            ForceKillOnTimeout = true,
        });
        var process = StartShell(
            "trap '' TERM; sh -c 'trap \"\" TERM; i=0; while [ $i -lt 30 ]; do sleep 1; i=$((i+1)); done' & echo $!; wait",
            redirectStandardOutput: true);
        var grandchildPid = int.Parse(ReadLineOrFail(process));
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Act
        await _guardian.KillAllProcessesAsync();

        // Assert - the tree is gone and this process is still here to observe it
        process.WaitForExit(10000).Should().BeTrue();
        (await WaitUntilAsync(() => !IsProcessRunning(grandchildPid))).Should().BeTrue("the grandchild shell should have been killed with the tree");
    }

    [Fact(SkipWhen = nameof(TestPlatform.IsWindows), SkipType = typeof(TestPlatform), Skip = "Requires a Unix platform")]
    public void SignalProcessTreeUnix_WithSIGKILL_KillsRootAndDescendants()
    {
        // Exercises the portable tree kill directly, independent of the runtime's Kill(entireProcessTree).
        using var process = Process.Start(new ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { "-c", "trap '' TERM; sh -c 'trap \"\" TERM; i=0; while [ $i -lt 30 ]; do sleep 1; i=$((i+1)); done' & echo $!; wait" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        var grandchildPid = int.Parse(ReadLineOrFail(process));

        var delivered = CompatibilityExtensions.SignalProcessTreeUnix(process.Id, 9);

        delivered.Should().BeTrue();
        process.WaitForExit(10000).Should().BeTrue();
        WaitUntil(() => !IsProcessRunning(grandchildPid)).Should().BeTrue("the grandchild shell should have been killed with the tree");
    }

    [Fact(SkipWhen = nameof(TestPlatform.IsWindows), SkipType = typeof(TestPlatform), Skip = "Requires a Unix platform")]
    public async Task MultipleProcesses_OnUnix_ShouldAllBeTerminated()
    {
        // Arrange
        _guardian = new ProcessGuardian();
        var process1 = _guardian.StartProcess("/bin/sleep", "60");
        var process2 = _guardian.StartProcess("/bin/sleep", "60");
        var process3 = _guardian.StartProcess("/bin/sleep", "60");

        // Act
        var terminatedCount = await _guardian.KillAllProcessesAsync(TimeSpan.FromSeconds(2));

        // Assert
        terminatedCount.Should().Be(3);
        (await WaitUntilAsync(() => process1.HasExited && process2.HasExited && process3.HasExited)).Should().BeTrue();
    }

    [Fact(SkipWhen = nameof(TestPlatform.IsWindows), SkipType = typeof(TestPlatform), Skip = "Requires a Unix platform")]
    public void ProcessWithCustomWorkingDirectory_OnUnix_ShouldStart()
    {
        // Arrange
        _guardian = new ProcessGuardian();
        var workingDir = "/tmp";

        // Act - a long-running child, so it is still tracked when the info is read
        var process = _guardian.StartProcess("/bin/sleep", "30", workingDir);

        // Assert
        process.Should().NotBeNull();

        var processInfo = _guardian.GetProcessInfo(process.Id);
        processInfo.Should().NotBeNull();
        processInfo!.WorkingDirectory.Should().Be(workingDir);
    }

    [Fact(SkipWhen = nameof(TestPlatform.IsWindows), SkipType = typeof(TestPlatform), Skip = "Requires a Unix platform")]
    public void ProcessWithEnvironmentVariables_OnUnix_ShouldPassVariables()
    {
        // Arrange
        _guardian = new ProcessGuardian();
        var envVars = new Dictionary<string, string>
        {
            { "TEST_VAR", "test_value" }
        };

        // Act - a long-running child, so it is still tracked when the info is read
        var process = _guardian.StartProcess("/bin/sleep", "30", null, envVars);

        // Assert
        var processInfo = _guardian.GetProcessInfo(process.Id);
        processInfo.Should().NotBeNull();
        processInfo!.EnvironmentVariables.Should().ContainKey("TEST_VAR");
        processInfo.EnvironmentVariables!["TEST_VAR"].Should().Be("test_value");
    }

    [Fact(SkipWhen = nameof(TestPlatform.IsWindows), SkipType = typeof(TestPlatform), Skip = "Requires a Unix platform")]
    public async Task Dispose_OnUnix_ShouldCleanupAllProcesses()
    {
        // Arrange
        _guardian = new ProcessGuardian();
        var process = _guardian.StartProcess("/bin/sleep", "60");
        var processId = process.Id;

        // Act
        _guardian.Dispose();

        // Assert
        _guardian.IsDisposed.Should().BeTrue();

        (await WaitUntilAsync(() => !IsProcessRunning(processId))).Should().BeTrue("Process should be terminated on Dispose");
    }

    [Fact(SkipWhen = nameof(TestPlatform.IsWindows), SkipType = typeof(TestPlatform), Skip = "Requires a Unix platform")]
    public void GetDescendantProcessIdsUnix_FindsChildAndGrandchild()
    {
        using var process = Process.Start(new ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { "-c", "sh -c 'sleep 30 & echo $!; wait' & wait" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;

        try
        {
            var grandchildPid = int.Parse(ReadLineOrFail(process));

            var descendants = new List<int>();
            WaitUntil(() => (descendants = CompatibilityExtensions.GetDescendantProcessIdsUnix(process.Id)).Contains(grandchildPid))
                .Should().BeTrue($"the walk from {process.Id} should reach grandchild {grandchildPid}; found [{string.Join(", ", descendants)}]");
            descendants.Should().HaveCount(2, "the tree is root shell -> inner shell -> sleep");
        }
        finally
        {
            process.KillProcessTree(entireProcessTree: true);
        }
    }

    [Fact(SkipWhen = nameof(TestPlatform.IsWindows), SkipType = typeof(TestPlatform), Skip = "Requires a Unix platform")]
    [Trait("Category", "Fast")]
    public void SignalProcessTreeUnix_WhenRootIsNotARunningChild_SendsNothing()
    {
        // Signal 0 only checks that a signal could be delivered, so nothing is disturbed either way.
        using var self = Process.GetCurrentProcess();

        CompatibilityExtensions.SignalProcessTreeUnix(self.Id, 0).Should().BeFalse("the caller is not its own child");
        CompatibilityExtensions.SignalProcessTreeUnix(1, 0).Should().BeFalse("init/launchd is not a child of the caller");
    }

    private Process StartShell(string script, bool redirectStandardOutput = false)
    {
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = redirectStandardOutput,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
        return _guardian!.StartProcessWithStartInfo(startInfo);
    }
}
#endif
