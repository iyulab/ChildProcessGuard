# Changelog

All notable changes to this project are documented in this file.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to [Semantic Versioning](https://semver.org/).

## [1.4.0] - 2026-10-04

### Added
- `ProcessGuardianOptions.CloseRequest` and `ProcessGuardianBuilder.WithCloseRequest` let the caller supply the close request of the graceful termination stage — for example closing a console child's standard input or sending it a quit command. When the callback returns `true`, the guardian waits up to `ProcessKillTimeout` and then forces the tree as before; when it returns `false` or throws, the built-in request (`SIGTERM` on Unix, `CloseMainWindow` on Windows) is used. It applies to `TerminateProcessAsync`, `TerminateProcessesWhere`, `KillAllProcesses` and `Dispose`.

### Changed
- Terminating many processes at once (`KillAllProcesses`, `Dispose`, `TerminateProcessesWhere`) reads the process table about twice per batch instead of once per process: concurrent process-tree walks now share a snapshot taken after each of them started. On Windows with 50 console children this roughly halved the time to terminate them.

### Removed
- The `net8.0` target. .NET 8 reaches end of support on 2026-11-10, and the build had no code of its own: projects targeting .NET 8 or .NET 9 now use the `netstandard2.1` build, which exposes the same API.

## [1.3.1] - 2026-10-04

### Fixed
- macOS: the graceful stage (`SIGTERM`) reached only the child itself, and so did the forced stage in the .NET Standard builds, because descendants were found by reading `/proc`, which macOS does not have. Descendants are now enumerated through `libproc` on macOS.
- Unix: descendant enumeration re-read the whole process table once per descendant; it now walks a single snapshot.
- Unix: termination signals could be sent to a child that had already exited but whose exit had not been observed yet, or to its pid after it had been reaped, and in the .NET 8 and .NET 10 builds the forced stage relied on `Process.Kill(entireProcessTree: true)`, which stops and kills whatever its own process scan reports as children. On macOS this left the calling environment unresponsive when many children were started and terminated concurrently. Both stages now use the library's own process-tree walk on every target and signal only a process that the process table shows running as a child of the caller, and its descendants.
- Windows: forcing a process tree to terminate took seconds per process on a machine with many processes in the .NET 8 and .NET 10 builds, because the runtime's `Process.Kill(entireProcessTree: true)` opens every process on the machine to find descendants. The tree is now taken from one ToolHelp32 snapshot on every target.
- Windows (.NET Standard builds): a recorded parent process ID is kept after the parent exits and can be reused, so an unrelated process could be treated as a descendant and killed. A child is now only accepted if it was created after its parent.

## [1.3.0] - 2026-10-03

### Added
- `ProcessGuardian.TerminateProcessAsync(int processId, TimeSpan? timeout)` and `TerminateProcessAsync(Process process, TimeSpan? timeout)` terminate a single managed process with the same two-stage sequence as `KillAllProcessesAsync` (close request, wait, then forced tree kill). They return whether the process has exited, and `false` for a process that is not being managed.

### Changed
- `TerminateProcessesWhere` now uses the two-stage sequence instead of killing the process tree immediately, terminates the matched processes concurrently instead of one at a time, and honors `ForceKillOnTimeout`. The `timeout` argument is the wait after the close request, and the return value counts the matched processes that have exited.

### Fixed
- `Dispose`, `KillAllProcesses` and other blocking calls could deadlock when called on a thread with a single-threaded synchronization context (a WPF or Windows Forms UI thread), because the library's asynchronous continuations resumed on the caller's context. The library no longer captures the caller's synchronization context.

### Deprecated
- `ProcessStatus.Exited` and `ProcessStatistics.ExitedProcesses`. Processes are removed from management as soon as they exit, so the filter rarely matches anything and the count is almost always 0. Observe exits through the `ProcessLifecycleEvent` event instead.

## [1.2.1] - 2026-10-03

### Fixed
- The package now includes the `LICENSE` file at its root alongside the `MIT` license expression, so the copyright and permission notice that the MIT license requires travels with every copy of the package.
- `KillAllProcessesAsync`, `KillAllProcesses` and `Dispose` now terminate managed processes concurrently. The close request and the process-tree kill are blocking calls that ran before the first asynchronous wait, so processes were terminated one after another and disposal time grew with the number of children (several seconds per child on a busy machine).
- `ManagedProcessInfo.GetRuntime()` (and the runtime shown by `ToString()`) was off by the machine's UTC offset for exited processes, because it subtracted the UTC start time from the local exit time.

## [1.2.0] - 2026-09-14

### Fixed
- **Unix: forced termination no longer kills the calling application.** The tree kill sent `SIGKILL` to the process *group* returned by `getpgid(child)`, but children started through `System.Diagnostics.Process` share the parent's group (no `setpgid` is ever applied), so `Dispose`, `KillAllProcesses` and `TerminateProcessesWhere` terminated the parent and its siblings along with the child. Signals are now sent to the child and its enumerated descendants individually; the `net8.0` and `net10.0` builds delegate the forced kill to `Process.Kill(entireProcessTree: true)`.
- Unix: graceful termination now actually sends `SIGTERM` to the process tree before escalating to `SIGKILL`; previously only `CloseMainWindow` was attempted, which is a no-op on Unix.
- `StartProcessWithStartInfo` threw `InvalidOperationException: Process with ID N is already being managed` when the OS reused the pid of an exited child before the periodic cleanup had run (up to `CleanupInterval`, 5 minutes by default). The new child had already started and was left running unmanaged and outside the job object. Exited processes now leave the managed table as soon as their `Exited` event fires, and a pid collision on registration replaces the stale entry instead of throwing.
- Exited processes no longer count toward `MaxManagedProcesses`.
- A child that exits before event notification is enabled no longer misses its `Exited` handling.
- A process that started but could not be registered is terminated instead of being left orphaned, and a failed `Process.Start()` reports the original exception instead of `No process is associated with this object`.
- `ManagedProcessInfo.Id` and `ManagedProcessInfo.ToString()` no longer throw after the underlying `Process` has been disposed.
- `RemoveProcess(Process)` works on a disposed `Process` instance (matched by identity rather than by reading `Process.Id`).
- Termination no longer waits the full `ProcessKillTimeout` for a process that could not be sent a close request (console processes, and every process on Unix, where `CloseMainWindow` returns `false`); it proceeds straight to forced termination. Disposing a guardian with running console children previously took the whole timeout (30 seconds by default).

### Added
- `RemoveProcess(int processId)` overload.
- `net8.0` target, so .NET 8 and .NET 9 consumers get the runtime's own process-tree termination instead of the .NET Standard polyfill.
- The test suite now also runs on .NET Framework 4.8 (Windows), exercising the `netstandard2.0` build.

### Known limitations
- The .NET Standard builds enumerate descendants through `/proc`, so on macOS they can only terminate the child itself, not its descendants. Consumers on .NET 8 or later are not affected.

### Deprecated
- `ManagedProcessInfo.ProcessGroupId` — always `null`; process groups are not used (`UseProcessGroupsOnUnix` was already marked obsolete).

### Changed
- The guardian no longer disposes the `Process` instances returned by the `Start*` methods; the caller owns them. Previously the periodic cleanup disposed them after `CleanupInterval`, which made a later `process.ExitCode` read throw.
- `AutoCleanupDisposedProcesses` / `CleanupInterval` now describe a fallback sweep; exited processes are normally removed immediately. As a result `GetProcessInfo` returns `null` for a process once it has exited (the `ProcessExited` event still carries its `ManagedProcessInfo`), and `ProcessStatistics.ExitedProcesses` / `GetProcessesByStatus(ProcessStatus.Exited)` only report entries not yet swept.

## [1.1.1] - 2026-03-23

### Changed
- Improved logging: `LogAction` delegate for routing log output, `Console.WriteLine` only when detailed logging is enabled.
- README updates.

## [1.1.0] - 2026-03-23

### Added
- `netstandard2.0` target (thanks to an external contribution).

### Changed
- `Microsoft.Bcl.AsyncInterfaces` pinned to 8.0.0 for the `netstandard2.0` target so .NET Framework 4.8 consumers are not forced onto a newer dependency graph.

[1.2.0]: https://github.com/iyulab/ChildProcessGuard/compare/v1.1.1...v1.2.0
[1.1.1]: https://github.com/iyulab/ChildProcessGuard/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/iyulab/ChildProcessGuard/compare/v1.0.4...v1.1.0
