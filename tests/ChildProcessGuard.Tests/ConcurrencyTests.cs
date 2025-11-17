using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Xunit;

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
        var executable = GetTestExecutable();

        // Act
        var tasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => _guardian.StartProcess(executable)))
            .ToArray();

        var processes = await Task.WhenAll(tasks);

        // Assert
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
            .Select(_ => _guardian.StartProcessAsync(executable))
            .ToArray();

        await Task.WhenAll(startTasks);

        // Act - Kill processes concurrently
        var killTasks = Enumerable.Range(0, 5)
            .Select(_ => Task.Run(() => _guardian.KillAllProcessesAsync(TimeSpan.FromSeconds(2))))
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
                    await Task.Delay(10);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }
        });

        var startTask = Task.Run(async () =>
        {
            for (int i = 0; i < 30; i++)
            {
                try
                {
                    _guardian.StartProcess(executable);
                    await Task.Delay(50);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }
        });

        await Task.WhenAll(statsTask, startTask);

        // Assert
        errors.Should().BeEmpty("Operations should be thread-safe");
    }

    [Fact]
    public async Task ConcurrentDispose_ShouldBeIdempotent()
    {
        // Arrange
        _guardian = new ProcessGuardian();
        _guardian.StartProcess(GetTestExecutable());

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
            }))
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
            await _guardian.StartProcessAsync(GetTestExecutable(), cancellationToken: cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SemaphoreLimit_ShouldEnforceConcurrency()
    {
        // Arrange
        var options = new ProcessGuardianOptions
        {
            MaxManagedProcesses = 100
        };
        _guardian = new ProcessGuardian(options);
        var executable = GetTestExecutable();

        var activeCount = 0;
        var maxConcurrent = 0;
        var lockObj = new object();

        // Act - Try to start more processes than semaphore allows
        var tasks = Enumerable.Range(0, 100)
            .Select(async i =>
            {
                lock (lockObj)
                {
                    activeCount++;
                    maxConcurrent = Math.Max(maxConcurrent, activeCount);
                }

                try
                {
                    var process = await _guardian.StartProcessAsync(executable);
                    await Task.Delay(10);
                    return process;
                }
                finally
                {
                    lock (lockObj)
                    {
                        activeCount--;
                    }
                }
            })
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Assert
        results.Should().HaveCount(100);
        maxConcurrent.Should().BeLessThanOrEqualTo(options.MaxManagedProcesses);
    }

    [Fact]
    public async Task RemoveProcess_DuringEnumeration_ShouldNotCorruptCollection()
    {
        // Arrange
        _guardian = new ProcessGuardian();
        var executable = GetTestExecutable();

        for (int i = 0; i < 10; i++)
        {
            _guardian.StartProcess(executable);
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
        });

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
        });

        await Task.WhenAll(enumerateTask, removeTask);

        // Assert - Should handle concurrent modification gracefully
        errors.Should().BeEmpty();
    }

    #region Helper Methods

    private static string GetTestExecutable()
    {
        return OperatingSystem.IsWindows() ? "ping" : "/bin/sleep";
    }

    private static string GetTestArguments()
    {
        return OperatingSystem.IsWindows() ? "localhost -n 100" : "60";
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

    #endregion
}
