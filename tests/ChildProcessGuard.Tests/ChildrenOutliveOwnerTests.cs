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
    // The Process objects are kept, not just their IDs: while one is alive its handle stops the operating
    // system from giving the ID to another process, and Windows reuses IDs fast. Looking a process up by ID
    // some time after it ended can find an unrelated process that was given the same ID.
    private readonly List<Process> _started = new();

    public void Dispose()
    {
        foreach (var process in _started)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch (Exception)
            {
                // Already gone
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private Process StartLongRunning(ProcessGuardian guardian)
    {
        var process = guardian.StartProcess(GetLongRunningExecutable(), GetLongRunningArguments());
        _started.Add(process);
        return process;
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
        var process = StartLongRunning(guardian);
        guardian.ManagedProcessCount.Should().Be(1);

        guardian.Dispose();
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        guardian.IsDisposed.Should().BeTrue();
        guardian.ManagedProcessCount.Should().Be(0);
        process.HasExited.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task DisposeAsync_LeavesChildrenRunning()
    {
        var guardian = new ProcessGuardianBuilder().WithChildrenOutliveOwner().Build();
        var process = StartLongRunning(guardian);

        await guardian.DisposeAsync();
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        guardian.ManagedProcessCount.Should().Be(0);
        process.HasExited.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task KillAllProcessesAsync_StillTerminatesChildren()
    {
        using var guardian = new ProcessGuardianBuilder().WithChildrenOutliveOwner().Build();
        var process = StartLongRunning(guardian);

        var terminated = await guardian.KillAllProcessesAsync(TimeSpan.FromSeconds(5));

        terminated.Should().Be(1);
        WaitUntil(() => process.HasExited).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task TerminateProcessAsync_StillTerminatesChild()
    {
        using var guardian = new ProcessGuardianBuilder().WithChildrenOutliveOwner().Build();
        var process = StartLongRunning(guardian);

        var exited = await guardian.TerminateProcessAsync(process.Id, TimeSpan.FromSeconds(5));

        exited.Should().BeTrue();
        WaitUntil(() => process.HasExited).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task Dispose_WithoutOption_StillTerminatesChildren()
    {
        var guardian = new ProcessGuardian();
        var process = StartLongRunning(guardian);

        guardian.Dispose();
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        process.HasExited.Should().BeTrue();
    }
}
