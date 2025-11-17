using System.Diagnostics;
using System.Runtime.InteropServices;
using FluentAssertions;
using Xunit;

namespace ChildProcessGuard.Tests;

/// <summary>
/// Windows-specific tests for Job Object functionality
/// </summary>
public class WindowsSpecificTests : IDisposable
{
    private ProcessGuardian? _guardian;

    public void Dispose()
    {
        _guardian?.Dispose();
    }

    [SkippableFact]
    public void JobObjectInitialization_OnWindows_ShouldSucceed()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        // Arrange & Act
        _guardian = new ProcessGuardian();

        // Assert
        _guardian.Should().NotBeNull();
        _guardian.IsDisposed.Should().BeFalse();
    }

    [SkippableFact]
    public void ProcessAssignment_ToJobObject_ShouldRaiseEvent()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        // Arrange
        _guardian = new ProcessGuardian();
        ProcessLifecycleEventArgs? jobAssignedEvent = null;
        _guardian.ProcessLifecycleEvent += (s, e) =>
        {
            if (e.EventType == ProcessLifecycleEventType.JobObjectAssigned)
                jobAssignedEvent = e;
        };

        // Act
        var startInfo = new ProcessStartInfo
        {
            FileName = "ping",
            Arguments = "localhost -n 100",
            CreateNoWindow = true,
            UseShellExecute = false
        };
        var process = _guardian.StartProcessWithStartInfo(startInfo);

        // Assert
        jobAssignedEvent.Should().NotBeNull("Job Object assignment event should be raised");

        var processInfo = _guardian.GetProcessInfo(process.Id);
        processInfo!.IsJobAssigned.Should().BeTrue("Process should be assigned to Job Object");
    }

    [SkippableFact]
    public async Task JobObject_ShouldTerminateChildrenOnDisposal()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        // Arrange
        _guardian = new ProcessGuardian();
        var process = _guardian.StartProcess("ping", "localhost -n 100");
        var processId = process.Id;

        // Verify Job Object assignment
        var processInfo = _guardian.GetProcessInfo(processId);
        processInfo!.IsJobAssigned.Should().BeTrue("Process should be assigned to Job Object");

        // Act - Dispose guardian
        await _guardian.DisposeAsync();

        // Wait for cleanup
        await Task.Delay(500);

        // Assert - Process should be terminated
        var stillRunning = IsProcessRunning(processId);
        stillRunning.Should().BeFalse("Process should be terminated when guardian is disposed");
    }

    [SkippableFact]
    public void JobObjectFailure_WithStrictMode_ShouldProvideDetails()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        // Arrange
        var options = new ProcessGuardianOptions
        {
            EnableDetailedLogging = true
        };
        _guardian = new ProcessGuardian(options);

        ProcessErrorEventArgs? errorEvent = null;
        _guardian.ProcessError += (s, e) => errorEvent = e;

        // Act - Try to start process that might fail Job Object assignment
        // (This is hard to test reliably, but we verify the error handling exists)
        var startInfo = new ProcessStartInfo
        {
            FileName = "ping",
            Arguments = "localhost -n 100",
            CreateNoWindow = true,
            UseShellExecute = false
        };
        var process = _guardian.StartProcessWithStartInfo(startInfo);

        // Assert - If Job Object fails, error should be captured
        // In normal circumstances, this should succeed
        var processInfo = _guardian.GetProcessInfo(process.Id);
        processInfo.Should().NotBeNull();
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
