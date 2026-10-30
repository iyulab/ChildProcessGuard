using System.Diagnostics;
using System.Runtime.InteropServices;
using ChildProcessGuard;

// Native AOT smoke test. The executable plays several roles:
//   (no arguments)             the test: runs the scenarios below
//   --child [--stubborn]       a child that starts a long-running grandchild, reports its pid, then idles;
//                              --stubborn ignores SIGTERM so that the forced stage has to finish the job
//   --sleep                    a process that does nothing until it is terminated
//   --owner <file> [--outlive] a short-lived owner: starts a sleeper through a guardian, writes the
//                              sleeper's pid to <file> and exits without disposing the guardian
if (args.Length > 0)
{
    switch (args[0])
    {
        case "--child":
            return RunChild(stubborn: args.Length > 1 && args[1] == "--stubborn");
        case "--sleep":
            Thread.Sleep(Timeout.Infinite);
            return 0;
        case "--owner":
            return RunOwner(args[1], outlive: args.Length > 2 && args[2] == "--outlive");
    }
}

foreach (var stubborn in new[] { false, true })
{
    var result = await RunScenarioAsync(stubborn);
    if (result != 0)
        return result;
}

foreach (var outlive in new[] { false, true })
{
    var result = RunOwnerScenario(outlive);
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

static int RunOwner(string pidFile, bool outlive)
{
    // Not disposed on purpose: the guardian has to act when the process exits.
    var guardian = new ProcessGuardian(new ProcessGuardianOptions { ChildrenOutliveOwner = outlive });
    var sleeper = guardian.StartProcessWithStartInfo(SleeperStartInfo());
    File.WriteAllText(pidFile, sleeper.Id.ToString());
    return 0;
}

static ProcessStartInfo SleeperStartInfo() => new()
{
    FileName = Environment.ProcessPath!,
    Arguments = "--sleep",
    UseShellExecute = false,
    CreateNoWindow = true,
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
};

// The owner starts a child through a guardian and exits. A default guardian ends the child with its
// owner; one created with ChildrenOutliveOwner leaves it running.
static int RunOwnerScenario(bool outlive)
{
    var name = outlive ? "owner exit, children outlive owner" : "owner exit, default";
    var pidFile = Path.Combine(Path.GetTempPath(), $"cpg-aot-smoke-{Guid.NewGuid():N}.pid");
    try
    {
        using var owner = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            Arguments = outlive ? $"--owner \"{pidFile}\" --outlive" : $"--owner \"{pidFile}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        if (!owner.WaitForExit(TimeSpan.FromSeconds(30)) || !File.Exists(pidFile)
            || !int.TryParse(File.ReadAllText(pidFile), out var sleeperId))
        {
            Console.Error.WriteLine($"FAIL ({name}): the owner did not report its child and exit");
            return 1;
        }

        if (outlive)
        {
            // Give a wrongly delivered termination time to show before judging the child alive.
            Thread.Sleep(TimeSpan.FromSeconds(2));
            var survived = IsRunning(sleeperId);
            if (survived)
                Process.GetProcessById(sleeperId).Kill();

            if (!survived || !WaitUntilGone(sleeperId))
            {
                Console.Error.WriteLine($"FAIL ({name}): the child did not outlive its owner");
                return 1;
            }
        }
        else if (!WaitUntilGone(sleeperId))
        {
            Console.Error.WriteLine($"FAIL ({name}): the child is still running after its owner exited");
            return 1;
        }

        Console.WriteLine($"ok ({name})");
        return 0;
    }
    finally
    {
        File.Delete(pidFile);
    }
}

static bool IsRunning(int processId)
{
    try
    {
        using var process = Process.GetProcessById(processId);
        return !process.HasExited;
    }
    catch (ArgumentException)
    {
        return false;
    }
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
