using System.Diagnostics;
using AwesomeAssertions;
using Xunit;
using static ChildProcessGuard.Tests.TestProcesses;

namespace ChildProcessGuard.Tests;

/// <summary>
/// Tests for <see cref="ProcessGuardianOptions.CloseRequest"/>, the caller-supplied close request of
/// the graceful termination stage.
/// </summary>
public class CloseRequestTests : IDisposable
{
    private ProcessGuardian? _guardian;

    public void Dispose()
    {
        _guardian?.Dispose();
    }

    /// <summary>
    /// A child that runs until its standard input is closed and then exits with code 0, so a zero
    /// exit code shows it left on the close request rather than being killed.
    /// </summary>
    private static ProcessStartInfo StdinBoundChild() => new()
    {
        FileName = TestPlatform.IsWindows ? Path.Combine(Environment.SystemDirectory, "sort.exe") : "/bin/cat",
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    private static bool CloseStandardInput(ManagedProcessInfo info)
    {
        info.Process.StandardInput.Close();
        return true;
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Clone_CopiesCloseRequest()
    {
        Func<ManagedProcessInfo, bool> closeRequest = _ => true;
        var options = new ProcessGuardianOptions { CloseRequest = closeRequest };

        options.Clone().CloseRequest.Should().BeSameAs(closeRequest);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Builder_WithCloseRequest_SetsOption()
    {
        Func<ManagedProcessInfo, bool> closeRequest = _ => true;

        _guardian = new ProcessGuardianBuilder().WithCloseRequest(closeRequest).Build();

        _guardian.Options.CloseRequest.Should().BeSameAs(closeRequest);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Builder_WithOptions_KeepsCloseRequest()
    {
        Func<ManagedProcessInfo, bool> closeRequest = _ => true;

        _guardian = new ProcessGuardianBuilder()
            .WithOptions(new ProcessGuardianOptions { CloseRequest = closeRequest })
            .Build();

        _guardian.Options.CloseRequest.Should().BeSameAs(closeRequest);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Builder_WithCloseRequest_RejectsNull()
    {
        var builder = new ProcessGuardianBuilder();

        builder.Invoking(b => b.WithCloseRequest(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task TerminateProcessAsync_WhenCloseRequestIsDelivered_ProcessExitsGracefully()
    {
        // Arrange
        var calls = 0;
        _guardian = new ProcessGuardian(new ProcessGuardianOptions
        {
            ProcessKillTimeout = TimeSpan.FromSeconds(15),
            CloseRequest = info => { Interlocked.Increment(ref calls); return CloseStandardInput(info); },
        });
        using var process = _guardian.StartProcessWithStartInfo(StdinBoundChild());

        // Act
        var stopwatch = Stopwatch.StartNew();
        var exited = await _guardian.TerminateProcessAsync(process);
        stopwatch.Stop();

        // Assert
        exited.Should().BeTrue();
        calls.Should().Be(1);
        process.WaitForExit(10000).Should().BeTrue();
        process.ExitCode.Should().Be(0, "the child should have exited on end of input, not been killed");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the guardian should stop waiting once the child exits");
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task TerminateProcessAsync_WhenDeliveredRequestIsIgnored_ForcesTerminationAfterTimeout()
    {
        // Arrange - the callback claims delivery but the child never learns of it
        var timeout = TimeSpan.FromMilliseconds(500);
        _guardian = new ProcessGuardian(new ProcessGuardianOptions
        {
            ProcessKillTimeout = timeout,
            ForceKillOnTimeout = true,
            CloseRequest = _ => true,
        });
        using var process = _guardian.StartProcessWithStartInfo(StdinBoundChild());

        // Act
        var stopwatch = Stopwatch.StartNew();
        var exited = await _guardian.TerminateProcessAsync(process);
        stopwatch.Stop();

        // Assert
        exited.Should().BeTrue();
        process.WaitForExit(10000).Should().BeTrue();
        process.ExitCode.Should().NotBe(0, "the child should have been killed");
        stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(timeout - TimeSpan.FromMilliseconds(50), "the guardian should wait for a delivered request before forcing");
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task TerminateProcessAsync_WhenCloseRequestReturnsFalse_UsesBuiltInSequence()
    {
        // Arrange
        var calls = 0;
        _guardian = new ProcessGuardian(new ProcessGuardianOptions
        {
            ProcessKillTimeout = TimeSpan.FromSeconds(5),
            CloseRequest = _ => { Interlocked.Increment(ref calls); return false; },
        });
        using var process = _guardian.StartProcessWithStartInfo(GetLongRunningProcessStartInfo());

        // Act
        var exited = await _guardian.TerminateProcessAsync(process);

        // Assert
        exited.Should().BeTrue();
        calls.Should().Be(1);
        process.WaitForExit(10000).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task TerminateProcessAsync_WhenCloseRequestThrows_ReportsErrorAndStillTerminates()
    {
        // Arrange
        var failure = new InvalidOperationException("close request failed");
        _guardian = new ProcessGuardian(new ProcessGuardianOptions
        {
            ProcessKillTimeout = TimeSpan.FromSeconds(5),
            CloseRequest = _ => throw failure,
        });
        var errors = new List<ProcessErrorEventArgs>();
        _guardian.ProcessError += (_, e) => { lock (errors) errors.Add(e); };
        using var process = _guardian.StartProcessWithStartInfo(GetLongRunningProcessStartInfo());

        // Act
        var exited = await _guardian.TerminateProcessAsync(process);

        // Assert
        exited.Should().BeTrue();
        process.WaitForExit(10000).Should().BeTrue();
        errors.Should().ContainSingle(e => e.Operation == "CloseRequest")
            .Which.Should().Match<ProcessErrorEventArgs>(e => e.Exception == failure && e.ProcessId == process.Id);
    }

    [Fact]
    [Trait("Category", "Process")]
    public void Dispose_UsesCloseRequest()
    {
        // Arrange
        var guardian = new ProcessGuardian(new ProcessGuardianOptions
        {
            ProcessKillTimeout = TimeSpan.FromSeconds(15),
            CloseRequest = CloseStandardInput,
        });
        using var process = guardian.StartProcessWithStartInfo(StdinBoundChild());

        // Act
        guardian.Dispose();

        // Assert
        process.WaitForExit(10000).Should().BeTrue();
        process.ExitCode.Should().Be(0, "disposal should have asked the child to exit through the close request");
    }
}
