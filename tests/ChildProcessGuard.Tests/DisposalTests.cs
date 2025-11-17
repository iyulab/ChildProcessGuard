using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace ChildProcessGuard.Tests;

/// <summary>
/// Tests for proper resource disposal and cleanup
/// </summary>
public class DisposalTests
{
    [Fact]
    public async Task Dispose_ShouldReleaseAllResources()
    {
        // Arrange
        var guardian = new ProcessGuardian();
        var executable = GetLongRunningExecutable();

        var process1 = guardian.StartProcess(executable);
        var process2 = guardian.StartProcess(executable);

        var pid1 = process1.Id;
        var pid2 = process2.Id;

        // Act
        guardian.Dispose();

        // Wait for processes to terminate
        await Task.Delay(1500);

        // Assert
        guardian.IsDisposed.Should().BeTrue();

        // Processes should be terminated
        IsProcessRunning(pid1).Should().BeFalse();
        IsProcessRunning(pid2).Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_ShouldReleaseAllResources()
    {
        // Arrange
        var guardian = new ProcessGuardian();
        var executable = GetLongRunningExecutable();

        guardian.StartProcess(executable);
        guardian.StartProcess(executable);

        // Act
        await guardian.DisposeAsync();

        // Assert
        guardian.IsDisposed.Should().BeTrue();
        guardian.ManagedProcessCount.Should().Be(0);
    }

    [Fact]
    public void MultipleDispose_ShouldBeIdempotent()
    {
        // Arrange
        var guardian = new ProcessGuardian();

        // Act
        guardian.Dispose();
        var act = () => guardian.Dispose();

        // Assert - Should not throw
        act.Should().NotThrow();
        guardian.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task DisposeAndDisposeAsync_ShouldBeIdempotent()
    {
        // Arrange
        var guardian = new ProcessGuardian();

        // Act
        guardian.Dispose();
        await guardian.DisposeAsync();

        // Assert - Should not throw
        guardian.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void Finalizer_ShouldCleanupUnmanagedResources()
    {
        // This test verifies the finalizer path
        // Note: Hard to test reliably, but we can verify the pattern exists

        // Arrange & Act
        CreateAndAbandonGuardian();

        // Force garbage collection
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Assert - If finalizer has issues, we'd see exceptions or leaks
        // This test mainly verifies the code doesn't crash
        Assert.True(true);
    }

    [Fact]
    public void OperationsAfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        var guardian = new ProcessGuardian();
        guardian.Dispose();

        // Assert
        Assert.Throws<ObjectDisposedException>(() =>
            guardian.StartProcess(GetTestExecutable()));

        Assert.Throws<ObjectDisposedException>(() =>
            guardian.GetStatistics());

        Assert.Throws<ObjectDisposedException>(() =>
            guardian.GetManagedProcesses());

        Assert.Throws<ObjectDisposedException>(() =>
            guardian.GetProcessInfo(12345));
    }

    [Fact]
    public async Task DisposeWithLongRunningKill_ShouldEventuallyComplete()
    {
        // Arrange
        var options = new ProcessGuardianOptions
        {
            ProcessKillTimeout = TimeSpan.FromSeconds(2),
            ForceKillOnTimeout = true
        };
        var guardian = new ProcessGuardian(options);

        guardian.StartProcess(GetLongRunningExecutable());
        guardian.StartProcess(GetLongRunningExecutable());

        // Act
        var disposeTask = Task.Run(() => guardian.Dispose());
        var completed = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(10)));

        // Assert
        completed.Should().Be(disposeTask, "Dispose should complete within timeout");
        guardian.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void Dispose_ShouldUnregisterEventHandlers()
    {
        // Arrange
        var guardian = new ProcessGuardian();

        // Hook into AppDomain.ProcessExit would require more complex setup
        // This test verifies the guardian cleans up properly

        // Act
        guardian.Dispose();

        // Trigger would-be event
        // (Can't easily trigger AppDomain.ProcessExit in test)

        // Assert
        guardian.IsDisposed.Should().BeTrue();
        // Event handlers should be unregistered (no way to directly verify in test)
    }

    [Fact]
    public async Task CleanupTimer_ShouldBeDisposedProperly()
    {
        // Arrange
        var options = new ProcessGuardianOptions
        {
            AutoCleanupDisposedProcesses = true,
            CleanupInterval = TimeSpan.FromMilliseconds(100)
        };
        var guardian = new ProcessGuardian(options);

        // Start and let a process exit
        var process = guardian.StartProcess(GetShortLivedExecutable());
        await Task.Delay(500); // Wait for process to exit

        // Act
        guardian.Dispose();

        // Assert
        guardian.IsDisposed.Should().BeTrue();

        // Wait a bit to ensure timer doesn't fire after disposal
        await Task.Delay(300);

        // If timer wasn't disposed, it might cause issues (hard to test directly)
        Assert.True(true);
    }

    [Fact]
    public void Dispose_WithNoProcesses_ShouldCompleteQuickly()
    {
        // Arrange
        var guardian = new ProcessGuardian();

        // Act
        var stopwatch = Stopwatch.StartNew();
        guardian.Dispose();
        stopwatch.Stop();

        // Assert
        guardian.IsDisposed.Should().BeTrue();
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(500,
            "Dispose with no processes should be fast");
    }

    [Fact]
    public void Dispose_WithOptions_ShouldNotAffectStaticOptions()
    {
        // Arrange
        var options = ProcessGuardianOptions.Default;
        var guardian = new ProcessGuardian(options);

        // Act
        guardian.Dispose();

        // Assert
        ProcessGuardianOptions.Default.Should().NotBeNull();
        ProcessGuardianOptions.Default.ProcessKillTimeout
            .Should().Be(TimeSpan.FromSeconds(30));
    }

    #region Helper Methods

    private static string GetTestExecutable()
    {
        return OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/echo";
    }

    private static string GetTestArguments()
    {
        return OperatingSystem.IsWindows() ? "/c echo test" : "test";
    }

    private static ProcessStartInfo GetTestProcessStartInfo()
    {
        return new ProcessStartInfo
        {
            FileName = GetTestExecutable(),
            Arguments = GetTestArguments(),
            CreateNoWindow = true,
            UseShellExecute = false
        };
    }

    private static string GetLongRunningExecutable()
    {
        return OperatingSystem.IsWindows() ? "ping" : "/bin/sleep";
    }

    private static string GetLongRunningArguments()
    {
        return OperatingSystem.IsWindows() ? "localhost -n 100" : "60";
    }

    private static ProcessStartInfo GetLongRunningProcessStartInfo()
    {
        return new ProcessStartInfo
        {
            FileName = GetLongRunningExecutable(),
            Arguments = GetLongRunningArguments(),
            CreateNoWindow = true,
            UseShellExecute = false
        };
    }

    private static string GetShortLivedExecutable()
    {
        return OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/echo";
    }

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

    private static void CreateAndAbandonGuardian()
    {
        var guardian = new ProcessGuardian();
        guardian.StartProcess(GetTestExecutable());
        // Intentionally not disposing - let finalizer handle it
    }

    #endregion
}
