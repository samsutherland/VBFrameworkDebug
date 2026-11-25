# VB.NET Framework Debugger for VS Code

Debug local VB.NET console and WinForms applications targeting .NET Framework 4.8 from VS Code on x64 Windows. Includes x86/x64 desktop CLR workers; Visual Studio and the C# extension are not runtime dependencies. Version 0.2.0 is a preview, not Visual Studio debugger parity.

**WPF:** reported working in manual use with a .NET Framework application, using the same launch/attach configuration. WPF is not yet covered by the automated test matrix; automated application coverage currently includes console and WinForms only. This does not imply support for WPF on modern .NET/CoreCLR.

## Debug an existing project

1. Download the VSIX from [GitHub Releases](https://github.com/samsutherland/VBFrameworkDebug/releases), optionally verify it against the attached `SHA256SUMS.txt`, and install with **Extensions: Install from VSIX**. GitHub installations do not auto-update; install the newer VSIX to update.
2. Build the VB project with debugging symbols and optimizations disabled. The debugger reads both Portable PDBs (`DebugType=portable`) and classic Windows PDBs (`DebugType=full`), with the matching `.pdb` beside the executable.
3. Add a configuration to `.vscode\launch.json`, then open the VB source, set a breakpoint on an executable statement, and press **F5**:

   ```json
   {
     "version": "0.2.0",
     "configurations": [
       {
         "name": "Debug VB.NET Framework",
         "type": "desktopclr",
         "request": "launch",
         "program": "${workspaceFolder}\\bin\\Debug\\MyApp.exe",
         "architecture": "x64",
         "args": []
       }
     ]
   }
   ```

Use `"architecture": "x86"` for an x86 or Prefer 32-bit executable. An AnyCPU executable without Prefer 32-bit runs as x64 on x64 Windows. The working directory defaults to the executable's directory; use `cwd` to override it and `preLaunchTask` for an optional build task. Program input/output is in a separate Windows console, not the debug console.

## Inspect values and expressions

Debug Console, Watch, and hover share one read-only evaluator in the selected stack frame. Names are case-insensitive. Supported examples:

```vb
environment
Me
[answer]
sample.Count
sample.BaseCount
numbers(1)
matrix(-1, 3)
answer + 1
(answer > 40) AndAlso (sample.Count = 7)
environment & " value"
empty Is Nothing
```

- Locals/arguments, `Me`, inherited instance fields, array indexing/grouping, boxed primitives, Decimal, and enum display. In a synchronous instance method, you can omit `Me.` for fields (including VB `WithEvents` members, read directly from their backing fields). Locals and arguments take precedence. Arrays retain rank and actual lower bounds, with pages of at most 100 children. Expandable values provide reusable evaluation paths where representable.
- Literals (`True`, `False`, `Nothing`, doubled-quote strings, decimal numbers with optional `L`, `D`, `R`, `F` suffixes), unary `+`/`-`/`Not`, `+ - * / \ Mod`, string-only `&`, comparisons, reference `Is`/`IsNot`, and short-circuit `AndAlso`/`OrElse`.
- Arithmetic supports signed Integer/Long, Decimal, and finite Double values with checked integer/decimal overflow. `/` is floating/decimal division; `\` requires integer operands. Single/unsigned arithmetic, implicit object/Boolean/string conversions, and non-finite floating results are rejected, not guessed. Use `L` for integer literals outside Integer range.
- String comparisons require `"expressionStringComparison": "binary"` in launch/attach configuration. Set this only when Option Compare Binary semantics are appropriate. Project Option Compare/culture settings are not inferred.
- No assignments, method/property/default-property invocation, overloaded/custom operators, LINQ, late binding, implicit `ToString`, or debuggee-side conversions. Getters are never executed.
- Expressions are bounded to 4096 characters and 32 nested levels. Display strings are capped at 1024 characters; expression strings at 4096. Per-stop variable handles are bounded; references expire on resume.
- Basic VB async state-machine inspection recovers `Me` and uniquely named saved locals/arguments from compiler-generated fields. Ambiguous hoisted scopes are rejected. Generated frame names remain visible; logical async stacks and step-over-Await parity are not implemented.

An executable statement breakpoint must expose the real argument value. Stepping into a native method prologue can legitimately report `CORDBG_E_IL_VAR_NOT_AVAILABLE` (`0x80131304`); continue to the first executable statement before inspecting it.

## Breakpoints, exceptions, and execution

Source breakpoints, step into/over/out, pause, threads, and call stacks use **all-stop** execution. Source steps skip symbol-less methods and hidden sequence points to find a mapped line. Breakpoints, pauses, and enabled exception stops still interrupt stepping. Single-thread and instruction stepping are unsupported. Managed frames without symbols are displayed without invented source locations.

Conditional breakpoints use the same pure evaluator and require a Boolean. Hit conditions accept positive `N` or `>= N` (stop from hit N onward), `== N` (only hit N), and `% N` (every Nth hit). Counts increment before condition evaluation, per logical breakpoint across threads/AppDomains. Reapplying an identical specification preserves its count/ID; changing or removing/readding it resets them.

Logpoints support `count={iteration}` and escaped `{{` / `}}`. Expressions use the same display formatting as Watch (including quoted strings). Successful logpoints do not stop. Condition/logpoint evaluation errors **stop and report a diagnostic**, never silently skip the breakpoint.

The Breakpoints view exposes **Unhandled CLR exceptions** (default) and **All thrown CLR exceptions**. Exception details read type, message, and bounded inner exceptions without getters. WinForms/Task infrastructure may catch failures before CLR-unhandled notification; enable all-thrown when investigating them.

## Attach and detach

Use the numeric PID of a local .NET Framework process and its matching architecture:

```json
{
  "name": "Attach VB.NET Framework",
  "type": "desktopclr",
  "request": "attach",
  "processId": 1234,
  "architecture": "x64"
}
```

Attach resumes after initial configuration; set source breakpoints before continuing configuration or use Pause once attached. Disconnect/client EOF detaches and resumes attached processes by default; launched processes terminate by default. DAP `disconnect.terminateDebuggee` overrides ownership policy. No process picker, automatic elevation, modern .NET/CoreCLR, remote, IIS/service, mixed-mode, or Edit and Continue support.

Known shutdown limitation: forcibly killing an externally attached, paused target can leave the CLR debugger reporting `CORDBG_E_ILLEGAL_SHUTDOWN_ORDER` during final cleanup. The worker reports this warning and exits; OS handles are released and the exit-race test verifies no surviving target. This native cleanup warning remains a release-qualification issue.

## Sources and diagnostics

Keep the matching PDB beside each module. Module events and the DAP Modules request expose symbol status, including missing/mismatched symbols. Breakpoint IDs remain stable across module reloads. Exact executable lines are preferred; comment lines relocate only inside a single proven containing method/source span. Ambiguous locations stay unverified rather than moving to a neighboring method. DAP executable-location queries are supported.

For builds from another directory, map absolute PDB source prefixes to local directories:

```json
"sourceFileMap": { "C:\\build\\src": "${workspaceFolder}" }
```

Longest path-boundary-matched prefix wins. Mapped source checksums are verified; modified sources stay unverified. Missing checksums produce a warning, not a claim of verification. No source download or symbol server support. Use binaries and PDBs you trust.

Set `"diagnostics": true` to emit `[desktopclr]` request names and stop/module transitions. This opt-in trace excludes expression text, variable values, launch arguments, and paths. **Ordinary errors and module/source responses can contain paths; Debug Console results/logpoints contain values.** Review/redact the entire console or protocol capture before sharing it; do not share raw captures containing credentials or business data.

## Develop and package

On Windows, with PowerShell 7, .NET SDK 8 or later, and .NET Framework 4.8 runtime/reference assemblies, run `.\scripts\build.ps1` to stage the two workers. NuGet restores reference assemblies when needed; building does not require the Visual Studio IDE. Run `.\scripts\build.ps1 -Package` (requires Node.js 22+/npm) to create a versioned VSIX and `dist\release-info.json` with source revision/dirty state and package/worker SHA256 hashes. The VSIX contains both workers. For development, use **Run the extension in a development host** in this workspace.

Run `.\scripts\test.ps1` (also requires Python 3 and Node.js) for pure evaluator/extension tests and real console/WinForms DAP tests in the x86/x64 x Portable/Windows PDB matrix, plus Prefer32Bit and negative symbol/bitness cases. Add `-VSCode` to package/install the VSIX into an isolated VS Code profile and verify conditional breakpoints, evaluation, paging, modules, and locations without the C# extension. The Windows GitHub workflow runs the matrix and publishes VSIX/metadata/test-log artifacts; it must pass remotely before treating CI as qualified.

The complete matrix and isolated-profile installation have been exercised on a developer machine. **A separate clean Windows machine without Visual Studio has not yet been qualified.** A release gate is to install only the Framework runtime, VS Code, and the prebuilt VSIX/fixtures there, then verify both launch and attach (including detach survival). An isolated profile is not a substitute for that test.

### GitHub prereleases

Release Please uses conventional commits such as `fix:`, `feat:`, and `feat!:` to maintain a release PR with version and changelog updates. Merge that PR when ready. It creates a tag and a **draft prerelease**, then dispatches the Windows workflow for that tag: the workflow verifies the version, runs the test matrix, packages the workers, and attaches the VSIX, provenance metadata, and `SHA256SUMS.txt`. The draft can exist before tests finish: **do not publish until the tagged Windows workflow succeeds and all three assets are present**. Review the generated notes and known limitations before publishing.

Actions must be allowed to create pull requests in repository settings. PRs created using `GITHUB_TOKEN` may require a maintainer to approve their workflow runs. No personal publishing token is needed. If dispatch fails or the build fails, fix the failure and manually run the Windows workflow at the release tag; the workflow refuses to upload assets to a published or non-prerelease release. Manually pushed matching version tags continue to create draft prereleases. Ordinary commit/PR workflow artifacts are not published releases.

The extension and adapter are MIT-licensed. The VSIX includes notices for ClrDebug, dnlib, and the Microsoft runtime libraries that it bundles. The .NET Framework runtime is a system prerequisite and is not included.
