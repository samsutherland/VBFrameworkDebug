using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopClr;

internal static class Program
{
    [MTAThread]
    private static int Main()
    {
        try { new Protocol(Console.OpenStandardInput(), Console.OpenStandardOutput()).Run(); return 0; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException or
            FormatException or COMException or ClrDebug.DebugException or ClrDebug.HostingException)
        {
            Console.Error.WriteLine($"Desktop CLR adapter: {ex.Message}");
            return 1;
        }
    }
}

internal sealed class Protocol(Stream input, Stream output)
{
    private readonly object outputGate = new();
    private readonly JsonSerializerOptions json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private int sequence;
    private bool initialized;
    private bool configured;
    private bool diagnostics;
    private (int Seq, string Program, string Cwd, string[] Args)? pendingLaunch;
    private (int Seq, int ProcessId)? pendingAttach;

    public void Run()
    {
        using var session = new DebugSession(Event);
        while (Read() is { } document)
            using (document)
            {
                var request = document.RootElement;
                if (request.ValueKind != JsonValueKind.Object ||
                    !request.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "request" ||
                    !request.TryGetProperty("seq", out var seqValue) || !seqValue.TryGetInt32(out int seq) || seq < 1 ||
                    !request.TryGetProperty("command", out var commandValue) || commandValue.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(commandValue.GetString()))
                    throw new InvalidDataException("Malformed DAP request envelope; expected type, positive seq, and command.");
                string command = commandValue.GetString()!;
                var args = request.TryGetProperty("arguments", out var a) ? a : JsonSerializer.SerializeToElement(new { });
                try
                {
                    if (!initialized && command is not ("initialize" or "disconnect"))
                        throw new InvalidOperationException("Initialize the adapter before sending requests.");
                    ValidateArguments(command, args);
                    if (command is "launch" or "attach")
                        diagnostics = args.TryGetProperty("diagnostics", out var trace) && trace.GetBoolean();
                    if (command is "initialize" or "launch" or "attach" or "configurationDone" or "setBreakpoints" or
                        "setExceptionBreakpoints" or "exceptionInfo" or "threads" or "modules" or "breakpointLocations" or
                        "stackTrace" or "scopes" or "variables" or "evaluate" or "continue" or "next" or "stepIn" or
                        "stepOut" or "disconnect" or "pause")
                        Trace("request " + command);
                }
                catch (Exception ex) when (IsRequestError(ex))
                {
                    Respond(seq, command, error: ex.Message);
                    continue;
                }
                if (command == "pause")
                {
                    try { session.Pause(); Respond(seq, command); }
                    catch (Exception ex) when (IsRequestError(ex)) { Respond(seq, command, error: ex.Message); }
                    continue;
                }
                if (command == "setBreakpoints")
                {
                    try
                    {
                        var specs = args.TryGetProperty("breakpoints", out var points)
                            ? points.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
                        string? Option(JsonElement spec, string name) => spec.TryGetProperty(name, out var value) ? value.GetString() : null;
                        Respond(seq, command, session.SetBreakpoints(args.GetProperty("source").GetProperty("path").GetString()!,
                            specs.Select(p => new BreakpointRequest(p.GetProperty("line").GetInt32(),
                                Option(p, "condition"), Option(p, "hitCondition"), Option(p, "logMessage"))).ToArray()));
                    }
                    catch (Exception ex) when (IsRequestError(ex)) { Respond(seq, command, error: ex.Message); }
                    continue;
                }
                lock (session.Gate)
                {
                    try
                    {
                        switch (command)
                        {
                            case "initialize":
                                if (initialized) throw new InvalidOperationException("The adapter is already initialized.");
                                if ((args.TryGetProperty("linesStartAt1", out var lines) && !lines.GetBoolean()) ||
                                    (args.TryGetProperty("columnsStartAt1", out var columns) && !columns.GetBoolean()) ||
                                    (args.TryGetProperty("pathFormat", out var paths) && paths.GetString() != "path"))
                                    throw new NotSupportedException("This adapter requires native paths and one-based lines and columns.");
                                initialized = true;
                                Respond(seq, command, new
                                {
                                    supportsConfigurationDoneRequest = true,
                                    supportsEvaluateForHovers = true,
                                    supportsExceptionInfoRequest = true,
                                    supportTerminateDebuggee = true,
                                    supportsModulesRequest = true,
                                    supportsConditionalBreakpoints = true,
                                    supportsHitConditionalBreakpoints = true,
                                    supportsLogPoints = true,
                                    supportsBreakpointLocationsRequest = true,
                                    exceptionBreakpointFilters = new[] {
                                    new { filter = "unhandled", label = "Unhandled CLR exceptions", @default = true },
                                    new { filter = "thrown", label = "All thrown CLR exceptions", @default = false }
                                }
                                });
                                break;
                            case "launch":
                                if (!initialized || configured || pendingLaunch != null || pendingAttach != null)
                                    throw new InvalidOperationException("Expected a single launch after initialize.");
                                if (args.TryGetProperty("noDebug", out var noDebug) && noDebug.GetBoolean())
                                    throw new NotSupportedException("Run Without Debugging is not supported.");
                                session.SetSourceMap(SourceMap(args));
                                session.SetStringComparison(args.TryGetProperty("expressionStringComparison", out var comparison) ? comparison.GetString() : null);
                                string program = Path.GetFullPath(args.GetProperty("program").GetString()!);
                                string cwd = args.TryGetProperty("cwd", out var dir)
                                    ? Path.GetFullPath(dir.GetString()!) : Path.GetDirectoryName(program)!;
                                string[] argv = args.TryGetProperty("args", out var list)
                                    ? list.EnumerateArray().Select(v => v.GetString() ?? throw new ArgumentException("Expected a string argument.")).ToArray()
                                    : Array.Empty<string>();
                                pendingLaunch = (seq, program, cwd, argv);
                                Event("initialized", new { });
                                break;
                            case "attach":
                                if (configured || pendingLaunch != null || pendingAttach != null)
                                    throw new InvalidOperationException("Expected a single launch or attach after initialize.");
                                session.SetSourceMap(SourceMap(args));
                                session.SetStringComparison(args.TryGetProperty("expressionStringComparison", out var attachComparison) ? attachComparison.GetString() : null);
                                pendingAttach = (seq, Int("processId"));
                                Event("initialized", new { });
                                break;
                            case "configurationDone":
                                if (pendingAttach is { } attach)
                                {
                                    pendingAttach = null;
                                    configured = true;
                                    Respond(seq, command);
                                    try { session.Attach(attach.ProcessId); Respond(attach.Seq, "attach"); }
                                    catch (Exception ex) when (IsRequestError(ex))
                                    {
                                        Respond(attach.Seq, "attach", error: ex.Message);
                                        return;
                                    }
                                    break;
                                }
                                if (pendingLaunch is not { } launch)
                                    throw new InvalidOperationException("There is no pending launch.");
                                pendingLaunch = null;
                                configured = true;
                                Respond(seq, command);
                                try { session.Launch(launch.Program, launch.Cwd, launch.Args); Respond(launch.Seq, "launch"); }
                                catch (Exception ex) when (IsRequestError(ex))
                                {
                                    Respond(launch.Seq, "launch", error: ex.Message);
                                    return;
                                }
                                break;
                            case "setExceptionBreakpoints":
                                session.SetExceptionBreakpoints(args.GetProperty("filters").EnumerateArray()
                                    .Select(f => f.GetString() ?? throw new ArgumentException("Exception filters must be strings.")).ToArray());
                                Respond(seq, command);
                                break;
                            case "exceptionInfo": Respond(seq, command, session.ExceptionInfo(Int("threadId"))); break;
                            case "threads": Respond(seq, command, session.Threads()); break;
                            case "modules": Respond(seq, command, session.Modules(OptionalInt("startModule"), OptionalInt("moduleCount"))); break;
                            case "breakpointLocations":
                                Respond(seq, command, session.BreakpointLocations(args.GetProperty("source").GetProperty("path").GetString()!,
                                    Int("line"), args.TryGetProperty("endLine", out var endLine) ? endLine.GetInt32() : Int("line")));
                                break;
                            case "stackTrace":
                                Respond(seq, command, session.Stack(Int("threadId"), OptionalInt("startFrame"), OptionalInt("levels")));
                                break;
                            case "scopes": Respond(seq, command, session.Scopes(Int("frameId"))); break;
                            case "variables":
                                Respond(seq, command, session.Variables(Int("variablesReference"), OptionalInt("start"), OptionalInt("count"),
                                    args.TryGetProperty("filter", out var filter) ? filter.GetString() : null));
                                break;
                            case "evaluate":
                                Respond(seq, command, session.Evaluate(args.GetProperty("expression").GetString()!,
                                    args.TryGetProperty("frameId", out var frameId) ? frameId.GetInt32() : (int?)null));
                                break;
                            case "continue":
                            case "next":
                            case "stepIn":
                            case "stepOut":
                                int threadId = Int("threadId");
                                session.Resume(threadId, command);
                                Respond(seq, command, command == "continue" ? new { allThreadsContinued = true } : null);
                                break;
                            case "disconnect":
                                if (pendingLaunch is { } cancelled)
                                    Respond(cancelled.Seq, "launch", error: "Launch cancelled by disconnect.");
                                if (pendingAttach is { } cancelledAttach)
                                    Respond(cancelledAttach.Seq, "attach", error: "Attach cancelled by disconnect.");
                                session.Disconnect(args.TryGetProperty("terminateDebuggee", out var terminate) ? terminate.GetBoolean() : (bool?)null);
                                Respond(seq, command);
                                return;
                            default: throw new NotSupportedException($"Unsupported request: {command}.");
                        }
                    }
                    catch (Exception ex) when (IsRequestError(ex)) { Respond(seq, command, error: ex.Message); }
                }

                int Int(string name) => args.GetProperty(name).GetInt32();
                int OptionalInt(string name) => args.TryGetProperty(name, out var value) ? value.GetInt32() : 0;
            }
    }

    private static void ValidateArguments(string command, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Request arguments must be an object.");
        switch (command)
        {
            case "modules":
                OptionalNumber("startModule");
                OptionalNumber("moduleCount");
                break;
            case "attach":
                Number(args, "processId", 1);
                ValidateDiagnostics();
                break;
            case "launch":
                ValidateDiagnostics();
                Text(args, "program");
                if (args.TryGetProperty("cwd", out _)) Text(args, "cwd");
                if (args.TryGetProperty("args", out var argv) &&
                    (argv.ValueKind != JsonValueKind.Array || argv.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String)))
                    throw new ArgumentException("'args' must be an array of strings.");
                break;
            case "setBreakpoints":
                Text(args.GetProperty("source"), "path");
                if (args.TryGetProperty("breakpoints", out var points))
                {
                    if (points.ValueKind != JsonValueKind.Array) throw new ArgumentException("'breakpoints' must be an array.");
                    foreach (var point in points.EnumerateArray()) Number(point, "line", 1);
                }
                break;
            case "breakpointLocations":
                Text(args.GetProperty("source"), "path");
                Number(args, "line", 1);
                if (args.TryGetProperty("endLine", out _)) Number(args, "endLine", 1);
                break;
            case "stackTrace":
                Number(args, "threadId", 1);
                OptionalNumber("startFrame");
                OptionalNumber("levels");
                break;
            case "scopes": Number(args, "frameId", 1); break;
            case "variables":
                Number(args, "variablesReference", 1);
                OptionalNumber("start");
                OptionalNumber("count");
                if (args.TryGetProperty("filter", out var filter) && filter.GetString() is not ("indexed" or "named"))
                    throw new ArgumentException("Variable filter must be 'indexed' or 'named'.");
                break;
            case "evaluate":
                Text(args, "expression");
                if (args.TryGetProperty("frameId", out _)) Number(args, "frameId", 1);
                break;
            case "continue":
            case "next":
            case "stepIn":
            case "stepOut":
                Number(args, "threadId", 1);
                if (args.TryGetProperty("singleThread", out var single) && single.GetBoolean())
                    throw new NotSupportedException("Single-thread execution is not supported; this debugger uses all-stop mode.");
                if (args.TryGetProperty("granularity", out var granularity) && granularity.GetString() != "statement")
                    throw new NotSupportedException("Only statement stepping is supported.");
                break;
        }

        void OptionalNumber(string name) { if (args.TryGetProperty(name, out _)) Number(args, name, 0); }
        void ValidateDiagnostics()
        {
            if (args.TryGetProperty("diagnostics", out var value) && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("'diagnostics' must be Boolean.");
        }
    }

    private static void Text(JsonElement args, string name)
    {
        var value = args.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ArgumentException($"'{name}' must be a nonempty string.");
    }

    private static void Number(JsonElement args, string name, int minimum)
    {
        var value = args.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number) || number < minimum)
            throw new ArgumentException($"'{name}' must be an integer >= {minimum}.");
    }

    private static Dictionary<string, string> SourceMap(JsonElement args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!args.TryGetProperty("sourceFileMap", out var mapping)) return result;
        if (mapping.ValueKind != JsonValueKind.Object) throw new ArgumentException("sourceFileMap must be an object.");
        foreach (var entry in mapping.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entry.Value.GetString()))
                throw new ArgumentException("Source map values must be nonempty paths.");
            result.Add(entry.Name, entry.Value.GetString()!);
        }
        return result;
    }

    private static bool IsRequestError(Exception ex) => ex is IOException or UnauthorizedAccessException or
        InvalidOperationException or InvalidDataException or ArgumentException or FormatException or NotSupportedException or
        KeyNotFoundException or COMException or ClrDebug.DebugException or ClrDebug.HostingException or BadImageFormatException or
        ArithmeticException;

    private JsonDocument? Read()
    {
        var header = new StringBuilder();
        while (true)
        {
            int next = input.ReadByte();
            if (next < 0)
            {
                if (header.Length == 0) return null;
                throw new EndOfStreamException("Incomplete DAP header.");
            }
            header.Append((char)next);
            int n = header.Length;
            if (n >= 4 && header[n - 4] == '\r' && header[n - 3] == '\n' &&
                header[n - 2] == '\r' && header[n - 1] == '\n') break;
            if (n > 8192) throw new InvalidDataException("DAP header is too large.");
        }
        const string prefix = "Content-Length:";
        var lengths = header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (lengths.Length != 1 || !int.TryParse(lengths[0].Substring(prefix.Length), out int length) ||
            length <= 0 || length > 4 * 1024 * 1024)
            throw new InvalidDataException("Invalid DAP Content-Length.");
        var bytes = new byte[length];
        for (int read = 0; read < bytes.Length;)
        {
            int amount = input.Read(bytes, read, bytes.Length - read);
            if (amount == 0) throw new EndOfStreamException("Incomplete DAP payload.");
            read += amount;
        }
        return JsonDocument.Parse(bytes);
    }

    private void Event(string name, object body)
    {
        if (diagnostics && name is "module" or "stopped")
        {
            string detail = name == "module" ? "module status changed" :
                "stopped " + JsonSerializer.SerializeToElement(body).GetProperty("reason").GetString();
            Trace(detail);
        }
        Write(seq => new { seq, type = "event", @event = name, body });
    }

    private void Trace(string text)
    {
        if (diagnostics)
            Write(seq => new
            {
                seq,
                type = "event",
                @event = "output",
                body = new { category = "stderr", output = "[desktopclr] " + text + "\n" }
            });
    }

    private void Respond(int request, string command, object? body = null, string? error = null) =>
        Write(seq => new { seq, type = "response", request_seq = request, command, success = error == null, body, message = error });

    private void Write(Func<int, object> message)
    {
        lock (outputGate)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(message(++sequence), json);
            var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
            output.Write(header, 0, header.Length);
            output.Write(payload, 0, payload.Length);
            output.Flush();
        }
    }
}
