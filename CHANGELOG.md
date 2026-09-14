# Changelog

All notable changes to this project are documented in this file.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to [Semantic Versioning](https://semver.org/).

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
