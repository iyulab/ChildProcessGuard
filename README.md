# ChildProcessGuard

[![NuGet](https://img.shields.io/nuget/v/ChildProcessGuard.svg)](https://www.nuget.org/packages/ChildProcessGuard)
[![NuGet Downloads](https://img.shields.io/nuget/dt/ChildProcessGuard.svg)](https://www.nuget.org/packages/ChildProcessGuard)
[![Build Status](https://github.com/iyulab/ChildProcessGuard/actions/workflows/dotnet.yml/badge.svg)](https://github.com/iyulab/ChildProcessGuard/actions)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)

A cross-platform .NET library that ensures child processes automatically terminate when the parent process exits unexpectedly.

## Features

- **Cross-Platform Support**: Works on Windows, Linux, and macOS
- **Automatic Cleanup**: Child processes terminate when the parent exits
- **Windows Job Objects**: Uses Job Objects for process management on Windows
- **Unix Process Groups**: Uses process groups on Unix systems
- **Process Tree Termination**: Terminates all descendant processes
- **Async/Await Support**: Full asynchronous API with cancellation tokens
- **Process Monitoring**: Real-time statistics and lifecycle events
- **Batch Processing**: Start multiple processes with concurrency control
- **Thread-Safe**: Supports concurrent operations
- **Graceful Shutdown**: Configurable timeout and fallback mechanisms

## Requirements

- .NET Standard 2.1
- .NET 10.0+
- .NET Framework 4.7.2+ / .NET Core 2.1+ / .NET 5+

## Installation

### Package Manager

```powershell
Install-Package ChildProcessGuard
```

### .NET CLI

```bash
dotnet add package ChildProcessGuard
```

## Quick Start

### Basic Usage

```csharp
using ChildProcessGuard;

// Simple usage with automatic cleanup
using var guardian = new ProcessGuardian();

var process = guardian.StartProcess("notepad.exe");
Console.WriteLine($"Started process with PID: {process.Id}");

// Process will be automatically terminated when guardian is disposed
```

### Advanced Configuration

```csharp
using ChildProcessGuard;

// Configure with builder pattern
using var guardian = ProcessGuardianBuilder.Debug()
    .WithKillTimeout(TimeSpan.FromSeconds(10))
    .WithMaxProcesses(50)
    .WithDetailedLogging(true)
    .WithAutoCleanup(true, TimeSpan.FromMinutes(1))
    .Build();

// Set up event handlers
guardian.ProcessError += (sender, e) =>
    Console.WriteLine($"Error: {e.Operation} - {e.Exception.Message}");

guardian.ProcessLifecycleEvent += (sender, e) =>
    Console.WriteLine($"Event: {e.EventType} - {e.ProcessInfo}");

var process = guardian.StartProcess("myapp.exe", "--verbose");
```

## Usage Examples

### Environment Variables and Working Directory

```csharp
using var guardian = new ProcessGuardian();

var envVars = new Dictionary<string, string>
{
    { "DEBUG", "true" },
    { "CONFIG_PATH", "/etc/myapp/config.json" },
    { "LOG_LEVEL", "verbose" }
};

var process = guardian.StartProcess(
    "myapp.exe",
    "--config config.json",
    workingDirectory: "/path/to/working/dir",
    environmentVariables: envVars
);
```

### Custom ProcessStartInfo

```csharp
using var guardian = new ProcessGuardian();

var startInfo = new ProcessStartInfo
{
    FileName = "powershell.exe",
    Arguments = "-NoProfile -Command \"Get-Process\"",
    UseShellExecute = false,
    RedirectStandardOutput = true,
    CreateNoWindow = true
};

var process = guardian.StartProcessWithStartInfo(startInfo);
string output = process.StandardOutput.ReadToEnd();
```

### Batch Processing

```csharp
using var guardian = ProcessGuardianBuilder.HighPerformance().Build();

// Prepare multiple processes
var processInfos = Enumerable.Range(1, 5)
    .Select(i => new ProcessStartInfo("ping", "127.0.0.1 -n 3"))
    .ToList();

// Start all processes concurrently
var processes = await guardian.StartProcessesBatchAsync(processInfos, maxConcurrency: 3);

// Wait for all to complete
bool allCompleted = await guardian.WaitForAllProcessesAsync(TimeSpan.FromSeconds(30));
Console.WriteLine($"All processes completed: {allCompleted}");
```

### Process Monitoring

```csharp
using var guardian = new ProcessGuardian();

// Start processes
guardian.StartProcess("notepad.exe");
guardian.StartProcess("calc.exe");

// Get statistics
var stats = guardian.GetStatistics();
Console.WriteLine($"Total: {stats.TotalProcesses}, Running: {stats.RunningProcesses}");
Console.WriteLine($"Memory Usage: {stats.TotalMemoryUsage / 1024 / 1024:F1} MB");

// Get detailed process information
var runningProcesses = guardian.GetProcessesByStatus(ProcessStatus.Running);
foreach (var processInfo in runningProcesses)
{
    Console.WriteLine($"Process: {processInfo}");
    Console.WriteLine($"Runtime: {processInfo.GetRuntime():hh\\:mm\\:ss}");
}
```

### Async Operations

```csharp
using var guardian = new ProcessGuardian();
var cts = new CancellationTokenSource();

// Start process asynchronously
var process = await guardian.StartProcessAsync("myapp.exe", cancellationToken: cts.Token);

// Terminate all processes
int terminated = await guardian.KillAllProcessesAsync(TimeSpan.FromSeconds(10));
Console.WriteLine($"Terminated {terminated} processes");

// Selective termination
int killed = await guardian.TerminateProcessesWhere(
    p => p.GetRuntime() > TimeSpan.FromMinutes(5),
    TimeSpan.FromSeconds(5)
);
```

### Cross-Platform Example

```csharp
using var guardian = new ProcessGuardian();

string executable, arguments;

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    executable = "cmd.exe";
    arguments = "/c echo Hello from Windows";
}
else
{
    executable = "/bin/bash";
    arguments = "-c 'echo Hello from Unix'";
}

var process = guardian.StartProcess(executable, arguments);
await process.WaitForExitAsync();
```

## Configuration Options

### ProcessGuardianOptions

```csharp
var options = new ProcessGuardianOptions
{
    ProcessKillTimeout = TimeSpan.FromSeconds(30),      // Graceful termination timeout
    EnableDetailedLogging = false,                      // Verbose logging
    ForceKillOnTimeout = true,                          // Force kill if timeout exceeded
    MaxManagedProcesses = 100,                          // Maximum concurrent processes
    AutoCleanupDisposedProcesses = true,                // Auto cleanup exited processes
    CleanupInterval = TimeSpan.FromMinutes(5),          // Cleanup check interval
    UseProcessGroupsOnUnix = true,                      // Use process groups on Unix
    ThrowOnProcessOperationFailure = false              // Exception handling behavior
};

using var guardian = new ProcessGuardian(options);
```

### Predefined Configurations

```csharp
// High performance configuration
using var guardian = ProcessGuardianBuilder.HighPerformance().Build();

// Debug configuration with detailed logging
using var guardian = ProcessGuardianBuilder.Debug().Build();

// Custom configuration
using var guardian = ProcessGuardianBuilder.Default
    .WithKillTimeout(TimeSpan.FromSeconds(15))
    .WithMaxProcesses(200)
    .WithDetailedLogging(true)
    .Build();
```

## How It Works

### Windows Implementation
- Uses Windows Job Objects with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` flag
- Automatically terminates all child processes when the job is closed
- Kernel-level guarantee for process cleanup

### Unix Implementation (Linux/macOS)
- Creates process groups using `setpgid()` system call
- Uses `SIGTERM` for graceful termination, `SIGKILL` for force termination
- Terminates entire process groups to ensure all descendants are cleaned up

### Cross-Platform Failsafes
- Hooks into `AppDomain.ProcessExit` and `ConsoleCancelKeyPress` events
- Falls back to basic process termination if advanced features fail
- Provides .NET 5+ features for .NET Standard 2.1 compatibility

## Best Practices

1. **Always use `using` statements** or call `Dispose()` explicitly
2. **Configure appropriate timeouts** based on process characteristics
3. **Handle events** for production applications to track errors
4. **Use builder pattern** for complex configurations
5. **Monitor statistics** in long-running applications
6. **Test cross-platform behavior** when targeting multiple operating systems

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## Support

For issues, questions, or suggestions, please open an issue on [GitHub](https://github.com/iyulab/ChildProcessGuard/issues).
