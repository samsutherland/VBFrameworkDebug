using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace DesktopClr;

internal sealed class SourcePoint(string path, int line, int column, int token, int offset)
{
    public string Path { get; } = path;
    public int Line { get; } = line;
    public int Column { get; } = column;
    public int Token { get; } = token;
    public int Offset { get; } = offset;
    public bool Hidden => Line == 0xFEEFEE;
}

internal sealed class MethodSymbols(string name, string[] arguments, bool isStatic)
{
    public string Name { get; } = name;
    public string[] Arguments { get; } = arguments;
    public bool IsStatic { get; } = isStatic;
    public List<SourcePoint> Points { get; } = new();
    public List<(int Start, int End, int Slot, string Name)> Locals { get; } = new();

    public SourcePoint? At(int offset) => Points.LastOrDefault(p => p.Offset <= offset);
}

internal static class Symbols
{
    public static Dictionary<int, MethodSymbols> Read(string module)
    {
        using var pe = new PEReader(File.OpenRead(module));
        var metadata = pe.GetMetadataReader();
        string path = Path.ChangeExtension(module, ".pdb");
        var debugDirectory = pe.ReadDebugDirectory();
        if (!debugDirectory.Any(entry => entry.IsPortableCodeView))
            return ReadWindows(module, path);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(path));
        var pdb = provider.GetMetadataReader();
        var id = new BlobContentId(pdb.DebugMetadataHeader!.Id);
        if (!debugDirectory.Any(entry => entry.IsPortableCodeView &&
                entry.Stamp == id.Stamp && pe.ReadCodeViewDebugDirectoryData(entry).Guid == id.Guid))
            throw new InvalidDataException($"PDB does not match {Path.GetFileName(module)}.");

        var methods = new Dictionary<int, MethodSymbols>();
        foreach (var handle in metadata.MethodDefinitions)
        {
            var definition = metadata.GetMethodDefinition(handle);
            var type = metadata.GetTypeDefinition(definition.GetDeclaringType());
            int token = MetadataTokens.GetToken(handle);
            var method = new MethodSymbols($"{metadata.GetString(type.Name)}.{metadata.GetString(definition.Name)}",
                definition.GetParameters().Select(metadata.GetParameter).Where(p => p.SequenceNumber > 0)
                    .OrderBy(p => p.SequenceNumber).Select(p => metadata.GetString(p.Name)).ToArray(),
                definition.Attributes.HasFlag(System.Reflection.MethodAttributes.Static));
            var info = pdb.GetMethodDebugInformation(handle);
            foreach (var point in info.GetSequencePoints())
            {
                var doc = point.Document.IsNil ? info.Document : point.Document;
                if (!doc.IsNil)
                    method.Points.Add(new SourcePoint(pdb.GetString(pdb.GetDocument(doc).Name),
                        point.StartLine, point.StartColumn, token, point.Offset));
            }
            foreach (var scopeHandle in pdb.GetLocalScopes(handle))
            {
                var scope = pdb.GetLocalScope(scopeHandle);
                foreach (var localHandle in scope.GetLocalVariables())
                {
                    var local = pdb.GetLocalVariable(localHandle);
                    if ((local.Attributes & LocalVariableAttributes.DebuggerHidden) == 0)
                        method.Locals.Add((scope.StartOffset, scope.EndOffset, local.Index, pdb.GetString(local.Name)));
                }
            }
            methods.Add(token, method);
        }
        return methods;
    }

    private static Dictionary<int, MethodSymbols> ReadWindows(string module, string path)
    {
        var options = new dnlib.DotNet.ModuleCreationOptions
        {
            PdbFileOrData = path,
            PdbOptions = dnlib.DotNet.Pdb.PdbReaderOptions.None
        };
        using var image = dnlib.DotNet.ModuleDefMD.Load(module, options);
        if (image.PdbState == null)
            throw new InvalidDataException($"PDB does not match {Path.GetFileName(module)} or could not be read.");
        var methods = new Dictionary<int, MethodSymbols>();
        foreach (var type in image.GetTypes())
        foreach (var definition in type.Methods)
        {
            if (definition.Body is not { PdbMethod: { } pdb } body) continue;
            int token = definition.MDToken.ToInt32();
            var method = new MethodSymbols($"{type.Name}.{definition.Name}", definition.ParamDefs
                .Where(p => p.Sequence > 0).OrderBy(p => p.Sequence).Select(p => p.Name.String).ToArray(), definition.IsStatic);
            foreach (var instruction in body.Instructions)
                if (instruction.SequencePoint is { Document: { } doc } point)
                    method.Points.Add(new SourcePoint(doc.Url, point.StartLine, point.StartColumn, token, (int)instruction.Offset));
            int end = body.Instructions.Count == 0 ? 0 : (int)body.Instructions.Last().Offset + body.Instructions.Last().GetSize();
            AddScope(pdb.Scope);
            methods.Add(token, method);

            void AddScope(dnlib.DotNet.Pdb.PdbScope scope)
            {
                int startOffset = (int)(scope.Start?.Offset ?? 0);
                int endOffset = scope.End == null ? end : (int)scope.End.Offset;
                foreach (var local in scope.Variables)
                    if ((local.Attributes & dnlib.DotNet.Pdb.PdbLocalAttributes.DebuggerHidden) == 0)
                        method.Locals.Add((startOffset, endOffset, local.Index, local.Name));
                foreach (var child in scope.Scopes) AddScope(child);
            }
        }
        return methods;
    }
}
