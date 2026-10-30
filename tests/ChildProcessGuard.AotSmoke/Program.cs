using System.Diagnostics;
using System.Runtime.InteropServices;
using ChildProcessGuard;

// Native AOT smoke test. The executable plays three roles:
//   (no arguments)      the test: starts a child that has a child of its own, terminates the tree, checks it is gone
//   --child             the child: starts a long-running grandchild, reports its pid, then idles
//   --child --stubborn  the same, but ignores SIGTERM so that the forced stage has to finish the job
if (args.Length > 0 && args[0] == "--child")
    return RunChild(stubborn: args.Length > 1 && args[1] == "--stubborn");

foreach (var stubborn in new[] { false, true })
{
    var result = await RunScenarioAsync(stubborn);
    if (result != 0)
        return result;
}

Console.WriteLine("AOT smoke test passed");
return 0;

static int RunChild(bool stubborn)
{
    PosixSignalRegistration? ignoreTerm = null;
    if (stubborn)
    {
        try
        {
            ignoreTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => context.Cancel = true);
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    using var grandchild = Process.Start(new ProcessStartInfo
    {
        FileName = OperatingSystem.IsWindows() ? "ping" : "/bin/sleep",
        Arguments = OperatingSystem.IsWindows() ? "localhost -n 60" : "60",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
    });

    Console.WriteLine(grandchild!.Id);
    Thread.Sleep(Timeout.Infinite);
    GC.KeepAlive(ignoreTerm);
    return 0;
}

static async Task<int> RunScenarioAsync(bool stubborn)
{
    var name = stubborn ? "stubborn child" : "child";
    var killTimeout = TimeSpan.FromSeconds(2);
    using var guardian = new ProcessGuardian(new ProcessGuardianOptions { ProcessKillTimeout = killTimeout });

    var child = guardian.StartProcessWithStartInfo(new ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        Arguments = stubborn ? "--child --stubborn" : "--child",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
    });

    var reported = child.StandardOutput.ReadLineAsync();
    if (await Task.WhenAny(reported, Task.Delay(TimeSpan.FromSeconds(30))) != reported
        || !int.TryParse(await reported, out var grandchildId))
    {
        Console.Error.WriteLine($"FAIL ({name}): the child did not report its own child");
        return 1;
    }

    if (guardian.ManagedProcessCount != 1)
    {
        Console.Error.WriteLine($"FAIL ({name}): expected 1 managed process, found {guardian.ManagedProcessCount}");
        return 1;
    }

    var stopwatch = Stopwatch.StartNew();
    var terminated = await guardian.KillAllProcessesAsync();
    stopwatch.Stop();

    if (terminated != 1)
    {
        Console.Error.WriteLine($"FAIL ({name}): KillAllProcessesAsync terminated {terminated} processes, expected 1");
        return 1;
    }

    if (!WaitUntilGone(child.Id) || !WaitUntilGone(grandchildId))
    {
        Console.Error.WriteLine($"FAIL ({name}): the process tree is still running after termination");
        return 1;
    }

    // The forced stage has to wait out the timeout before it acts, so a stubborn child cannot be
    // gone sooner on Unix; anywhere else the whole thing must finish within the timeout plus slack.
    if (stopwatch.Elapsed > killTimeout + TimeSpan.FromSeconds(10))
    {
        Console.Error.WriteLine($"FAIL ({name}): termination took {stopwatch.Elapsed}");
        return 1;
    }

    Console.WriteLine($"ok ({name}): tree terminated in {stopwatch.ElapsedMilliseconds} ms");
    return 0;
}

static bool WaitUntilGone(int processId)
{
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
    while (DateTime.UtcNow < deadline)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
                return true;
        }
        catch (ArgumentException)
        {
            return true;
        }

        Thread.Sleep(50);
    }

    return false;
}
