# VB.NET Framework Debugger for VS Code

Launch a local VB.NET console executable targeting .NET Framework 4.8 from VS Code on x64 Windows. The extension supports source breakpoints, step into/over/out, call stacks, and read-only locals and arguments. It ships x86 and x64 debug adapter workers, uses the installed desktop CLR, and does not depend on Visual Studio or the C# extension.

## Debug an existing project

1. Install the Windows VSIX with **Extensions: Install from VSIX**.
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

The Debug Console, Watch window, and hovers can evaluate local and argument names in the selected stack frame (for example `environment`, `Me`, or `[answer]`). Names are case-insensitive. Results can be expanded like Locals. This is read-only name lookup, not a full VB expression evaluator: arithmetic, member access, array indexing, assignments, property evaluation, and method calls are not yet supported.

If a breakpoint stays unverified, check that the source path matches the build machine's path recorded in the PDB and that the line contains an executable statement. Use project binaries and PDBs you trust. Source remapping, symbol servers, attach, mixed-mode debugging, conditional breakpoints, and optimized/async stepping parity are not supported. Variable expansion reads fields and array elements directly, with limits; it does not run property getters or `ToString`.

## Develop and package

On Windows, with PowerShell 7 and a .NET SDK, run `.\scripts\build.ps1` to stage the two workers. Run `.\scripts\build.ps1 -Package` (also requires Node.js/npm) to create the VSIX in `dist`. For development, run **Run the extension in a development host** in this workspace; in the new window choose **Debug the VB console fixture**.

Run `python tests\smoke.py` after building the fixture and adapter in Debug, or run `.\scripts\test.ps1` for the supported architecture/PDB matrix and negative cases. Add `-VSCode` to build and install the VSIX in an isolated VS Code profile and verify a source breakpoint and Locals without the C# extension.

The extension and adapter are MIT-licensed. The VSIX includes notices for ClrDebug, dnlib, and the Microsoft runtime libraries that it bundles. The .NET Framework runtime is a system prerequisite and is not included.
