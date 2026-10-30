using System.Diagnostics;
using AwesomeAssertions;
using Xunit;
using static ChildProcessGuard.Tests.TestProcesses;

namespace ChildProcessGuard.Tests;

/// <summary>
/// Tests for <see cref="ProcessGuardianOptions.ChildrenOutliveOwner"/>. The exit of a real owner
/// process is covered by the Native AOT smoke executable (tests/ChildProcessGuard.AotSmoke).
/// </summary>
public class ChildrenOutliveOwnerTests : IDisposable
{
    private readonly List<int> _started = new();

    public void Dispose()
    {
        foreach (var processId in _started)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                process.Kill();
            }
            catch (Exception)
            {
                // Already gone
            }
        }
    }

    private int StartLongRunning(ProcessGuardian guardian)
    {
        var process = guardian.StartProcess(GetLongRunningExecutable(), GetLongRunningArguments());
        _started.Add(process.Id);
        return process.Id;
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Option_IsOffByDefault()
    {
        new ProcessGuardianOptions().ChildrenOutliveOwner.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Clone_CopiesOption()
    {
        var options = new ProcessGuardianOptions { ChildrenOutliveOwner = true };

        options.Clone().ChildrenOutliveOwner.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Builder_WithChildrenOutliveOwner_SetsOption()
    {
        using var guardian = new ProcessGuardianBuilder().WithChildrenOutliveOwner().Build();

        guardian.Options.ChildrenOutliveOwner.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task Dispose_LeavesChildrenRunning()
    {
        var guardian = new ProcessGuardianBuilder().WithChildrenOutliveOwner().Build();
        var processId = StartLongRunning(guardian);
        guardian.ManagedProcessCount.Should().Be(1);

        guardian.Dispose();
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        guardian.IsDisposed.Should().BeTrue();
        guardian.ManagedProcessCount.Should().Be(0);
        IsProcessRunning(processId).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task DisposeAsync_LeavesChildrenRunning()
    {
        var guardian = new ProcessGuardianBuilder().WithChildrenOutliveOwner().Build();
        var processId = StartLongRunning(guardian);

        await guardian.DisposeAsync();
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        guardian.ManagedProcessCount.Should().Be(0);
        IsProcessRunning(processId).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task KillAllProcessesAsync_StillTerminatesChildren()
    {
        using var guardian = new ProcessGuardianBuilder().WithChildrenOutliveOwner().Build();
        var processId = StartLongRunning(guardian);

        var terminated = await guardian.KillAllProcessesAsync(TimeSpan.FromSeconds(5));

        terminated.Should().Be(1);
        WaitUntil(() => !IsProcessRunning(processId)).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task TerminateProcessAsync_StillTerminatesChild()
    {
        using var guardian = new ProcessGuardianBuilder().WithChildrenOutliveOwner().Build();
        var processId = StartLongRunning(guardian);

        var exited = await guardian.TerminateProcessAsync(processId, TimeSpan.FromSeconds(5));

        exited.Should().BeTrue();
        WaitUntil(() => !IsProcessRunning(processId)).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task Dispose_WithoutOption_StillTerminatesChildren()
    {
        var guardian = new ProcessGuardian();
        var processId = StartLongRunning(guardian);

        guardian.Dispose();
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        IsProcessRunning(processId).Should().BeFalse();
    }
}
