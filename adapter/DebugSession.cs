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
    private sealed class Variable(string name, string value, int variablesReference = 0, string? type = null,
        int? indexedVariables = null, int? namedVariables = null, string? evaluateName = null)
    {
        public string name { get; } = name;
        public string value { get; } = value;
        public int variablesReference { get; } = variablesReference;
        public string? type { get; } = type;
        public int? indexedVariables { get; } = indexedVariables;
        public int? namedVariables { get; } = namedVariables;
        public string? evaluateName { get; } = evaluateName;
    }

    private sealed class Breakpoint(int id, BreakpointRequest request)
    {
        public int Id { get; } = id;
        public BreakpointRequest Request { get; } = request;
        public int RequestedLine => Request.Line;
        public long Hits { get; set; }
        public SourcePoint? Bound { get; set; }
        public Dictionary<ICorDebugModule, List<CorDebugFunctionBreakpoint>> Handles { get; } = new();
        public string Message { get; set; } = "No matching executable source location is loaded.";
        public object Describe() => new
        {
            id = Id,
            verified = Bound != null,
            line = Bound?.Line ?? RequestedLine,
            message = Bound == null ? Message : (Bound.Checksum.Length == 0 ? "Source checksum unavailable in PDB." : null)
        };
    }

    private sealed class ModuleInfo(int id, CorDebugModule module, string path)
    {
        public int Id { get; } = id;
        public CorDebugModule Module { get; } = module;
        public string Path { get; } = path;
        public string Status { get; set; } = "Symbols not found";
        public Dictionary<int, MethodSymbols> Symbols { get; set; } = new();
        public object Describe() => new { id = Id, name = System.IO.Path.GetFileName(Path), path = Path,
            symbolStatus = Status, architecture = Environment.Is64BitProcess ? "x64" : "x86" };
    }

    private readonly Dictionary<string, Breakpoint[]> breakpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CorDebugFunctionBreakpoint> retired = new();
    private readonly Dictionary<ICorDebugModule, ModuleInfo> modules = new();
    private readonly SourcePaths sourcePaths = new();
    private readonly Dictionary<int, CorDebugILFrame> frames = new();
    private readonly Dictionary<int, (Func<int, int, object[]> Get, bool Indexed)> variables = new();
    private int nextId;
    private CorDebug? debugger;
    private CorDebugManagedCallback? callback;
    private CorDebugProcess? process;
    private CorDebugController? stopped;
    private CorDebugStepper? stepper;
    private int sourceStepRetries;
    private bool finished;
    private bool shuttingDown;
    private string? mainProgram;
    private IntPtr processHandle;
    private bool attached;
    private bool? terminateOnDisconnect;
    private bool breakOnThrown;
    private bool breakOnUnhandled = true;
    private (int ThreadId, object Info)? exceptionStop;
    private bool binaryStringComparison;
    public object Gate { get; } = new();

    public void SetSourceMap(Dictionary<string, string> mapping) => sourcePaths.Configure(mapping);
    public void SetStringComparison(string? mode)
    {
        if (mode is not (null or "binary"))
            throw new NotSupportedException("Only explicit Option Compare Binary expression comparisons are supported.");
        binaryStringComparison = mode == "binary";
    }

    public object Modules(int start, int count) => new
    {
        modules = modules.Values.Skip(start)
        .Take(count == 0 ? modules.Count : count).Select(m => m.Describe()).ToArray(),
        totalModules = modules.Count
    };

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

    public void Attach(int processId)
    {
        processHandle = OpenProcess(0x101000, false, processId);
        if (processHandle == IntPtr.Zero)
            throw new InvalidOperationException($"Cannot open process {processId}: Win32 error {Marshal.GetLastWin32Error()}.");
        attached = true;
        if (!IsWow64Process(processHandle, out bool wow64))
            throw new InvalidOperationException($"Cannot determine target bitness: Win32 error {Marshal.GetLastWin32Error()}.");
        bool target64 = Environment.Is64BitOperatingSystem && !wow64;
        if (target64 != Environment.Is64BitProcess)
            throw new InvalidOperationException("The selected process does not match the adapter architecture.");
        debugger = new CorDebug();
        callback = new CorDebugManagedCallback();
        callback.OnAnyEvent += (_, e) => OnCallback(e);
        debugger.SetManagedHandler(callback);
        process = debugger.DebugActiveProcess(processId, false);
        emit("process", new { name = $"Process {processId}", systemProcessId = processId, isLocalProcess = true, startMethod = "attach" });
    }

    public void Disconnect(bool? terminateDebuggee)
    {
        terminateOnDisconnect = terminateDebuggee;
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
                    if (finished) return;
                    finished = true;
                    stopped = null;
                    InvalidateHandles();
                    if (processHandle != IntPtr.Zero && GetExitCodeProcess(processHandle, out uint exitCode))
                        emit("exited", new { exitCode = unchecked((int)exitCode) });
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
                        case CreateThreadCorDebugManagedCallbackEventArgs createdThread:
                            emit("thread", new { reason = "started", threadId = createdThread.Thread.Id });
                            break;
                        case ExitThreadCorDebugManagedCallbackEventArgs exitedThread:
                            emit("thread", new { reason = "exited", threadId = exitedThread.Thread.Id });
                            break;
                        case LoadModuleCorDebugManagedCallbackEventArgs loaded:
                            LoadModule(loaded.Module);
                            break;
                        case UnloadModuleCorDebugManagedCallbackEventArgs unloaded:
                            UnloadModule(unloaded.Module);
                            break;
                        case BreakpointCorDebugManagedCallbackEventArgs hit:
                            var breakpoint = breakpoints.Values.SelectMany(b => b).FirstOrDefault(b => b.Handles.Values.SelectMany(h => h)
                                .Any(h => Equals(h.Raw, hit.Breakpoint.Raw)));
                            if (breakpoint != null && ShouldStop(breakpoint, hit.Thread))
                                Stop(e, hit.Thread, "breakpoint");
                            break;
                        case StepCompleteCorDebugManagedCallbackEventArgs step:
                            if (stepper != null && Equals(stepper.Raw, step.Stepper.Raw))
                                CompleteStep(step);
                            break;
                        case BreakCorDebugManagedCallbackEventArgs pause:
                            Stop(e, pause.Thread, "pause");
                            break;
                        case Exception2CorDebugManagedCallbackEventArgs exception:
                            if ((breakOnThrown && exception.EventType == CorDebugExceptionCallbackType.DEBUG_EXCEPTION_FIRST_CHANCE) ||
                                (breakOnUnhandled && exception.EventType == CorDebugExceptionCallbackType.DEBUG_EXCEPTION_UNHANDLED))
                            {
                                var value = exception.Thread.CurrentException;
                                var details = ExceptionDetails(value, 0);
                                string typeName = ValueAccess.Dereference(value) is CorDebugObjectValue instance
                                    ? ValueAccess.TypeName(instance.Class) : "System.Exception";
                                Stop(e, exception.Thread, "exception");
                                exceptionStop = (exception.Thread.Id, new
                                {
                                    exceptionId = typeName,
                                    breakMode = exception.EventType == CorDebugExceptionCallbackType.DEBUG_EXCEPTION_UNHANDLED ? "unhandled" : "always",
                                    details
                                });
                            }
                            break;
                        case DebuggerErrorCorDebugManagedCallbackEventArgs error:
                            Diagnostic($"Desktop CLR debugger error: {error.ErrorHR}");
                            break;
                    }
                }
                if (e.Continue) e.Controller.Continue(false);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or IOException or DebugException or ArgumentException)
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
        stepper?.Deactivate();
        stepper = null;
        InvalidateHandles();
        stopped = e.Controller;
        e.Continue = false;
        RefreshBreakpoints(true);
        emit("stopped", new { reason, threadId = thread.Id, allThreadsStopped = true });
    }

    public void SetExceptionBreakpoints(string[] filters)
    {
        if (filters.Any(f => f is not ("thrown" or "unhandled")))
            throw new ArgumentException("Supported exception filters are 'thrown' and 'unhandled'.");
        breakOnThrown = filters.Contains("thrown");
        breakOnUnhandled = filters.Contains("unhandled");
    }

    public object ExceptionInfo(int threadId)
    {
        RequireStopped();
        return exceptionStop is { } stop && stop.ThreadId == threadId ? stop.Info :
            throw new InvalidOperationException("The selected thread is not stopped on an exception.");
    }

    private object ExceptionDetails(CorDebugValue value, int depth)
    {
        var instance = ValueAccess.Dereference(value) as CorDebugObjectValue
            ?? throw new InvalidOperationException("The CLR exception object is unavailable.");
        string typeName = ValueAccess.TypeName(instance.Class);
        var fields = ValueAccess.Fields(instance);
        var messageField = fields.FirstOrDefault(f => f.Name == "_message");
        string message = "<message unavailable without property evaluation>";
        if (messageField != null)
        {
            var raw = ValueAccess.Dereference(instance.GetFieldValue(messageField.Class.Raw, messageField.Token));
            if (raw is CorDebugStringValue text)
                message = text.Length <= 4096 ? text.GetString(text.Length + 1) : "<message exceeds 4096 characters>";
        }
        var inner = fields.FirstOrDefault(f => f.Name == "_innerException");
        var children = new List<object>();
        if (inner != null && depth < 4)
        {
            var raw = ValueAccess.Dereference(instance.GetFieldValue(inner.Class.Raw, inner.Token));
            if (raw is CorDebugObjectValue) children.Add(ExceptionDetails(raw, depth + 1));
        }
        return new { message, typeName, fullTypeName = typeName, innerException = children.ToArray() };
    }

    private void LoadModule(CorDebugModule module)
    {
        string path = module.Name;
        var info = new ModuleInfo(++nextId, module, path);
        modules[module.Raw] = info;
        if (!Path.IsPathRooted(path) || !File.Exists(path))
            info.Status = "Module is dynamic, in-memory, or unavailable on disk";
        else if (!File.Exists(Path.ChangeExtension(path, ".pdb")))
        {
            if (string.Equals(path, mainProgram, StringComparison.OrdinalIgnoreCase))
                Diagnostic($"Cannot find symbols for {path}. Put its matching PDB beside the executable.");
        }
        else
        {
            try { info.Symbols = Symbols.Read(path); info.Status = "Symbols loaded"; }
            catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidOperationException or
                InvalidDataException or UnauthorizedAccessException)
            {
                info.Status = ex.Message;
                Diagnostic($"Cannot load symbols for {path}: {ex.Message}");
            }
        }
        emit("module", new { reason = "new", module = info.Describe() });
        RefreshBreakpoints(true);
    }

    private void UnloadModule(CorDebugModule module)
    {
        var key = module.Raw;
        if (!modules.TryGetValue(key, out var info)) return;
        modules.Remove(key);
        emit("module", new { reason = "removed", module = info.Describe() });
        foreach (var b in breakpoints.Values.SelectMany(b => b))
        {
            if (!b.Handles.Remove(key) || b.Handles.Count != 0) continue;
            b.Bound = null;
            emit("breakpoint", new { reason = "changed", breakpoint = b.Describe() });
        }
    }

    public object SetBreakpoints(string path, BreakpointRequest[] requests)
    {
        path = Path.GetFullPath(path);
        CorDebugProcess? running;
        lock (Gate) running = stopped == null && !finished && modules.Count > 0 ? process : null;
        // Stop may wait for a CLR callback; that callback must be able to acquire Gate.
        if (running == null)
        {
            lock (Gate) return SetBreakpointsCore(path, requests, false);
        }
        var hr = running.TryStop(0);
        lock (Gate)
        {
            if ((int)hr < 0 && !finished)
                throw new InvalidOperationException($"Could not synchronize breakpoint update: {hr}");
            try { return SetBreakpointsCore(path, requests, !finished); }
            finally { if ((int)hr >= 0 && !finished) running.Continue(false); }
        }
    }

    private object SetBreakpointsCore(string path, BreakpointRequest[] requests, bool synchronized)
    {
        breakpoints.TryGetValue(path, out var previous);
        var reusable = (previous ?? Array.Empty<Breakpoint>()).GroupBy(b => b.Request.Key)
            .ToDictionary(g => g.Key, g => new Queue<Breakpoint>(g));
        var entries = requests.Select(request => reusable.TryGetValue(request.Key, out var group) && group.Count > 0
            ? group.Dequeue() : new Breakpoint(++nextId, request)).ToArray();
        retired.AddRange(reusable.Values.SelectMany(g => g).SelectMany(b => b.Handles.Values).SelectMany(h => h));
        breakpoints[path] = entries;
        if (stopped != null || synchronized) RefreshBreakpoints(false);
        return new { breakpoints = entries.Select(b => b.Describe()).ToArray() };
    }

    private bool ShouldStop(Breakpoint breakpoint, CorDebugThread thread)
    {
        try
        {
            breakpoint.Hits = checked(breakpoint.Hits + 1);
            if (!breakpoint.Request.Hit(breakpoint.Hits)) return false;
            if (thread.ActiveFrame is not CorDebugILFrame frame)
                throw new InvalidOperationException("No IL frame is available for breakpoint evaluation.");
            Evaluation Read(string expression) => new ValueExpression(expression, name => Resolve(frame, name), binaryStringComparison).Read();
            if (breakpoint.Request.Condition != null && !Read(breakpoint.Request.Condition).Boolean()) return false;
            if (breakpoint.Request.LogMessage == null) return true;
            var message = new StringBuilder();
            foreach (var part in breakpoint.Request.LogParts)
            {
                if (!part.Expression) { message.Append(part.Text); continue; }
                var evaluated = Read(part.Text);
                message.Append(evaluated.Raw == null ? ConstantValue("", evaluated.Primitive).value :
                    ReadValue("", () => evaluated.Raw).value);
            }
            emit("output", new { category = "console", output = message + "\n" });
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException or
            DebugException or COMException or ArithmeticException or FormatException or IOException)
        {
            Diagnostic($"Breakpoint condition/logpoint evaluation failed: {ex.Message}");
            return true;
        }
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
                string previousMessage = b.Message;
                bool sourceFound = false;
                bool locationFound = false;
                foreach (var entry in modules)
                {
                    if (b.Handles.ContainsKey(entry.Key)) continue;
                    var candidates = SourcePoints(entry.Value, pair.Key);
                    if (candidates.Length > 0) sourceFound = true;
                    var points = BindingPoints(candidates, b.RequestedLine);
                    if (points.Length == 0) continue;
                    locationFound = true;
                    var handles = new List<CorDebugFunctionBreakpoint>();
                    foreach (var point in points)
                    {
                        string? problem = sourcePaths.Problem(point);
                        if (problem != null) { b.Message = problem; continue; }
                        try
                        {
                            var handle = entry.Value.Module.GetFunctionFromToken((mdMethodDef)point.Token).ILCode.CreateBreakpoint(point.Offset);
                            handle.Activate(true);
                            handles.Add(handle);
                            b.Bound = point;
                        }
                        catch (DebugException ex)
                        {
                            b.Message = $"CLR breakpoint binding failed: {ex.Message}";
                            Diagnostic($"Binding {pair.Key}:{b.RequestedLine} failed: {ex.Message}");
                        }
                    }
                    if (handles.Count > 0) b.Handles.Add(entry.Key, handles);
                }
                if (b.Bound == null && !locationFound && sourceFound)
                    b.Message = "No executable sequence point on this line; select an executable statement in the same method.";
                if (notify && ((!wasBound && b.Bound != null) || (b.Bound == null && previousMessage != b.Message)))
                    emit("breakpoint", new { reason = "changed", breakpoint = b.Describe() });
            }
    }

    private SourcePoint[] SourcePoints(ModuleInfo module, string path) =>
        module.Symbols.Values.SelectMany(m => m.Points).Where(p => !p.Hidden &&
            string.Equals(sourcePaths.Map(p.Path), path, StringComparison.OrdinalIgnoreCase)).ToArray();

    private static SourcePoint[] BindingPoints(SourcePoint[] candidates, int line)
    {
        var exact = candidates.Where(p => p.Line == line).GroupBy(p => (p.Token, p.Offset))
            .Select(g => g.First()).ToArray();
        if (exact.Length > 0) return exact;
        var methods = candidates.GroupBy(p => p.Token)
            .Where(g => g.Min(p => p.Line) <= line && g.Max(p => Math.Max(p.Line, p.EndLine)) >= line).ToArray();
        if (methods.Length != 1) return Array.Empty<SourcePoint>();
        var point = methods[0].Where(p => p.Line >= line || p.EndLine >= line)
            .OrderBy(p => p.Line).ThenBy(p => p.Offset).FirstOrDefault();
        return point == null ? Array.Empty<SourcePoint>() : new[] { point };
    }

    public object BreakpointLocations(string path, int line, int endLine)
    {
        path = Path.GetFullPath(path);
        if (endLine < line) throw new ArgumentException("endLine must not precede line.");
        var points = modules.Values.SelectMany(m => SourcePoints(m, path))
            .Where(p => p.Line >= line && p.Line <= endLine && sourcePaths.Problem(p) == null)
            .GroupBy(p => (p.Line, p.Column)).Select(g => g.First()).OrderBy(p => p.Line).ThenBy(p => p.Column);
        return new
        {
            breakpoints = points.Select(p => new
            {
                line = p.Line,
                column = p.Column,
                endLine = p.EndLine,
                endColumn = p.EndColumn
            }).ToArray()
        };
    }

    private CorDebugProcess RequireStopped() => stopped != null && process != null
        ? process : throw new InvalidOperationException("The program is not stopped.");

    public void Pause()
    {
        CorDebugProcess target;
        lock (Gate)
        {
            if (stopped != null) throw new InvalidOperationException("The program is already stopped.");
            target = !finished && process != null ? process : throw new InvalidOperationException("The program is not running.");
        }
        // A cooperative stop can wait for callbacks, which also acquire Gate.
        target.Stop(0);
        lock (Gate)
        {
            if (finished) throw new InvalidOperationException("The program exited before it could pause.");
            if (stopped != null)
            {
                target.Continue(false);
                return;
            }
            try
            {
                var threads = target.Threads;
                var thread = threads.FirstOrDefault(t => t.Chains.Any(c => c.IsManaged)) ?? threads.FirstOrDefault()
                    ?? throw new InvalidOperationException("No managed threads are available to inspect.");
                stepper?.Deactivate();
                stepper = null;
                stopped = target;
                InvalidateHandles();
                emit("stopped", new { reason = "pause", threadId = thread.Id, allThreadsStopped = true });
            }
            catch
            {
                stopped = null;
                target.Continue(false);
                throw;
            }
        }
    }

    public object Threads() => new
    {
        threads = stopped == null ? Array.Empty<object>() :
        RequireStopped().Threads.Select(t => (object)new { id = t.Id, name = $"Thread {t.Id}" }).ToArray()
    };

    private CorDebugThread Thread(int id) => RequireStopped().Threads.FirstOrDefault(t => t.Id == id)
        ?? throw new InvalidOperationException($"Unknown thread {id}.");

    private MethodSymbols? Method(CorDebugFrame frame) =>
        modules.TryGetValue(frame.Function.Module.Raw, out var module) &&
        module.Symbols.TryGetValue((int)frame.FunctionToken, out var method) ? method : null;

    private static SourcePoint? VisiblePoint(MethodSymbols? method, int offset) =>
        method?.At(offset) is { Hidden: false } point ? point : null;

    public object Stack(int threadId, int start, int levels)
    {
        var result = new List<object>();
        foreach (var chain in Thread(threadId).Chains.Where(c => c.IsManaged))
            foreach (var frame in chain.Frames.OfType<CorDebugILFrame>())
            {
                var method = Method(frame);
                var point = VisiblePoint(method, frame.IP.pnOffset);
                int id = ++nextId;
                frames.Add(id, frame);
                result.Add(new
                {
                    id,
                    name = method?.Name ?? $"Managed method 0x{(int)frame.FunctionToken:X8}",
                    source = point == null ? null : new { name = Path.GetFileName(point.Path), path = sourcePaths.Map(point.Path) },
                    line = point?.Line ?? 0,
                    column = point?.Column ?? 0
                });
            }
        return new { stackFrames = result.Skip(start).Take(levels == 0 ? result.Count : levels).ToArray(), totalFrames = result.Count };
    }

    public object Scopes(int frameId)
    {
        RequireStopped();
        if (!frames.TryGetValue(frameId, out var frame)) throw new InvalidOperationException("Unknown stack frame.");
        int localRef = Store((start, count) => Locals(frame, start, count), false);
        int argRef = Store((start, count) => Arguments(frame, start, count), false);
        var method = Method(frame);
        int argumentCount = method == null ? frame.Arguments.Length : method.Arguments.Length + (method.IsStatic ? 0 : 1);
        return new
        {
            scopes = new[] {
            new { name = "Locals", presentationHint = "locals", variablesReference = localRef, expensive = false,
                namedVariables = LocalSlots(frame).Length + HoistedNames(frame).Length },
            new { name = "Arguments", presentationHint = "arguments", variablesReference = argRef, expensive = false, namedVariables = argumentCount }
        }
        };
    }

    private (int Start, int End, int Slot, string Name)[] LocalSlots(CorDebugILFrame frame)
    {
        var method = Method(frame);
        if (method == null) return Array.Empty<(int, int, int, string)>();
        int ip = frame.IP.pnOffset;
        return method.Locals.Where(l => ip >= l.Start && ip < l.End).OrderByDescending(l => l.Start)
            .GroupBy(l => l.Slot).Select(g => g.First()).OrderBy(l => l.Slot).ToArray();
    }

    private Variable[] Locals(CorDebugILFrame frame, int start, int count) =>
        LocalSlots(frame).Select(l => l.Name).Concat(HoistedNames(frame)).Skip(start).Take(count)
            .Select(name => ReadValue(name, () => Resolve(frame, name), Identifier(name))).ToArray();

    private CorDebugObjectValue? StateMachine(CorDebugILFrame frame) =>
        Method(frame)?.Name is { } name && name.StartsWith("VB$StateMachine_", StringComparison.Ordinal) &&
        name.EndsWith(".MoveNext", StringComparison.Ordinal) ? ValueAccess.Dereference(frame.GetArgument(0)) as CorDebugObjectValue : null;

    private static string? HoistedName(string field)
    {
        const string local = "$VB$Local_", resumable = "$VB$ResumableLocal_";
        if (field.StartsWith(local, StringComparison.Ordinal)) return field.Substring(local.Length);
        if (field.StartsWith(resumable, StringComparison.Ordinal))
        {
            int suffix = field.LastIndexOf('$');
            if (suffix > resumable.Length && field.Substring(suffix + 1).All(char.IsDigit))
                return field.Substring(resumable.Length, suffix - resumable.Length);
        }
        return null;
    }

    private string[] HoistedNames(CorDebugILFrame frame)
    {
        var machine = StateMachine(frame);
        if (machine == null) return Array.Empty<string>();
        var locals = new HashSet<string>(LocalSlots(frame).Select(l => l.Name), StringComparer.OrdinalIgnoreCase);
        return ValueAccess.Fields(machine).Select(f => HoistedName(f.Name)).Where(n => n != null && !locals.Contains(n))
            .Select(n => n!).GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() == 1).Select(g => g.Key).ToArray();
    }

    private Variable[] Arguments(CorDebugILFrame frame, int start, int count)
    {
        var method = Method(frame);
        if (method != null)
        {
            var names = method.IsStatic ? method.Arguments : new[] { "Me" }.Concat(method.Arguments).ToArray();
            return names.Select((name, i) => (name, i)).Skip(start).Take(count)
                .Select(p => ReadValue(p.name, () => Resolve(frame, p.name), Identifier(p.name))).ToArray();
        }
        var args = frame.Arguments;
        return args.Select((value, i) => (value, i)).Skip(start).Take(count)
            .Select(p => ReadValue($"arg{p.i}", () => p.value)).ToArray();
    }

    public object Variables(int reference, int start = 0, int count = 0, string? filter = null)
    {
        RequireStopped();
        if (!variables.TryGetValue(reference, out var children))
            throw new InvalidOperationException("Unknown or expired variable reference.");
        return new
        {
            variables = filter != null && (filter == "indexed") != children.Indexed
            ? Array.Empty<object>() : children.Get(start, count == 0 ? 100 : Math.Min(count, 100))
        };
    }

    public object Evaluate(string expression, int? frameId)
    {
        RequireStopped();
        if (frameId == null)
            throw new InvalidOperationException("Select a stack frame to evaluate a variable.");
        if (!frames.TryGetValue(frameId.Value, out var frame))
            throw new InvalidOperationException("Unknown or expired stack frame.");
        var evaluated = new ValueExpression(expression, name => Resolve(frame, name), binaryStringComparison).Read();
        var value = evaluated.Raw != null ? ReadValue(expression, () => evaluated.Raw, $"({expression})") : ConstantValue(expression, evaluated.Primitive);
        return new { result = value.value, value.type, value.variablesReference, value.indexedVariables, value.namedVariables };
    }

    private static Variable ConstantValue(string name, object? value) => new(name, value switch
    {
        null => "Nothing",
        bool boolean => boolean ? "True" : "False",
        string text => System.Text.Json.JsonSerializer.Serialize(text),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)!
    }, type: value?.GetType().Name);

    private CorDebugValue Resolve(CorDebugILFrame frame, string name)
    {
        var method = Method(frame) ?? throw new InvalidOperationException("No symbols for the selected frame.");
        int ip = frame.IP.pnOffset;
        var local = method.Locals.Where(l => ip >= l.Start && ip < l.End &&
                string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)).OrderByDescending(l => l.Start).ToArray();
        if (local.Length > 0) return frame.GetLocalVariable(local[0].Slot);
        var machine = StateMachine(frame);
        if (machine != null)
        {
            var matches = ValueAccess.Fields(machine).Where(f => string.Equals(
                name.Equals("Me", StringComparison.OrdinalIgnoreCase) && f.Name == "$VB$Me" ? "Me" : HoistedName(f.Name),
                name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1)
                throw new NotSupportedException($"Multiple hoisted fields match '{name}'; async lexical scope cannot be reconstructed.");
            if (matches.Length == 1) return machine.GetFieldValue(matches[0].Class.Raw, matches[0].Token);
        }
        if (!method.IsStatic && name.Equals("Me", StringComparison.OrdinalIgnoreCase)) return frame.GetArgument(0);
        int index = Array.FindIndex(method.Arguments, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) return frame.GetArgument(index + (method.IsStatic ? 0 : 1));
        // Start at the frame's declaring type so a derived instance cannot hide its fields.
        if (machine == null && !method.IsStatic &&
            ValueAccess.Dereference(frame.GetArgument(0)) is CorDebugObjectValue instance &&
            ValueAccess.FindField(instance, name, frame.Function.Class) is { } field)
            return field;
        throw new InvalidOperationException($"'{name}' is not a local or argument in the selected stack frame or an instance field on Me; properties and method calls are not evaluated.");
    }

    private int Store(Func<int, int, object[]> get, bool indexed)
    {
        if (variables.Count >= 8192) throw new InvalidOperationException("Variable handle limit reached for this stop; resume before expanding more values.");
        int id = ++nextId;
        variables.Add(id, (get, indexed));
        return id;
    }

    private static string? Identifier(string name) => name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') &&
        name.All(c => char.IsLetterOrDigit(c) || c == '_') ? $"[{name}]" : null;

    private Variable ReadValue(string name, Func<CorDebugValue> get, string? evaluateName = null)
    {
        var value = ReadValueCore(name, get, evaluateName);
        return new Variable(value.name, value.value, value.variablesReference, value.type, value.indexedVariables,
            value.namedVariables, evaluateName);
    }

    private Variable ReadValueCore(string name, Func<CorDebugValue> get, string? evaluateName)
    {
        try
        {
            var value = ValueAccess.Dereference(get());
            if (value is CorDebugReferenceValue reference && reference.IsNull) return new Variable(name, "Nothing");
            if (value is CorDebugStringValue text)
            {
                const int limit = 1024;
                int length = text.Length;
                string contents = length > limit ? $"<String of {length} characters>" :
                    System.Text.Json.JsonSerializer.Serialize(text.GetString(length + 1));
                return new Variable(name, contents, type: "String");
            }
            if (value is CorDebugArrayValue array)
            {
                var dimensions = array.GetDimensions(array.Rank);
                var lower = ValueAccess.LowerBounds(array);
                return new Variable(name, $"Array ({array.Count} elements)", Store((start, count) =>
                    Enumerable.Range(Math.Min(start, array.Count), Math.Min(count, Math.Max(0, array.Count - start)))
                        .Select(i => ReadValue($"({string.Join(", ", ValueAccess.Indices(i, dimensions, lower))})",
                            () => array.GetElementAtPosition(i), evaluateName == null ? null :
                                $"{evaluateName}({string.Join(", ", ValueAccess.Indices(i, dimensions, lower))})")).ToArray(), true), indexedVariables: array.Count);
            }
            if (value is CorDebugObjectValue instance)
            {
                var fields = ValueAccess.Fields(instance);
                string typeName = ValueAccess.TypeName(instance.Class);
                if (typeName == "System.Decimal")
                {
                    int Part(string fieldName)
                    {
                        var field = fields.Single(f => f.Name == fieldName);
                        return (int)ValueAccess.ScalarBits((CorDebugGenericValue)ValueAccess.Dereference(
                            instance.GetFieldValue(field.Class.Raw, field.Token)));
                    }
                    var number = new decimal(new[] { Part("lo"), Part("mid"), Part("hi"), Part("flags") });
                    return new Variable(name, number.ToString(CultureInfo.InvariantCulture), type: typeName);
                }
                var enumValue = fields.FirstOrDefault(f => f.Name == "value__");
                if (enumValue != null)
                {
                    string numeric = Scalar((CorDebugGenericValue)ValueAccess.Dereference(
                        instance.GetFieldValue(enumValue.Class.Raw, enumValue.Token)));
                    string? label = ValueAccess.EnumName(instance.Class, numeric);
                    return new Variable(name, label == null ? numeric : $"{label} ({numeric})", type: typeName);
                }
                if (typeName is "System.Boolean" or "System.Char" or "System.SByte" or "System.Byte" or
                    "System.Int16" or "System.UInt16" or "System.Int32" or "System.UInt32" or
                    "System.Int64" or "System.UInt64" or "System.Single" or "System.Double")
                {
                    var contents = fields.Single(f => f.Name == "m_value");
                    return ReadValue(name, () => instance.GetFieldValue(contents.Class.Raw, contents.Token));
                }
                return new Variable(name, $"{{{typeName}}}", fields.Count == 0 ? 0 :
                    Store((start, count) => fields.Skip(start).Take(count)
                        .Select(f => ReadValue(f.Name, () => instance.GetFieldValue(f.Class.Raw, f.Token),
                            evaluateName != null && Identifier(f.Name) is { } member ? $"{evaluateName}.{member}" : null)).ToArray(), false),
                    typeName, namedVariables: fields.Count);
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
        long bits = ValueAccess.ScalarBits(value);
        object display = value.Type switch
        {
            CorElementType.Boolean => bits == 0 ? "False" : "True",
            CorElementType.Char => (char)bits,
            CorElementType.I1 => (sbyte)bits,
            CorElementType.U1 => (byte)bits,
            CorElementType.I2 => (short)bits,
            CorElementType.U2 => (ushort)bits,
            CorElementType.I4 => (int)bits,
            CorElementType.U4 => (uint)bits,
            CorElementType.I8 => bits,
            CorElementType.U8 => (ulong)bits,
            CorElementType.R4 => BitConverter.ToSingle(BitConverter.GetBytes((int)bits), 0),
            CorElementType.R8 => BitConverter.Int64BitsToDouble(bits),
            _ => $"<{value.Type}>"
        };
        return Convert.ToString(display, CultureInfo.InvariantCulture)!;
    }

    public void Resume(int threadId, string command)
    {
        var thread = Thread(threadId);
        stepper?.Deactivate();
        stepper = null;
        if (command != "continue")
        {
            var frame = thread.ActiveFrame as CorDebugILFrame ?? throw new InvalidOperationException("No managed frame to step.");
            sourceStepRetries = 0;
            StartStep(frame, Method(frame), command == "stepIn", command == "stepOut");
        }
        RefreshBreakpoints(true);
        stopped!.Continue(false);
        stopped = null;
        InvalidateHandles();
        emit("continued", new { threadId, allThreadsContinued = true });
    }

    private void CompleteStep(StepCompleteCorDebugManagedCallbackEventArgs step)
    {
        stepper = null;
        try
        {
            if (step.Thread.ActiveFrame is CorDebugILFrame frame)
            {
                var method = Method(frame);
                if (VisiblePoint(method, frame.IP.pnOffset) == null)
                {
                    if (sourceStepRetries++ < 128)
                    {
                        // StepOut returns to the caller; a hidden region in a mapped method
                        // can instead be stepped over without abandoning the method.
                        // https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebugstepper-stepout-method
                        StartStep(frame, method, stepIn: false, stepOut: method?.Points.Any(p => !p.Hidden) != true);
                        return;
                    }
                    Diagnostic("Stepping stopped after 128 attempts to find a mapped source line.");
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or DebugException)
        {
            Diagnostic($"Could not step to a mapped source line: {ex.Message}");
        }
        Stop(step, step.Thread, "step");
    }

    private void StartStep(CorDebugILFrame frame, MethodSymbols? method, bool stepIn, bool stepOut)
    {
        var next = frame.CreateStepper();
        next.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
        if (stepOut) next.StepOut();
        else
        {
            int ip = frame.IP.pnOffset;
            var point = method?.At(ip);
            if (point == null) next.Step(stepIn);
            else
            {
                int end = method!.Points.FirstOrDefault(p => !p.Hidden && p.Offset > ip &&
                    (point.Hidden || p.Line != point.Line || p.Path != point.Path))?.Offset ?? frame.Code.Size;
                if (end > ip)
                    next.StepRange(stepIn, new[] { new COR_DEBUG_STEP_RANGE
                        { startOffset = point.Offset, endOffset = end } }, 1);
                else next.Step(stepIn);
            }
        }
        stepper = next;
    }

    private void InvalidateHandles()
    {
        frames.Clear();
        variables.Clear();
        exceptionStop = null;
    }

    public void Dispose()
    {
        bool wasStopped;
        lock (Gate)
        {
            shuttingDown = true;
            wasStopped = stopped != null;
        }
        bool terminate = terminateOnDisconnect ?? !attached;
        try
        {
            if (!terminate && process != null && !finished) Detach(wasStopped);
            if (processHandle != IntPtr.Zero && terminate)
            {
                if (WaitForSingleObject(processHandle, 0) == 258)
                {
                    IntPtr killHandle = attached ? OpenProcess(1, false, process!.Id) : processHandle;
                    try
                    {
                        if (killHandle == IntPtr.Zero || !TerminateProcess(killHandle, 0))
                            Console.Error.WriteLine($"Terminating debuggee failed: Win32 error {Marshal.GetLastWin32Error()}.");
                    }
                    finally { if (attached && killHandle != IntPtr.Zero) CloseHandle(killHandle); }
                }
                if (WaitForSingleObject(processHandle, 2000) != 0)
                    Console.Error.WriteLine("Timed out waiting for the debuggee to exit.");
            }
        }
        finally
        {
            if (processHandle != IntPtr.Zero) { CloseHandle(processHandle); processHandle = IntPtr.Zero; }
            CloseDebugger();
        }
    }

    private void Detach(bool wasStopped)
    {
        HRESULT result = wasStopped ? HRESULT.S_OK : process!.TryStop(0);
        if ((int)result >= 0)
        {
            lock (Gate)
            {
                if (!finished)
                {
                    stepper?.Deactivate();
                    stepper = null;
                    foreach (var breakpoint in breakpoints.Values.SelectMany(b => b).SelectMany(b => b.Handles.Values).SelectMany(h => h))
                        breakpoint.Activate(false);
                }
            }
            if (!finished) result = process!.TryDetach();
        }
        // A target can exit between Stop and Detach; verify the OS process, not just the CLR HRESULT.
        if ((int)result < 0)
        {
            if (WaitForSingleObject(processHandle, 2000) != 0)
                throw new InvalidOperationException($"Could not detach the live target: {result}.");
        }
        lock (Gate)
        {
            stopped = null;
            InvalidateHandles();
            if (!finished) { finished = true; emit("terminated", new { }); }
        }
    }

    private void CloseDebugger()
    {
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
            debugger = null;
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
    private static extern bool GetExitCodeProcess(IntPtr handle, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr handle, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(IntPtr handle, [MarshalAs(UnmanagedType.Bool)] out bool wow64);
}
