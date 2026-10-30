using System.Diagnostics;
using ChildProcessGuard;

// Stands in for an application that owns a guardian: it starts a child that has a child of its own,
// reports the child's process ID in a file, and then either waits to be ended from outside or exits
// once told to. The test project ends it in different ways and checks what happens to the children.
//
//   ChildProcessGuard.OwnerHarness <pid-file> <wait|exit> [--sigterm]
//
// "--sigterm" sets ProcessGuardianOptions.CleanupOnSigterm.
// "exit" returns from Main once "<pid-file>.go" exists, so the process ends the ordinary way.
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: ChildProcessGuard.OwnerHarness <pid-file> <wait|exit> [--sigterm]");
    return 2;
}

var pidFile = args[0];
var mode = args[1];

// Not disposed on purpose: the guardian has to act when this process ends.
var guardian = new ProcessGuardian(new ProcessGuardianOptions { CleanupOnSigterm = args.Contains("--sigterm") });

var startInfo = OperatingSystem.IsWindows()
    ? new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 >nul")
    : new ProcessStartInfo("/bin/sh", "-c \"sleep 60 & wait\"");
startInfo.UseShellExecute = false;
startInfo.CreateNoWindow = true;

var child = guardian.StartProcessWithStartInfo(startInfo);

// Written under another name first so the reader never sees a partial file
var temp = pidFile + ".tmp";
File.WriteAllText(temp, child.Id.ToString());
File.Move(temp, pidFile);

if (mode == "exit")
{
    var deadline = DateTime.UtcNow.AddSeconds(60);
    while (!File.Exists(pidFile + ".go") && DateTime.UtcNow < deadline)
        Thread.Sleep(20);

    GC.KeepAlive(guardian);
    return 0;
}

Thread.Sleep(Timeout.Infinite);
GC.KeepAlive(guardian);
return 0;
