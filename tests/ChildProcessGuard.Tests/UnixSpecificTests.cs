using System.Diagnostics;
using System.Runtime.InteropServices;
using FluentAssertions;
using Xunit;

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

    [SkippableFact]
    public void ProcessGuardianInitialization_OnUnix_ShouldSucceed()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        // Arrange & Act
        _guardian = new ProcessGuardian();

        // Assert
        _guardian.Should().NotBeNull();
        _guardian.IsDisposed.Should().BeFalse();
    }

    [SkippableFact]
    public void ProcessStart_OnUnix_ShouldUseManualTracking()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        // Arrange
        _guardian = new ProcessGuardian();

        // Act
        var process = _guardian.StartProcess("/bin/sleep", "10");

        // Assert
        var processInfo = _guardian.GetProcessInfo(process.Id);
        processInfo.Should().NotBeNull();
        processInfo!.IsManaged.Should().BeTrue();
    }

    [SkippableFact]
    public async Task GracefulTermination_OnUnix_DeliversSIGTERM()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        // Arrange - a child that exits cleanly on SIGTERM; SIGKILL would leave a non-zero exit code
        _guardian = new ProcessGuardian();
        var process = _guardian.StartProcessWithStartInfo(new ProcessStartInfo("/bin/sh", "-c \"trap 'exit 0' TERM; sleep 60 & wait\"")
        {
            UseShellExecute = false,
        });
        await Task.Delay(200); // let the shell install its trap

        // Act
        var terminatedCount = await _guardian.KillAllProcessesAsync(TimeSpan.FromSeconds(5));

        // Assert
        terminatedCount.Should().Be(1);
        process.WaitForExit(2000).Should().BeTrue();
        process.ExitCode.Should().Be(0, "the child should have exited from its SIGTERM trap, not from SIGKILL");
    }

    [SkippableFact]
    public async Task ForcedTermination_OnUnix_KillsDescendants_AndSparesTheCaller()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        // Arrange - a shell that ignores SIGTERM and reports the pid of its sleeping grandchild
        _guardian = new ProcessGuardian(new ProcessGuardianOptions
        {
            ProcessKillTimeout = TimeSpan.FromMilliseconds(300),
            ForceKillOnTimeout = true,
        });
        var process = _guardian.StartProcessWithStartInfo(new ProcessStartInfo("/bin/sh", "-c \"trap '' TERM; sleep 60 & echo $!; wait\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        });
        var grandchildPid = int.Parse(process.StandardOutput.ReadLine()!);

        // Act
        await _guardian.KillAllProcessesAsync();

        // Assert - the tree is gone and this process is still here to observe it
        process.WaitForExit(2000).Should().BeTrue();
        await Task.Delay(200);
        var grandchildAlive = () => Process.GetProcessById(grandchildPid);
        grandchildAlive.Should().Throw<ArgumentException>("the sleeping grandchild should have been killed with the tree");
    }

    [SkippableFact]
    public async Task ProcessTermination_OnUnix_ShouldUseSIGTERM()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        // Arrange
        _guardian = new ProcessGuardian();
        var process = _guardian.StartProcess("/bin/sleep", "60");

        // Act
        var terminatedCount = await _guardian.KillAllProcessesAsync(TimeSpan.FromSeconds(2));

        // Assert
        terminatedCount.Should().Be(1);

        // Wait for termination
        await Task.Delay(500);

        process.HasExited.Should().BeTrue();
    }

    [SkippableFact]
    public async Task ProcessTermination_WithTimeout_ShouldUseSIGKILL()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        // Arrange
        var options = new ProcessGuardianOptions
        {
            ProcessKillTimeout = TimeSpan.FromMilliseconds(100),
            ForceKillOnTimeout = true
        };
        _guardian = new ProcessGuardian(options);

        // Start a process that ignores SIGTERM (would need custom script)
        var process = _guardian.StartProcess("/bin/sleep", "60");

        // Act
        var terminatedCount = await _guardian.KillAllProcessesAsync();

        // Assert
        terminatedCount.Should().Be(1);

        // Even with short timeout, SIGKILL should force termination
        await Task.Delay(1000);
        process.HasExited.Should().BeTrue();
    }

    [SkippableFact]
    public async Task MultipleProcesses_OnUnix_ShouldAllBeTerminated()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        // Arrange
        _guardian = new ProcessGuardian();
        var process1 = _guardian.StartProcess("/bin/sleep", "60");
        var process2 = _guardian.StartProcess("/bin/sleep", "60");
        var process3 = _guardian.StartProcess("/bin/sleep", "60");

        // Act
        var terminatedCount = await _guardian.KillAllProcessesAsync(TimeSpan.FromSeconds(2));

        // Assert
        terminatedCount.Should().Be(3);

        await Task.Delay(500);

        process1.HasExited.Should().BeTrue();
        process2.HasExited.Should().BeTrue();
        process3.HasExited.Should().BeTrue();
    }

    [SkippableFact]
    public void ProcessWithCustomWorkingDirectory_OnUnix_ShouldStart()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        // Arrange
        _guardian = new ProcessGuardian();
        var workingDir = "/tmp";

        // Act
        var process = _guardian.StartProcess("/bin/pwd", "", workingDir);

        // Assert
        process.Should().NotBeNull();

        var processInfo = _guardian.GetProcessInfo(process.Id);
        processInfo!.WorkingDirectory.Should().Be(workingDir);
    }

    [SkippableFact]
    public void ProcessWithEnvironmentVariables_OnUnix_ShouldPassVariables()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        // Arrange
        _guardian = new ProcessGuardian();
        var envVars = new Dictionary<string, string>
        {
            { "TEST_VAR", "test_value" }
        };

        // Act
        var process = _guardian.StartProcess("/bin/env", "", null, envVars);

        // Assert
        var processInfo = _guardian.GetProcessInfo(process.Id);
        processInfo!.EnvironmentVariables.Should().ContainKey("TEST_VAR");
        processInfo.EnvironmentVariables!["TEST_VAR"].Should().Be("test_value");
    }

    [SkippableFact]
    public async Task Dispose_OnUnix_ShouldCleanupAllProcesses()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        // Arrange
        _guardian = new ProcessGuardian();
        var process = _guardian.StartProcess("/bin/sleep", "60");
        var processId = process.Id;

        // Act
        _guardian.Dispose();

        // Assert
        _guardian.IsDisposed.Should().BeTrue();

        // Wait for cleanup
        await Task.Delay(1000);

        var stillRunning = IsProcessRunning(processId);
        stillRunning.Should().BeFalse("Process should be terminated on Dispose");
    }

    #region Helper Methods

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    #endregion
}
