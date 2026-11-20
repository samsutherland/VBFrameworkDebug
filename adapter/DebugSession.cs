using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using ClrDebug;

namespace DesktopClr;

internal sealed class DebugSession(Action<string, object> emit) : IDisposable
{
    private sealed class Variable(string name, string value, int variablesReference = 0, string? type = null)
    {
        public string name { get; } = name;
        public string value { get; } = value;
        public int variablesReference { get; } = variablesReference;
        public string? type { get; } = type;
    }

    private sealed class Breakpoint(int id, int line)
    {
        public int Id { get; } = id;
        public int RequestedLine { get; } = line;
        public SourcePoint? Bound { get; set; }
        public Dictionary<CORDB_ADDRESS, CorDebugFunctionBreakpoint> Handles { get; } = new();
        public object Describe() => new { id = Id, verified = Bound != null, line = Bound?.Line ?? RequestedLine,
            message = Bound == null ? "No matching executable source location is loaded." : null };
    }

    private readonly Dictionary<string, Breakpoint[]> breakpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CorDebugFunctionBreakpoint> retired = new();
    private readonly Dictionary<CORDB_ADDRESS, (CorDebugModule Module, Dictionary<int, MethodSymbols> Symbols)> modules = new();
    private readonly Dictionary<int, CorDebugILFrame> frames = new();
    private readonly Dictionary<int, Func<object[]>> variables = new();
    private int nextId;
    private CorDebug? debugger;
    private CorDebugManagedCallback? callback;
    private CorDebugProcess? process;
    private CorDebugController? stopped;
    private CorDebugStepper? stepper;
    private bool finished;
    private bool shuttingDown;
    private string? mainProgram;
    private IntPtr processHandle;
    public object Gate { get; } = new();

    public void Launch(string program, string cwd, string[] args)
    {
        ValidateTarget(program);
        mainProgram = program;
        if (!Directory.Exists(cwd))
            throw new DirectoryNotFoundException(cwd);
        debugger = new CorDebug();
        callback = new CorDebugManagedCallback();
        callback.OnAnyEvent += (_, e) => OnCallback(e);
        debugger.SetManagedHandler(callback);
        var startup = new STARTUPINFOW { cb = Marshal.SizeOf(typeof(STARTUPINFOW)) };
        var info = new PROCESS_INFORMATION();
        process = debugger.CreateProcess(program, string.Join(" ", new[] { program }.Concat(args).Select(Quote)),
            default, default, false, CreateProcessFlags.CREATE_NEW_CONSOLE, IntPtr.Zero, cwd, startup,
            ref info, CorDebugCreateProcessFlags.DEBUG_NO_SPECIAL_OPTIONS);
        processHandle = info.hProcess;
        CloseHandle(info.hThread);
        emit("process", new { name = Path.GetFileName(program), systemProcessId = info.dwProcessId, isLocalProcess = true, startMethod = "launch" });
    }

    private static void ValidateTarget(string path)
    {
        using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(path));
        var header = pe.PEHeaders.CorHeader ?? throw new InvalidDataException("Program is not a managed executable.");
        if ((header.Flags & System.Reflection.PortableExecutable.CorFlags.NativeEntryPoint) != 0 ||
            header.EntryPointTokenOrRelativeVirtualAddress == 0)
            throw new InvalidDataException("Program must have a managed entry point.");
        var machine = pe.PEHeaders.CoffHeader.Machine;
        bool x86 = machine == System.Reflection.PortableExecutable.Machine.I386 &&
            (header.Flags.HasFlag(System.Reflection.PortableExecutable.CorFlags.Requires32Bit) ||
             header.Flags.HasFlag(System.Reflection.PortableExecutable.CorFlags.Prefers32Bit));
        if (!(machine == System.Reflection.PortableExecutable.Machine.I386 ||
              machine == System.Reflection.PortableExecutable.Machine.Amd64) ||
            x86 == Environment.Is64BitProcess)
            throw new InvalidDataException($"The program does not match the {(Environment.Is64BitProcess ? "x64" : "x86")} adapter.");
    }

    private static string Quote(string arg)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private void OnCallback(CorDebugManagedCallbackEventArgs e)
    {
        lock (Gate)
        {
            try
            {
                if (e is ExitProcessCorDebugManagedCallbackEventArgs)
                {
                    finished = true;
                    stopped = null;
                    InvalidateHandles();
                    emit("terminated", new { });
                    return;
                }
                if (!shuttingDown)
                {
                    switch (e)
                    {
                        case CreateAppDomainCorDebugManagedCallbackEventArgs created:
                            created.AppDomain.Attach();
                            break;
                        case LoadModuleCorDebugManagedCallbackEventArgs loaded:
                            LoadModule(loaded.Module);
                            break;
                        case UnloadModuleCorDebugManagedCallbackEventArgs unloaded:
                            UnloadModule(unloaded.Module);
                            break;
                        case BreakpointCorDebugManagedCallbackEventArgs hit:
                            if (breakpoints.Values.SelectMany(b => b).Any(b => b.Handles.Values.Any(h => Equals(h.Raw, hit.Breakpoint.Raw))))
                                Stop(e, hit.Thread, "breakpoint");
                            break;
                        case StepCompleteCorDebugManagedCallbackEventArgs step:
                            stepper = null;
                            Stop(e, step.Thread, "step");
                            break;
                        case BreakCorDebugManagedCallbackEventArgs pause:
                            Stop(e, pause.Thread, "pause");
                            break;
                        case DebuggerErrorCorDebugManagedCallbackEventArgs error:
                            Diagnostic($"Desktop CLR debugger error: {error.ErrorHR}");
                            break;
                    }
                }
                if (e.Continue) e.Controller.Continue(false);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or IOException or DebugException)
            {
                Diagnostic($"CLR callback {e.Kind} failed: {ex.Message}");
                if (e.Continue)
                {
                    var result = e.Controller.TryContinue(false);
                    if ((int)result < 0) Diagnostic($"Continuing after callback failure failed: {result}");
                }
            }
        }
    }

    private void Stop(CorDebugManagedCallbackEventArgs e, CorDebugThread thread, string reason)
    {
        stopped = e.Controller;
        e.Continue = false;
        RefreshBreakpoints(true);
        emit("stopped", new { reason, threadId = thread.Id, allThreadsStopped = true });
    }

    private void LoadModule(CorDebugModule module)
    {
        string path = module.Name;
        if (!Path.IsPathRooted(path) || !File.Exists(path)) return;
        if (!File.Exists(Path.ChangeExtension(path, ".pdb")))
        {
            if (string.Equals(path, mainProgram, StringComparison.OrdinalIgnoreCase))
                Diagnostic($"Cannot find symbols for {path}. Put its matching PDB beside the executable.");
            return;
        }
        try { modules.Add(module.BaseAddress, (module, Symbols.Read(path))); }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidOperationException or
            InvalidDataException or UnauthorizedAccessException)
        {
            Diagnostic($"Cannot load symbols for {path}: {ex.Message}");
            return;
        }
        RefreshBreakpoints(true);
    }

    private void UnloadModule(CorDebugModule module)
    {
        var key = module.BaseAddress;
        if (!modules.Remove(key)) return;
        foreach (var b in breakpoints.Values.SelectMany(b => b))
        {
            if (!b.Handles.Remove(key) || b.Handles.Count != 0) continue;
            b.Bound = null;
            emit("breakpoint", new { reason = "changed", breakpoint = b.Describe() });
        }
    }

    public object SetBreakpoints(string path, int[] lines)
    {
        path = Path.GetFullPath(path);
        CorDebugProcess? running;
        lock (Gate) running = stopped == null && !finished && modules.Count > 0 ? process : null;
        // Stop may wait for a CLR callback; that callback must be able to acquire Gate.
        if (running == null)
        {
            lock (Gate) return SetBreakpointsCore(path, lines, false);
        }
        var hr = running.TryStop(0);
        lock (Gate)
        {
            if ((int)hr < 0 && !finished)
                throw new InvalidOperationException($"Could not synchronize breakpoint update: {hr}");
            try { return SetBreakpointsCore(path, lines, !finished); }
            finally { if ((int)hr >= 0 && !finished) running.Continue(false); }
        }
    }

    private object SetBreakpointsCore(string path, int[] lines, bool synchronized)
    {
        breakpoints.TryGetValue(path, out var previous);
        var reusable = (previous ?? Array.Empty<Breakpoint>()).GroupBy(b => b.RequestedLine)
            .ToDictionary(g => g.Key, g => new Queue<Breakpoint>(g));
        var entries = lines.Select(line => reusable.TryGetValue(line, out var group) && group.Count > 0
            ? group.Dequeue() : new Breakpoint(++nextId, line)).ToArray();
        retired.AddRange(reusable.Values.SelectMany(g => g).SelectMany(b => b.Handles.Values));
        breakpoints[path] = entries;
        if (stopped != null || synchronized) RefreshBreakpoints(false);
        return new { breakpoints = entries.Select(b => b.Describe()).ToArray() };
    }

    private void RefreshBreakpoints(bool notify)
    {
        foreach (var handle in retired)
        {
            var hr = handle.TryActivate(false);
            if ((int)hr < 0) Diagnostic($"Removing breakpoint failed: {hr}");
        }
        retired.Clear();
        foreach (var pair in breakpoints)
        foreach (var b in pair.Value)
        {
            bool wasBound = b.Bound != null;
            foreach (var entry in modules)
            {
                if (b.Handles.ContainsKey(entry.Key)) continue;
                // Do not move a requested breakpoint into an unrelated method.
                var point = entry.Value.Symbols.Values.SelectMany(m => m.Points).FirstOrDefault(p =>
                    !p.Hidden && p.Line == b.RequestedLine && string.Equals(p.Path, pair.Key, StringComparison.OrdinalIgnoreCase));
                if (point == null) continue;
                try
                {
                    var handle = entry.Value.Module.GetFunctionFromToken((mdMethodDef)point.Token).ILCode.CreateBreakpoint(point.Offset);
                    handle.Activate(true);
                    b.Handles.Add(entry.Key, handle);
                    b.Bound = point;
                }
                catch (DebugException ex) { Diagnostic($"Binding {pair.Key}:{b.RequestedLine} failed: {ex.Message}"); }
            }
            if (notify && !wasBound && b.Bound != null)
                emit("breakpoint", new { reason = "changed", breakpoint = b.Describe() });
        }
    }

    private CorDebugProcess RequireStopped() => stopped != null && process != null
        ? process : throw new InvalidOperationException("The program is not stopped.");

    public object Threads() => new { threads = stopped == null ? Array.Empty<object>() :
        RequireStopped().Threads.Select(t => (object)new { id = t.Id, name = $"Thread {t.Id}" }).ToArray() };

    private CorDebugThread Thread(int id) => RequireStopped().Threads.FirstOrDefault(t => t.Id == id)
        ?? throw new InvalidOperationException($"Unknown thread {id}.");

    private MethodSymbols? Method(CorDebugFrame frame) =>
        modules.TryGetValue(frame.Function.Module.BaseAddress, out var module) &&
        module.Symbols.TryGetValue((int)frame.FunctionToken, out var method) ? method : null;

    public object Stack(int threadId, int start, int levels)
    {
        var result = new List<object>();
        foreach (var chain in Thread(threadId).Chains.Where(c => c.IsManaged))
        foreach (var frame in chain.Frames.OfType<CorDebugILFrame>())
        {
            var method = Method(frame);
            var point = method?.At(frame.IP.pnOffset);
            if (point?.Hidden == true) point = null;
            int id = ++nextId;
            frames.Add(id, frame);
            result.Add(new { id, name = method?.Name ?? $"Managed method 0x{(int)frame.FunctionToken:X8}",
                source = point == null ? null : new { name = Path.GetFileName(point.Path), path = point.Path },
                line = point?.Line ?? 0, column = point?.Column ?? 0 });
        }
        return new { stackFrames = result.Skip(start).Take(levels == 0 ? result.Count : levels).ToArray(), totalFrames = result.Count };
    }

    public object Scopes(int frameId)
    {
        RequireStopped();
        if (!frames.TryGetValue(frameId, out var frame)) throw new InvalidOperationException("Unknown stack frame.");
        int localRef = Store(() => Locals(frame));
        int argRef = Store(() => Arguments(frame));
        return new { scopes = new[] {
            new { name = "Locals", presentationHint = "locals", variablesReference = localRef, expensive = false },
            new { name = "Arguments", presentationHint = "arguments", variablesReference = argRef, expensive = false }
        } };
    }

    private Variable[] Locals(CorDebugILFrame frame)
    {
        var method = Method(frame);
        if (method == null) return Array.Empty<Variable>();
        int ip = frame.IP.pnOffset;
        return method.Locals.Where(l => ip >= l.Start && ip < l.End).OrderByDescending(l => l.Start)
            .GroupBy(l => l.Slot).Select(g => g.First()).OrderBy(l => l.Slot)
            .Select(l => ReadValue(l.Name, () => frame.GetLocalVariable(l.Slot))).ToArray();
    }

    private Variable[] Arguments(CorDebugILFrame frame)
    {
        var method = Method(frame);
        if (method != null)
        {
            var names = method.IsStatic ? method.Arguments : new[] { "Me" }.Concat(method.Arguments).ToArray();
            return names.Select((name, i) => ReadValue(name, () => frame.GetArgument(i))).ToArray();
        }
        var args = frame.Arguments;
        return args.Select((value, i) => ReadValue($"arg{i}", () => value)).ToArray();
    }

    public object Variables(int reference)
    {
        RequireStopped();
        return variables.TryGetValue(reference, out var get) ? new { variables = get() }
            : throw new InvalidOperationException("Unknown or expired variable reference.");
    }

    public object Evaluate(string expression, int? frameId)
    {
        RequireStopped();
        if (frameId == null)
            throw new InvalidOperationException("Select a stack frame to evaluate a variable.");
        if (!frames.TryGetValue(frameId.Value, out var frame))
            throw new InvalidOperationException("Unknown or expired stack frame.");
        string name = expression.Trim();
        if (name.StartsWith("[", StringComparison.Ordinal) && name.EndsWith("]", StringComparison.Ordinal))
            name = name.Substring(1, name.Length - 2);
        if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_') ||
            name.Any(c => !(char.IsLetterOrDigit(c) || c == '_')))
            throw new NotSupportedException("Only local and argument names are supported; VB expressions and function calls are not yet supported.");
        var value = Locals(frame).FirstOrDefault(v => string.Equals(v.name, name, StringComparison.OrdinalIgnoreCase))
            ?? Arguments(frame).FirstOrDefault(v => string.Equals(v.name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"'{name}' is not a local or argument in the selected stack frame.");
        return new { result = value.value, value.type, value.variablesReference };
    }

    private int Store(Func<object[]> get)
    {
        int id = ++nextId;
        variables.Add(id, get);
        return id;
    }

    private Variable ReadValue(string name, Func<CorDebugValue> get)
    {
        try
        {
            var value = get();
            if (value is CorDebugReferenceValue reference)
            {
                if (reference.IsNull) return new Variable(name, "Nothing");
                value = reference.Dereference();
            }
            if (value is CorDebugStringValue text)
            {
                const int limit = 1024;
                int length = text.Length;
                string contents = length > limit ? $"<String of {length} characters>" :
                    System.Text.Json.JsonSerializer.Serialize(text.GetString(length + 1));
                return new Variable(name, contents, type: "String");
            }
            if (value is CorDebugArrayValue array)
                return new Variable(name, $"Array ({array.Count} elements)", Store(() =>
                    Enumerable.Range(0, Math.Min(array.Count, 100)).Select(i =>
                        ReadValue($"({i})", () => array.GetElementAtPosition(i))).ToArray()));
            if (value is CorDebugObjectValue instance)
            {
                var cls = instance.Class;
                string path = cls.Module.Name;
                if (Path.IsPathRooted(path) && File.Exists(path))
                {
                    using var pe = new PEReader(File.OpenRead(path));
                    var md = pe.GetMetadataReader();
                    var type = md.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.Handle((int)cls.Token));
                    string typeName = md.GetString(type.Name);
                    var fields = type.GetFields().Select(h => (Handle: h, Definition: md.GetFieldDefinition(h)))
                        .Where(f => !f.Definition.Attributes.HasFlag(FieldAttributes.Static)).Take(100)
                        .Select(f => (Name: md.GetString(f.Definition.Name), Token: (mdFieldDef)MetadataTokens.GetToken(f.Handle)))
                        .ToArray();
                    return new Variable(name, $"{{{typeName}}}", fields.Length == 0 ? 0 :
                        Store(() => fields.Select(f => ReadValue(f.Name, () => instance.GetFieldValue(cls.Raw, f.Token))).ToArray()), typeName);
                }
            }
            return new Variable(name, value is CorDebugGenericValue scalar ? Scalar(scalar) : $"<{value.Type}>",
                type: value.Type.ToString());
        }
        catch (DebugException ex)
        {
            return new Variable(name, $"<unavailable: {(int)ex.HResult:X8}>");
        }
    }

    private static string Scalar(CorDebugGenericValue value)
    {
        if (value.Size is <= 0 or > 8) return $"<{value.Type}>";
        IntPtr buffer = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt64(buffer, 0);
            value.GetValue(buffer);
            long bits = Marshal.ReadInt64(buffer);
            object display = value.Type switch
            {
                CorElementType.Boolean => bits == 0 ? "False" : "True",
                CorElementType.Char => (char)bits,
                CorElementType.I1 => (sbyte)bits, CorElementType.U1 => (byte)bits,
                CorElementType.I2 => (short)bits, CorElementType.U2 => (ushort)bits,
                CorElementType.I4 => (int)bits, CorElementType.U4 => (uint)bits,
                CorElementType.I8 => bits, CorElementType.U8 => (ulong)bits,
                CorElementType.R4 => BitConverter.ToSingle(BitConverter.GetBytes((int)bits), 0),
                CorElementType.R8 => BitConverter.Int64BitsToDouble(bits),
                _ => $"<{value.Type}>"
            };
            return Convert.ToString(display, CultureInfo.InvariantCulture)!;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Resume(int threadId, string command)
    {
        var thread = Thread(threadId);
        stepper?.Deactivate();
        stepper = null;
        if (command != "continue")
        {
            var frame = thread.ActiveFrame as CorDebugILFrame ?? throw new InvalidOperationException("No managed frame to step.");
            stepper = frame.CreateStepper();
            if (command == "stepOut") stepper.StepOut();
            else
            {
                int ip = frame.IP.pnOffset;
                var method = Method(frame);
                var point = method?.At(ip);
                if (point != null)
                {
                    int end = method!.Points.FirstOrDefault(p => !p.Hidden && p.Offset > ip &&
                        (p.Line != point.Line || p.Path != point.Path))?.Offset ?? frame.Code.Size;
                    if (end > ip)
                        stepper.StepRange(command == "stepIn", new[] { new COR_DEBUG_STEP_RANGE
                            { startOffset = point.Offset, endOffset = end } }, 1);
                    else stepper.Step(command == "stepIn");
                }
                else stepper.Step(command == "stepIn");
            }
        }
        RefreshBreakpoints(true);
        stopped!.Continue(false);
        stopped = null;
        InvalidateHandles();
    }

    private void InvalidateHandles()
    {
        frames.Clear();
        variables.Clear();
    }

    public void Dispose()
    {
        lock (Gate)
            shuttingDown = true;
        if (processHandle != IntPtr.Zero)
        {
            try
            {
                if (WaitForSingleObject(processHandle, 0) == 258 && !TerminateProcess(processHandle, 0) &&
                    WaitForSingleObject(processHandle, 0) == 258)
                    Console.Error.WriteLine($"Terminating debuggee failed: Win32 error {Marshal.GetLastWin32Error()}.");
                if (WaitForSingleObject(processHandle, 2000) != 0)
                    Console.Error.WriteLine("Timed out waiting for the debuggee to exit.");
            }
            finally { CloseHandle(processHandle); }
        }
        if (debugger != null)
        {
            HRESULT hr;
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                hr = debugger.TryTerminate();
                if (hr != HRESULT.CORDBG_E_ILLEGAL_SHUTDOWN_ORDER) break;
                System.Threading.Thread.Sleep(10);
            } while (deadline.ElapsedMilliseconds < 2000);
            if ((int)hr < 0) Console.Error.WriteLine($"Closing CLR debugger: {hr}");
        }
        GC.KeepAlive(callback);
    }

    private void Diagnostic(string text) => emit("output", new { category = "stderr", output = text + "\n" });

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr handle, uint exitCode);
}
