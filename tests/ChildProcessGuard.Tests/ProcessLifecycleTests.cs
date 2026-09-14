using System.ComponentModel;
using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace ChildProcessGuard.Tests;

/// <summary>
/// Tests for the managed-process table lifecycle: registration, pid reuse, exit reaping,
/// removal, and ownership of the returned <see cref="Process"/> instances.
/// Tests tagged Category=Process start real child processes and are excluded from the CI filter.
/// </summary>
public class ProcessLifecycleTests : IDisposable
{
    private ProcessGuardian? _guardian;

    public void Dispose()
    {
        _guardian?.Dispose();
    }

    private static ProcessGuardianOptions NoTimerOptions() => new()
    {
        AutoCleanupDisposedProcesses = false,
    };

    /// <summary>
    /// Builds a managed entry for the current process. Disposing the <see cref="Process"/> makes the
    /// entry read as exited, which stands in for a child that exited and whose pid the OS may reuse.
    /// A live entry for the current process must be removed before the guardian is disposed,
    /// otherwise disposal terminates the test host.
    /// </summary>
    private static ManagedProcessInfo CurrentProcessEntry(out Process process)
    {
        process = Process.GetCurrentProcess();
        return new ManagedProcessInfo(process, "self", string.Empty);
    }

    private static ProcessStartInfo ShortLivedChild() => OperatingSystem.IsWindows()
        ? new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true }
        : new ProcessStartInfo("/bin/sh", "-c \"exit 0\"") { UseShellExecute = false };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void ManagedProcessInfo_Id_RemainsReadableAfterProcessIsDisposed()
    {
        var info = CurrentProcessEntry(out var process);
        var expectedId = process.Id;

        process.Dispose();

        info.Id.Should().Be(expectedId);
        info.HasExited.Should().BeTrue();
        info.Invoking(i => i.ToString()).Should().NotThrow();
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Track_WhenPidIsHeldByExitedEntry_ReplacesStaleEntry()
    {
        _guardian = new ProcessGuardian(NoTimerOptions());
        var stale = CurrentProcessEntry(out var staleProcess);
        staleProcess.Dispose();
        _guardian.Track(stale);

        var fresh = CurrentProcessEntry(out _);
        _guardian.Track(fresh);

        try
        {
            _guardian.ManagedProcessCount.Should().Be(1);
            _guardian.GetProcessInfo(fresh.Id).Should().BeSameAs(fresh);
            stale.IsManaged.Should().BeFalse();
            fresh.IsManaged.Should().BeTrue();
        }
        finally
        {
            _guardian.RemoveProcess(fresh.Id);
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void RemoveProcess_WithDisposedProcess_RemovesEntry()
    {
        _guardian = new ProcessGuardian(NoTimerOptions());
        var info = CurrentProcessEntry(out var process);
        _guardian.Track(info);
        process.Dispose(); // entry now reads as exited, so guardian disposal will not touch it

        var removed = _guardian.RemoveProcess(process);

        removed.Should().BeTrue();
        _guardian.ManagedProcessCount.Should().Be(0);
        info.IsManaged.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void RemoveProcess_ByProcessId_RemovesEntry()
    {
        _guardian = new ProcessGuardian(NoTimerOptions());
        var info = CurrentProcessEntry(out _);
        _guardian.Track(info);

        var removedFirst = _guardian.RemoveProcess(info.Id);
        var removedAgain = _guardian.RemoveProcess(info.Id);

        removedFirst.Should().BeTrue();
        removedAgain.Should().BeFalse();
        _guardian.ManagedProcessCount.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void RemoveProcess_WithUnmanagedProcess_ReturnsFalse()
    {
        _guardian = new ProcessGuardian(NoTimerOptions());
        using var process = Process.GetCurrentProcess();

        _guardian.RemoveProcess(process).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void StartProcess_WhenExecutableIsMissing_ThrowsStartFailure()
    {
        _guardian = new ProcessGuardian(NoTimerOptions());

        var act = () => _guardian.StartProcess("definitely-not-an-executable-" + Guid.NewGuid());

        act.Should().Throw<Win32Exception>();
        _guardian.ManagedProcessCount.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task StartProcess_ExitedChild_IsRemovedFromManagedList()
    {
        _guardian = new ProcessGuardian(NoTimerOptions());

        using var process = _guardian.StartProcessWithStartInfo(ShortLivedChild());
        process.WaitForExit();
        await WaitUntilAsync(() => _guardian.ManagedProcessCount == 0, TimeSpan.FromSeconds(5));

        _guardian.ManagedProcessCount.Should().Be(0);
        _guardian.GetProcessInfo(process.Id).Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task StartProcess_ExitedChildren_DoNotCountTowardMaxManagedProcesses()
    {
        _guardian = new ProcessGuardian(new ProcessGuardianOptions
        {
            AutoCleanupDisposedProcesses = false,
            MaxManagedProcesses = 1,
        });

        for (var i = 0; i < 3; i++)
        {
            using var process = _guardian.StartProcessWithStartInfo(ShortLivedChild());
            process.WaitForExit();
            await WaitUntilAsync(() => _guardian.ManagedProcessCount == 0, TimeSpan.FromSeconds(5));
        }

        _guardian.ManagedProcessCount.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task StartProcess_ReturnedProcess_IsNotDisposedByGuardian()
    {
        _guardian = new ProcessGuardian(new ProcessGuardianOptions
        {
            AutoCleanupDisposedProcesses = true,
            CleanupInterval = TimeSpan.FromMilliseconds(50),
        });

        using var process = _guardian.StartProcessWithStartInfo(ShortLivedChild());
        process.WaitForExit();
        await WaitUntilAsync(() => _guardian.ManagedProcessCount == 0, TimeSpan.FromSeconds(5));
        await Task.Delay(200); // let at least one cleanup sweep run after the exit

        process.Invoking(p => p.ExitCode).Should().NotThrow();
        process.ExitCode.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Process")]
    public void StartProcess_RepeatedShortLivedChildren_DisposedByCaller_DoesNotThrow()
    {
        _guardian = new ProcessGuardian();

        for (var batch = 0; batch < 3; batch++)
        {
            var processes = new List<Process>();
            for (var i = 0; i < 10; i++)
            {
                processes.Add(_guardian.StartProcessWithStartInfo(ShortLivedChild()));
            }

            foreach (var process in processes)
            {
                process.WaitForExit();
                process.Dispose();
            }
        }
    }

    [Fact]
    [Trait("Category", "Process")]
    public void RemoveProcess_AfterCallerDisposedTheProcess_Succeeds()
    {
        _guardian = new ProcessGuardian(NoTimerOptions());

        var process = _guardian.StartProcessWithStartInfo(ShortLivedChild());
        process.WaitForExit();
        process.Dispose();

        // The entry may already have been reaped by the exit notification; either way, the call
        // must not throw on a disposed instance.
        _guardian.Invoking(g => g.RemoveProcess(process)).Should().NotThrow();
        _guardian.ManagedProcessCount.Should().Be(0);
    }
}
