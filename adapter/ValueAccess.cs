using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using ClrDebug;

namespace DesktopClr;

internal static class ValueAccess
{
    public static object? Primitive(CorDebugValue raw)
    {
        var value = Dereference(raw);
        if (value is CorDebugReferenceValue reference && reference.IsNull) return null;
        if (value is CorDebugStringValue text)
            return text.Length <= 4096 ? text.GetString(text.Length + 1) :
                throw new NotSupportedException("Expression string values are limited to 4096 characters.");
        if (value is CorDebugGenericValue scalar)
        {
            long bits = ScalarBits(scalar);
            return scalar.Type switch
            {
                CorElementType.Boolean => bits != 0,
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
                _ => throw new NotSupportedException($"Unsupported expression type: {scalar.Type}.")
            };
        }
        if (value is CorDebugObjectValue instance)
        {
            string name = TypeName(instance.Class);
            var fields = Fields(instance);
            if (name == "System.Decimal")
            {
                int Part(string fieldName)
                {
                    var field = fields.Single(f => f.Name == fieldName);
                    return (int)ScalarBits((CorDebugGenericValue)Dereference(instance.GetFieldValue(field.Class.Raw, field.Token)));
                }
                return new decimal(new[] { Part("lo"), Part("mid"), Part("hi"), Part("flags") });
            }
            if (name is "System.Boolean" or "System.Char" or "System.SByte" or "System.Byte" or "System.Int16" or
                "System.UInt16" or "System.Int32" or "System.UInt32" or "System.Int64" or "System.UInt64" or "System.Single" or "System.Double")
            {
                var contents = fields.Single(f => f.Name == "m_value");
                return Primitive(instance.GetFieldValue(contents.Class.Raw, contents.Token));
            }
        }
        throw new NotSupportedException("Object conversions and overloaded operators are not evaluated.");
    }

    public static long ScalarBits(CorDebugGenericValue value)
    {
        if (value.Size is <= 0 or > 8) throw new InvalidOperationException("Value is not a supported scalar.");
        IntPtr buffer = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt64(buffer, 0);
            value.GetValue(buffer);
            return Marshal.ReadInt64(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal sealed class FieldInfo(string name, CorDebugClass declaringClass, mdFieldDef token, string? propertyName)
    {
        public string Name { get; } = name;
        public CorDebugClass Class { get; } = declaringClass;
        public mdFieldDef Token { get; } = token;
        public string? PropertyName { get; } = propertyName;
    }

    public static CorDebugValue Dereference(CorDebugValue value)
    {
        for (int i = 0; i < 16; i++)
        {
            if (value is CorDebugReferenceValue reference)
            {
                if (reference.IsNull) return value;
                value = reference.Dereference();
            }
            else if (value is CorDebugBoxValue box) value = box.Object;
            else return value;
        }
        throw new InvalidOperationException("Reference indirection limit exceeded.");
    }

    public static List<FieldInfo> Fields(CorDebugObjectValue value) => Fields(value, null, includePropertyNames: false);

    private static List<FieldInfo> Fields(CorDebugObjectValue value, CorDebugClass? startAt, bool includePropertyNames)
    {
        var result = new List<FieldInfo>();
        var seen = new HashSet<string>();
        for (var type = value.ExactType; type != null; type = type.Base)
        {
            var cls = type.Class;
            if (startAt != null)
            {
                if (cls.Token != startAt.Token || !Equals(cls.Module.Raw, startAt.Module.Raw)) continue;
                startAt = null;
            }
            string path = cls.Module.Name;
            if (!File.Exists(path)) throw new FileNotFoundException($"Cannot inspect field metadata for {path}.");
            using var pe = new PEReader(File.OpenRead(path));
            var md = pe.GetMetadataReader();
            var definition = md.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.Handle((int)cls.Token));
            foreach (var handle in definition.GetFields())
            {
                var field = md.GetFieldDefinition(handle);
                if (field.Attributes.HasFlag(FieldAttributes.Static)) continue;
                string name = md.GetString(field.Name);
                // Preserve base fields even if a derived class hides one.
                string display = seen.Add(name.ToUpperInvariant()) ? name : $"{md.GetString(definition.Name)}.{name}";
                result.Add(new FieldInfo(display, cls, (mdFieldDef)MetadataTokens.GetToken(handle),
                    includePropertyNames ? BackingProperty(md, field) : null));
            }
        }
        return result;
    }

    private static string? BackingProperty(MetadataReader md, FieldDefinition field)
    {
        // VB WithEvents marks the field with the source-level property name.
        // https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.accessedthroughpropertyattribute
        foreach (var handle in field.GetCustomAttributes())
        {
            var attribute = md.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var parent = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
            if (parent.Kind != HandleKind.TypeReference) continue;
            var type = md.GetTypeReference((TypeReferenceHandle)parent);
            if (md.GetString(type.Namespace) != "System.Runtime.CompilerServices" ||
                md.GetString(type.Name) != "AccessedThroughPropertyAttribute") continue;
            var blob = md.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() == 1) return blob.ReadSerializedString();
        }
        return null;
    }

    public static string TypeName(CorDebugClass cls)
    {
        using var pe = new PEReader(File.OpenRead(cls.Module.Name));
        var md = pe.GetMetadataReader();
        var type = md.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.Handle((int)cls.Token));
        string ns = md.GetString(type.Namespace);
        return (ns.Length == 0 ? "" : ns + ".") + md.GetString(type.Name);
    }

    public static string? EnumName(CorDebugClass cls, string numeric)
    {
        using var pe = new PEReader(File.OpenRead(cls.Module.Name));
        var md = pe.GetMetadataReader();
        var type = md.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.Handle((int)cls.Token));
        foreach (var handle in type.GetFields())
        {
            var field = md.GetFieldDefinition(handle);
            if (!field.Attributes.HasFlag(FieldAttributes.Literal)) continue;
            var constantHandle = field.GetDefaultValue();
            if (constantHandle.IsNil) continue;
            var constant = md.GetConstant(constantHandle);
            object? constantValue = md.GetBlobReader(constant.Value).ReadConstant(constant.TypeCode);
            if (Convert.ToString(constantValue, System.Globalization.CultureInfo.InvariantCulture) == numeric)
                return md.GetString(field.Name);
        }
        return null;
    }

    public static CorDebugValue Field(CorDebugValue raw, string name)
    {
        var value = Dereference(raw);
        if (value is CorDebugReferenceValue reference && reference.IsNull)
            throw new InvalidOperationException($"Cannot read '{name}' from Nothing.");
        if (value is not CorDebugObjectValue instance)
            throw new InvalidOperationException($"'{name}' requires an object value.");
        return FindField(instance, name)
            ?? throw new InvalidOperationException($"No instance field named '{name}'; properties and method calls are not evaluated.");
    }

    public static CorDebugValue? FindField(CorDebugObjectValue instance, string name, CorDebugClass? startAt = null)
    {
        var field = Fields(instance, startAt, includePropertyNames: true).FirstOrDefault(f =>
            string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(f.PropertyName, name, StringComparison.OrdinalIgnoreCase));
        return field == null ? null : instance.GetFieldValue(field.Class.Raw, field.Token);
    }

    public static CorDebugValue Element(CorDebugValue raw, int[] indices)
    {
        if (Dereference(raw) is not CorDebugArrayValue array)
            throw new NotSupportedException("Indexing requires an array; method and default-property calls are not supported.");
        if (indices.Length != array.Rank) throw new ArgumentException($"Array requires {array.Rank} indices.");
        var lengths = array.GetDimensions(array.Rank);
        var lower = LowerBounds(array);
        for (int i = 0; i < indices.Length; i++)
            if ((long)indices[i] < lower[i] || (long)indices[i] >= (long)lower[i] + lengths[i])
                throw new ArgumentException($"Array index {indices[i]} is out of range in dimension {i + 1}.");
        return array.GetElement(indices.Length, indices);
    }

    public static int[] LowerBounds(CorDebugArrayValue array) =>
        array.HasBaseIndicies() ? array.GetBaseIndicies(array.Rank) : new int[array.Rank];

    public static int[] Indices(int position, int[] lengths, int[] lower)
    {
        var indices = new int[lengths.Length];
        for (int i = lengths.Length - 1; i >= 0; i--)
        {
            indices[i] = lower[i] + position % lengths[i];
            position /= lengths[i];
        }
        return indices;
    }
}
