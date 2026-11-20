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
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException)
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
    private (int Seq, string Program, string Cwd, string[] Args)? pendingLaunch;

    public void Run()
    {
        using var session = new DebugSession(Event);
        while (Read() is { } document)
        using (document)
        {
            var request = document.RootElement;
            if (request.GetProperty("type").GetString() != "request") continue;
            int seq = request.GetProperty("seq").GetInt32();
            string command = request.GetProperty("command").GetString()!;
            var args = request.TryGetProperty("arguments", out var a) ? a : default;
            if (command == "setBreakpoints")
            {
                try
                {
                    var specs = args.TryGetProperty("breakpoints", out var points)
                        ? points.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
                    if (specs.Any(p => p.TryGetProperty("condition", out _) ||
                                       p.TryGetProperty("hitCondition", out _) || p.TryGetProperty("logMessage", out _)))
                        throw new NotSupportedException("Conditional breakpoints and logpoints are not supported.");
                    Respond(seq, command, session.SetBreakpoints(args.GetProperty("source").GetProperty("path").GetString()!,
                        specs.Select(p => p.GetProperty("line").GetInt32()).ToArray()));
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
                            Respond(seq, command, new { supportsConfigurationDoneRequest = true, supportsEvaluateForHovers = true });
                            break;
                        case "launch":
                            if (!initialized || configured || pendingLaunch != null)
                                throw new InvalidOperationException("Expected a single launch after initialize.");
                            if (args.TryGetProperty("noDebug", out var noDebug) && noDebug.GetBoolean())
                                throw new NotSupportedException("Run Without Debugging is not supported.");
                            string program = Path.GetFullPath(args.GetProperty("program").GetString()!);
                            string cwd = args.TryGetProperty("cwd", out var dir)
                                ? Path.GetFullPath(dir.GetString()!) : Path.GetDirectoryName(program)!;
                            string[] argv = args.TryGetProperty("args", out var list)
                                ? list.EnumerateArray().Select(v => v.GetString() ?? throw new ArgumentException("Expected a string argument.")).ToArray()
                                : Array.Empty<string>();
                            pendingLaunch = (seq, program, cwd, argv);
                            Event("initialized", new { });
                            break;
                        case "configurationDone":
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
                            if (args.GetProperty("filters").GetArrayLength() != 0)
                                throw new NotSupportedException("Exception breakpoints are not supported.");
                            Respond(seq, command);
                            break;
                        case "threads": Respond(seq, command, session.Threads()); break;
                        case "stackTrace":
                            Respond(seq, command, session.Stack(Int("threadId"), OptionalInt("startFrame"), OptionalInt("levels")));
                            break;
                        case "scopes": Respond(seq, command, session.Scopes(Int("frameId"))); break;
                        case "variables": Respond(seq, command, session.Variables(Int("variablesReference"))); break;
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

    private static bool IsRequestError(Exception ex) => ex is IOException or UnauthorizedAccessException or
        InvalidOperationException or InvalidDataException or ArgumentException or FormatException or NotSupportedException or
        KeyNotFoundException or COMException or ClrDebug.DebugException or ClrDebug.HostingException or BadImageFormatException;

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

    private void Event(string name, object body) => Write(seq => new { seq, type = "event", @event = name, body });

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
