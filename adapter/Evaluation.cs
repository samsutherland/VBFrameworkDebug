using System.Globalization;
using ClrDebug;

namespace DesktopClr;

internal sealed class Evaluation
{
    public CorDebugValue? Raw { get; }
    private readonly object? constant;
    private Evaluation(CorDebugValue? raw, object? value) { Raw = raw; constant = value; }
    public static Evaluation Runtime(CorDebugValue value) => new(value, null);
    public static Evaluation Constant(object? value)
    {
        if (value is double d && (double.IsNaN(d) || double.IsInfinity(d)))
            throw new NotSupportedException("Non-finite floating point results are not supported.");
        if (value is float f && (float.IsNaN(f) || float.IsInfinity(f)))
            throw new NotSupportedException("Non-finite floating point results are not supported.");
        if (value is string text && text.Length > 4096)
            throw new NotSupportedException("Expression string results are limited to 4096 characters.");
        return new(null, value);
    }
    public object? Primitive => Raw == null ? constant : ValueAccess.Primitive(Raw);

    public bool Boolean() => Primitive is bool value ? value :
        throw new NotSupportedException("A Boolean value is required; implicit conversions are not evaluated.");

    public static Evaluation Unary(string op, Evaluation operand)
    {
        if (op == "not") return Constant(!operand.Boolean());
        var value = operand.Primitive;
        if (!Numeric(value)) throw new NotSupportedException("Unary arithmetic requires a supported number.");
        if (op == "+") return Constant(value);
        checked
        {
            return Constant(value switch
            {
                decimal d => -d,
                double d => -d,
                long l => -l,
                _ => -Convert.ToInt32(value, CultureInfo.InvariantCulture)
            });
        }
    }

    public static Evaluation Binary(string op, Evaluation left, Evaluation right, bool binaryStrings)
    {
        if (op is "is" or "isnot")
        {
            bool same = SameReference(left, right);
            return Constant(op == "is" ? same : !same);
        }
        var a = left.Primitive;
        var b = right.Primitive;
        if (op == "&")
        {
            if (a is not (null or string) || b is not (null or string))
                throw new NotSupportedException("Concatenation currently supports strings and Nothing only.");
            return Constant((string?)a + (string?)b);
        }
        if (op is "andalso" or "orelse")
            return Constant(op == "andalso" ? left.Boolean() && right.Boolean() : left.Boolean() || right.Boolean());
        if (op is "=" or "<>" or "<" or "<=" or ">" or ">=")
        {
            int comparison;
            if (a is string sa && b is string sb)
            {
                if (!binaryStrings)
                    throw new NotSupportedException("Set expressionStringComparison to 'binary' for Option Compare Binary string comparisons.");
                comparison = string.CompareOrdinal(sa, sb);
            }
            else if (a is bool ba && b is bool bb && op is "=" or "<>") comparison = ba.CompareTo(bb);
            else if (Numeric(a) && Numeric(b))
            {
                if (a is double || b is double) comparison = Convert.ToDouble(a).CompareTo(Convert.ToDouble(b));
                else comparison = Convert.ToDecimal(a).CompareTo(Convert.ToDecimal(b));
            }
            else throw new NotSupportedException("Comparison requires matching Boolean/string values or supported numbers. Use Is for object identity.");
            return Constant(op switch
            {
                "=" => comparison == 0,
                "<>" => comparison != 0,
                "<" => comparison < 0,
                "<=" => comparison <= 0,
                ">" => comparison > 0,
                ">=" => comparison >= 0,
                _ => throw new InvalidOperationException()
            });
        }
        if (!Numeric(a) || !Numeric(b)) throw new NotSupportedException("Arithmetic requires supported numeric values.");
        if (op == "/")
        {
            double divisor = Convert.ToDouble(b);
            if (divisor == 0) throw new DivideByZeroException();
            return Constant(a is decimal && b is not double || b is decimal && a is not double
                ? (object)(Convert.ToDecimal(a) / Convert.ToDecimal(b)) : Convert.ToDouble(a) / divisor);
        }
        if (op == "\\" && (a is decimal or double || b is decimal or double))
            throw new NotSupportedException("Integer division currently requires integer operands.");
        checked
        {
            if (a is double || b is double)
            {
                double x = Convert.ToDouble(a), y = Convert.ToDouble(b);
                if (op == "mod" && y == 0) throw new DivideByZeroException();
                return Constant(op switch
                {
                    "+" => x + y,
                    "-" => x - y,
                    "*" => x * y,
                    "mod" => x % y,
                    _ => throw new NotSupportedException($"Unsupported operator '{op}'.")
                });
            }
            if (a is decimal || b is decimal)
            {
                decimal x = Convert.ToDecimal(a), y = Convert.ToDecimal(b);
                return Constant(op switch
                {
                    "+" => x + y,
                    "-" => x - y,
                    "*" => x * y,
                    "mod" => x % y,
                    _ => throw new NotSupportedException($"Unsupported operator '{op}'.")
                });
            }
            long lx = Convert.ToInt64(a), ly = Convert.ToInt64(b);
            long result = op switch
            {
                "+" => lx + ly,
                "-" => lx - ly,
                "*" => lx * ly,
                "\\" => lx / ly,
                "mod" => lx % ly,
                _ => throw new NotSupportedException($"Unsupported operator '{op}'.")
            };
            return Constant(a is long || b is long ? (object)result : checked((int)result));
        }
    }

    private static bool Numeric(object? value) => value is int or long or decimal ||
        value is double d && !double.IsNaN(d) && !double.IsInfinity(d);

    private static bool SameReference(Evaluation left, Evaluation right)
    {
        if (!ReferenceOperand(left) || !ReferenceOperand(right))
            throw new NotSupportedException("Is/IsNot requires reference values or Nothing.");
        CorDebugValue? a = left.Raw == null ? null : ValueAccess.Dereference(left.Raw);
        CorDebugValue? b = right.Raw == null ? null : ValueAccess.Dereference(right.Raw);
        bool an = a == null ? left.constant == null : a is CorDebugReferenceValue ar && ar.IsNull;
        bool bn = b == null ? right.constant == null : b is CorDebugReferenceValue br && br.IsNull;
        if ((!an && a is not (CorDebugObjectValue or CorDebugArrayValue or CorDebugStringValue)) ||
            (!bn && b is not (CorDebugObjectValue or CorDebugArrayValue or CorDebugStringValue)))
            throw new NotSupportedException("Is/IsNot requires reference values or Nothing.");
        if (an || bn) return an && bn;
        return a!.Address.Equals(b!.Address);
    }

    private static bool ReferenceOperand(Evaluation value)
    {
        var raw = value.Raw;
        for (int i = 0; i < 16 && raw is CorDebugReferenceValue reference && raw.Type == CorElementType.ByRef; i++)
            raw = reference.Dereference();
        return raw == null ? value.constant == null :
            raw is CorDebugReferenceValue or CorDebugStringValue or CorDebugArrayValue ||
            raw is CorDebugObjectValue && raw.Type != CorElementType.ValueType;
    }
}
