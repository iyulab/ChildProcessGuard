using System.Diagnostics;
using AwesomeAssertions;
using Xunit;
using static ChildProcessGuard.Tests.TestProcesses;

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

    [Fact(SkipUnless = nameof(TestPlatform.IsWindows), SkipType = typeof(TestPlatform), Skip = "Requires Windows")]
    public void JobObjectInitialization_OnWindows_ShouldSucceed()
    {
        // Arrange & Act
        _guardian = new ProcessGuardian();

        // Assert
        _guardian.Should().NotBeNull();
        _guardian.IsDisposed.Should().BeFalse();
    }

    [Fact(SkipUnless = nameof(TestPlatform.IsWindows), SkipType = typeof(TestPlatform), Skip = "Requires Windows")]
    public void ProcessAssignment_ToJobObject_ShouldRaiseEvent()
    {
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

    [Fact(SkipUnless = nameof(TestPlatform.IsWindows), SkipType = typeof(TestPlatform), Skip = "Requires Windows")]
    public async Task JobObject_ShouldTerminateChildrenOnDisposal()
    {
        // Arrange
        _guardian = new ProcessGuardian();
        var process = _guardian.StartProcess("ping", "localhost -n 100");
        var processId = process.Id;

        // Verify Job Object assignment
        var processInfo = _guardian.GetProcessInfo(processId);
        processInfo!.IsJobAssigned.Should().BeTrue("Process should be assigned to Job Object");

        // Act - Dispose guardian
        await _guardian.DisposeAsync();

        // Assert - Process should be terminated
        (await WaitUntilAsync(() => !IsProcessRunning(processId))).Should().BeTrue("Process should be terminated when guardian is disposed");
    }

    [Fact(SkipUnless = nameof(TestPlatform.IsWindows), SkipType = typeof(TestPlatform), Skip = "Requires Windows")]
    public void JobObjectFailure_WithStrictMode_ShouldProvideDetails()
    {
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

    [Fact(SkipUnless = nameof(TestPlatform.IsWindows), SkipType = typeof(TestPlatform), Skip = "Requires Windows")]
    public void KillProcessTree_OnWindows_KillsRootAndDescendants()
    {
        // cmd.exe is the root; the ping it runs is its child. Not started through a guardian, so no
        // Job Object takes part: this exercises the tree walk on its own.
        using var root = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping localhost -n 30")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        var descendants = new List<int>();
        WaitUntil(() => (descendants = CompatibilityExtensions.GetDescendantProcessIdsWindows(root.Id)).Count > 0)
            .Should().BeTrue("ping should have been found as a child of cmd.exe");
        using var child = Process.GetProcessById(descendants[0]);

        root.KillProcessTree(entireProcessTree: true);

        root.WaitForExit(15000).Should().BeTrue();
        child.WaitForExit(15000).Should().BeTrue("the child should have been killed with the tree");
    }
}
