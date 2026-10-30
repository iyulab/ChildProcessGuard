using System.Collections.Concurrent;
using System.Diagnostics;
using AwesomeAssertions;
using Xunit;
using static ChildProcessGuard.Tests.TestProcesses;

namespace ChildProcessGuard.Tests;

/// <summary>
/// Concurrency and thread-safety tests
/// </summary>
public class ConcurrencyTests : IDisposable
{
    private ProcessGuardian? _guardian;

    public void Dispose()
    {
        _guardian?.Dispose();
    }

    [Fact]
    public async Task StartMultipleProcessesConcurrently_ShouldBeThreadSafe()
    {
        // Arrange
        var options = new ProcessGuardianOptions
        {
            MaxManagedProcesses = 50
        };
        _guardian = new ProcessGuardian(options);
        var executable = GetLongRunningExecutable();

        // Act
        var tasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => _guardian.StartProcess(executable, GetLongRunningArguments()), TestContext.Current.CancellationToken))
            .ToArray();

        var processes = await Task.WhenAll(tasks);

        // Assert - the children are still running, so every one of them must be tracked
        processes.Should().HaveCount(20);
        processes.Should().OnlyContain(p => p != null);
        _guardian.ManagedProcessCount.Should().Be(20);

        // All processes should have unique IDs
        var processIds = processes.Select(p => p.Id).ToList();
        processIds.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task StartAndKillConcurrently_ShouldBeThreadSafe()
    {
        // Arrange
        _guardian = new ProcessGuardian();
        var executable = GetLongRunningExecutable();

        // Act - Start processes
        var startTasks = Enumerable.Range(0, 10)
            .Select(_ => _guardian.StartProcessAsync(executable, GetLongRunningArguments()))
            .ToArray();

        await Task.WhenAll(startTasks);

        // Act - Kill processes concurrently
        var killTasks = Enumerable.Range(0, 5)
            .Select(_ => Task.Run(() => _guardian.KillAllProcessesAsync(TimeSpan.FromSeconds(2)), TestContext.Current.CancellationToken))
            .ToArray();

        var killResults = await Task.WhenAll(killTasks);

        // Assert - Should not throw, and eventually all processes killed
        _guardian.ManagedProcessCount.Should().Be(0);
    }

    [Fact]
    public async Task GetStatisticsWhileAddingProcesses_ShouldNotThrow()
    {
        // Arrange
        _guardian = new ProcessGuardian(new ProcessGuardianOptions
        {
            MaxManagedProcesses = 100
        });
        var executable = GetTestExecutable();

        var errors = new ConcurrentBag<Exception>();

        // Act - Continuously get statistics while adding processes
        var statsTask = Task.Run(async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                try
                {
                    var stats = _guardian.GetStatistics();
                    await Task.Delay(10, TestContext.Current.CancellationToken);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }
        }, TestContext.Current.CancellationToken);

        var startTask = Task.Run(async () =>
        {
            for (int i = 0; i < 30; i++)
            {
                try
                {
                    _guardian.StartProcess(executable, GetTestArguments());
                    await Task.Delay(50, TestContext.Current.CancellationToken);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(statsTask, startTask);

        // Assert
        errors.Should().BeEmpty("Operations should be thread-safe");
    }

    [Fact]
    public async Task ConcurrentDispose_ShouldBeIdempotent()
    {
        // Arrange
        _guardian = new ProcessGuardian();
        _guardian.StartProcess(GetTestExecutable(), GetTestArguments());

        // Act - Dispose from multiple threads
        var disposeTasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() =>
            {
                try
                {
                    _guardian.Dispose();
                }
                catch (Exception)
                {
                    // Should not throw
                }
            }, TestContext.Current.CancellationToken))
            .ToArray();

        await Task.WhenAll(disposeTasks);

        // Assert
        _guardian.IsDisposed.Should().BeTrue();
        _guardian.ManagedProcessCount.Should().Be(0);
    }

    [Fact]
    public async Task StartProcessAsync_WithCancellation_ShouldRespectToken()
    {
        // Arrange
        _guardian = new ProcessGuardian();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        Func<Task> act = async () =>
            await _guardian.StartProcessAsync(GetTestExecutable(), GetTestArguments(), cancellationToken: cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task StartProcess_ConcurrentStarts_RespectMaxManagedProcesses()
    {
        // Arrange - the limit is checked before a child starts, and starting takes long enough that
        // every concurrent caller used to pass the check before any child was tracked
        _guardian = new ProcessGuardian(new ProcessGuardianOptions { MaxManagedProcesses = 2 });
        using var go = new ManualResetEventSlim();

        // Act
        var attempts = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                go.Wait(TestContext.Current.CancellationToken);
                try
                {
                    return (Process?)_guardian.StartProcessWithStartInfo(GetLongRunningProcessStartInfo());
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }, TestContext.Current.CancellationToken))
            .ToArray();
        go.Set();
        var results = await Task.WhenAll(attempts);

        // Assert
        results.Count(p => p != null).Should().Be(2);
        _guardian.ManagedProcessCount.Should().Be(2);
    }

    [Fact]
    [Trait("Category", "Process")]
    public void StartProcess_FailedStart_ReleasesItsSlot()
    {
        // Arrange
        _guardian = new ProcessGuardian(new ProcessGuardianOptions { MaxManagedProcesses = 1 });
        var missing = new ProcessStartInfo { FileName = "child-process-guard-no-such-program", UseShellExecute = false };

        // Act - failed starts must not use up the only slot
        for (var i = 0; i < 3; i++)
        {
            _guardian.Invoking(g => g.StartProcessWithStartInfo(missing)).Should().Throw<Exception>()
                .Which.Should().NotBeOfType<InvalidOperationException>();
        }

        var process = _guardian.StartProcessWithStartInfo(GetLongRunningProcessStartInfo());

        // Assert
        _guardian.ManagedProcessCount.Should().Be(1);
        process.HasExited.Should().BeFalse();
    }

    [Fact]
    public async Task RemoveProcess_DuringEnumeration_ShouldNotCorruptCollection()
    {
        // Arrange
        _guardian = new ProcessGuardian();
        var executable = GetLongRunningExecutable();

        for (int i = 0; i < 10; i++)
        {
            _guardian.StartProcess(executable, GetLongRunningArguments());
        }

        var errors = new ConcurrentBag<Exception>();

        // Act - Enumerate while removing
        var enumerateTask = Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                try
                {
                    var processes = _guardian.GetManagedProcesses();
                    foreach (var p in processes)
                    {
                        _ = p.ProcessName; // Access property
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }
        }, TestContext.Current.CancellationToken);

        var removeTask = Task.Run(() =>
        {
            var processes = _guardian.GetManagedProcesses().ToList();
            foreach (var processInfo in processes.Take(5))
            {
                try
                {
                    _guardian.RemoveProcess(processInfo.Process);
                    Thread.Sleep(10);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(enumerateTask, removeTask);

        // Assert - Should handle concurrent modification gracefully
        errors.Should().BeEmpty();
    }

}
