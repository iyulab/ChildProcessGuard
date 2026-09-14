using System.Diagnostics;
using FluentAssertions;
using Xunit;
using static ChildProcessGuard.Tests.TestProcesses;

namespace ChildProcessGuard.Tests;

/// <summary>
/// Core functionality tests for ProcessGuardian
/// </summary>
public class ProcessGuardianTests : IDisposable
{
    private ProcessGuardian? _guardian;

    public void Dispose()
    {
        _guardian?.Dispose();
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Constructor_WithDefaultOptions_ShouldInitializeSuccessfully()
    {
        // Arrange & Act
        _guardian = new ProcessGuardian();

        // Assert
        _guardian.Should().NotBeNull();
        _guardian.IsDisposed.Should().BeFalse();
        _guardian.ManagedProcessCount.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Constructor_WithCustomOptions_ShouldUseProvidedOptions()
    {
        // Arrange
        var options = new ProcessGuardianOptions
        {
            ProcessKillTimeout = TimeSpan.FromSeconds(15),
            MaxManagedProcesses = 50,
            EnableDetailedLogging = true
        };

        // Act
        _guardian = new ProcessGuardian(options);

        // Assert
        _guardian.Options.ProcessKillTimeout.Should().Be(TimeSpan.FromSeconds(15));
        _guardian.Options.MaxManagedProcesses.Should().Be(50);
        _guardian.Options.EnableDetailedLogging.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Process")]
    public void StartProcess_WithValidExecutable_ShouldStartAndTrackProcess()
    {
        // Arrange
        _guardian = new ProcessGuardian();
        var executable = GetLongRunningExecutable();
        var arguments = GetLongRunningArguments();

        // Act
        var process = _guardian.StartProcess(executable, arguments);

        // Assert
        process.Should().NotBeNull();
        process.HasExited.Should().BeFalse();
        _guardian.ManagedProcessCount.Should().Be(1);

        var processInfo = _guardian.GetProcessInfo(process.Id);
        processInfo.Should().NotBeNull();
        processInfo!.OriginalFileName.Should().Be(executable);
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task StartProcessAsync_WithValidExecutable_ShouldStartProcess()
    {
        // Arrange
        _guardian = new ProcessGuardian();
        var executable = GetLongRunningExecutable();
        var arguments = GetLongRunningArguments();

        // Act
        var process = await _guardian.StartProcessAsync(executable, arguments);

        // Assert
        process.Should().NotBeNull();
        _guardian.ManagedProcessCount.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Process")]
    public void StartProcess_WhenMaxProcessesReached_ShouldThrowException()
    {
        // Arrange
        var options = new ProcessGuardianOptions { MaxManagedProcesses = 2 };
        _guardian = new ProcessGuardian(options);
        var executable = GetLongRunningExecutable();
        var arguments = GetLongRunningArguments();

        // Act
        _guardian.StartProcess(executable, arguments);
        _guardian.StartProcess(executable, arguments);

        // Assert
        var action = () => _guardian.StartProcess(executable, arguments);
        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*Maximum number of managed processes*");
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task KillAllProcessesAsync_ShouldTerminateAllProcesses()
    {
        // Arrange
        _guardian = new ProcessGuardian();
        var executable = GetLongRunningExecutable();
        var arguments = GetLongRunningArguments();

        var process1 = _guardian.StartProcess(executable, arguments);
        var process2 = _guardian.StartProcess(executable, arguments);

        _guardian.ManagedProcessCount.Should().Be(2);

        // Act
        // Use very short timeout on Linux since CloseMainWindow doesn't work for command-line processes
        // Go straight to force kill to avoid wasting time
        var timeout = TestPlatform.IsLinux ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(5);
        var terminatedCount = await _guardian.KillAllProcessesAsync(timeout);

        // Assert
        terminatedCount.Should().Be(2);
        _guardian.ManagedProcessCount.Should().Be(0);

        // Wait a bit for processes to fully terminate
        await Task.Delay(500);

        process1.HasExited.Should().BeTrue();
        process2.HasExited.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task GetStatistics_ShouldReturnAccurateMetrics()
    {
        // Arrange
        _guardian = new ProcessGuardian();
        var executable = GetLongRunningExecutable();
        var arguments = GetLongRunningArguments();

        // Act
        var process1 = _guardian.StartProcess(executable, arguments);
        var process2 = _guardian.StartProcess(executable, arguments);

        // Give processes time to fully start
        await Task.Delay(100);

        var stats = _guardian.GetStatistics();

        // Assert
        stats.TotalProcesses.Should().Be(2);
        stats.RunningProcesses.Should().Be(2);
        stats.ExitedProcesses.Should().Be(0);
        stats.TotalMemoryUsage.Should().BeGreaterThan(0);
    }

    [Fact]
    [Trait("Category", "Process")]
    public void GetManagedProcesses_ShouldReturnAllTrackedProcesses()
    {
        // Arrange
        _guardian = new ProcessGuardian();
        var executable = GetLongRunningExecutable();
        var arguments = GetLongRunningArguments();

        // Act
        _guardian.StartProcess(executable, arguments);
        _guardian.StartProcess(executable, arguments);

        var processes = _guardian.GetManagedProcesses();

        // Assert
        processes.Should().HaveCount(2);
        processes.Should().OnlyContain(p => p.IsManaged);
    }

    [Fact]
    [Trait("Category", "Process")]
    public void RemoveProcess_ShouldUntrackProcess()
    {
        // Arrange
        _guardian = new ProcessGuardian();
        var executable = GetLongRunningExecutable();
        var arguments = GetLongRunningArguments();
        var process = _guardian.StartProcess(executable, arguments);

        // Act
        var removed = _guardian.RemoveProcess(process);

        // Assert
        removed.Should().BeTrue();
        _guardian.ManagedProcessCount.Should().Be(0);
        _guardian.GetProcessInfo(process.Id).Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Process")]
    public void Dispose_ShouldTerminateAllProcesses()
    {
        // Arrange
        _guardian = new ProcessGuardian();
        var executable = GetLongRunningExecutable();
        var arguments = GetLongRunningArguments();

        var process = _guardian.StartProcess(executable, arguments);
        var processId = process.Id;

        // Act
        _guardian.Dispose();

        // Assert
        _guardian.IsDisposed.Should().BeTrue();

        // Wait for termination
        Thread.Sleep(1000);

        // Process should be terminated
        var stillRunning = IsProcessRunning(processId);
        stillRunning.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Dispose_WhenAlreadyDisposed_ShouldNotThrow()
    {
        // Arrange
        _guardian = new ProcessGuardian();

        // Act
        _guardian.Dispose();
        var action = () => _guardian.Dispose();

        // Assert
        action.Should().NotThrow();
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void OperationAfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        _guardian = new ProcessGuardian();
        _guardian.Dispose();

        // Act & Assert
        var action = () => _guardian.StartProcess(GetLongRunningExecutable(), GetLongRunningArguments());
        action.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    [Trait("Category", "Process")]
    public void ProcessLifecycleEvent_ShouldBeRaisedOnProcessStart()
    {
        // Arrange
        _guardian = new ProcessGuardian();
        ProcessLifecycleEventArgs? capturedEvent = null;
        _guardian.ProcessLifecycleEvent += (s, e) => capturedEvent = e;

        // Act
        _guardian.StartProcess(GetLongRunningExecutable(), GetLongRunningArguments());

        // Assert
        capturedEvent.Should().NotBeNull();
        capturedEvent!.EventType.Should().Be(ProcessLifecycleEventType.ProcessStarted);
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task ProcessExitedEvent_ShouldBeRaisedWhenProcessEnds()
    {
        // Arrange
        _guardian = new ProcessGuardian();
        ProcessLifecycleEventArgs? exitEvent = null;
        _guardian.ProcessLifecycleEvent += (s, e) =>
        {
            if (e.EventType == ProcessLifecycleEventType.ProcessExited)
                exitEvent = e;
        };

        // Act
        var process = _guardian.StartProcess(GetShortLivedExecutable(), GetShortLivedArguments());
        await Task.Delay(2000); // Wait for process to exit

        // Assert
        exitEvent.Should().NotBeNull();
        exitEvent!.EventType.Should().Be(ProcessLifecycleEventType.ProcessExited);
    }

}
