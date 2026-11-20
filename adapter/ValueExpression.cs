using System.Globalization;
using System.Text;
using ClrDebug;

namespace DesktopClr;

internal sealed class ValueExpression
{
    private readonly string text;
    private readonly Func<string, CorDebugValue> resolve;
    private readonly bool binaryStrings;
    private int position;
    private int depth;
    private string token = "";
    private string contents = "";

    public ValueExpression(string text, Func<string, CorDebugValue> resolve, bool binaryStrings = false)
    {
        if (text.Length > 4096) throw new ArgumentException("Expression exceeds 4096 characters.");
        this.text = text;
        this.resolve = resolve;
        this.binaryStrings = binaryStrings;
        Next();
    }

    public Evaluation Read()
    {
        var value = Expression(0, true);
        if (token != "") throw Error("Unsupported VB expression or method call.");
        return value;
    }

    public static void Validate(string text)
    {
        var parser = new ValueExpression(text, _ => throw new InvalidOperationException("Syntax validation cannot read target values."));
        parser.Expression(0, false);
        if (parser.token != "") throw parser.Error("Unsupported VB expression.");
    }

    private Evaluation Expression(int minimum, bool evaluate)
    {
        if (++depth > 32) throw Error("Expression nesting exceeds 32 levels.");
        Evaluation left;
        if (token is "+" or "-" or "not")
        {
            string op = token;
            Next();
            // VB Not binds less tightly than comparisons.
            var operand = Expression(op == "not" ? 3 : 9, evaluate);
            left = evaluate ? Evaluation.Unary(op, operand) : Evaluation.Constant(null);
        }
        else left = Primary(evaluate);
        while (Precedence(token) is int precedence && precedence >= minimum)
        {
            string op = token;
            Next();
            bool skip = evaluate && ((op == "andalso" && !left.Boolean()) || (op == "orelse" && left.Boolean()));
            var right = Expression(precedence + 1, evaluate && !skip);
            if (evaluate && !skip) left = Evaluation.Binary(op, left, right, binaryStrings);
        }
        depth--;
        return left;
    }

    private Evaluation Primary(bool evaluate)
    {
        Evaluation value;
        if (Take("(")) { value = Expression(0, evaluate); Expect(")"); }
        else if (token == "number")
        {
            string literal = contents;
            Next();
            char suffix = char.ToUpperInvariant(literal[literal.Length - 1]);
            bool typed = suffix is 'D' or 'F' or 'R' or 'L';
            string digits = typed ? literal.Substring(0, literal.Length - 1) : literal;
            value = Evaluation.Constant(suffix switch
            {
                'D' => (object)decimal.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture),
                'F' => float.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture),
                'R' => double.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture),
                'L' => long.Parse(digits, CultureInfo.InvariantCulture),
                _ => digits.IndexOf('.') >= 0 ? (object)double.Parse(digits, CultureInfo.InvariantCulture) :
                    int.Parse(digits, CultureInfo.InvariantCulture)
            });
        }
        else if (token == "string") { value = Evaluation.Constant(contents); Next(); }
        else if (Take("true")) value = Evaluation.Constant(true);
        else if (Take("false")) value = Evaluation.Constant(false);
        else if (Take("nothing")) value = Evaluation.Constant(null);
        else
        {
            string name = Name();
            value = evaluate ? Evaluation.Runtime(resolve(name)) : Evaluation.Constant(null);
        }
        while (true)
        {
            if (Take("."))
            {
                string name = Name();
                if (evaluate) value = Evaluation.Runtime(ValueAccess.Field(RequireRaw(value), name));
            }
            else if (Take("("))
            {
                var indices = new List<int>();
                do
                {
                    if (indices.Count == 32) throw Error("Too many array indices.");
                    var index = Expression(0, evaluate);
                    if (evaluate)
                    {
                        if (index.Primitive is not int integer) throw Error("Array indices must be Integer values.");
                        indices.Add(integer);
                    }
                } while (Take(","));
                Expect(")");
                if (evaluate) value = Evaluation.Runtime(ValueAccess.Element(RequireRaw(value), indices.ToArray()));
            }
            else return value;
        }
    }

    private static CorDebugValue RequireRaw(Evaluation value) => value.Raw ??
        throw new NotSupportedException("Member/index access requires a runtime object or array; method calls are not supported.");

    private string Name()
    {
        if (token != "name") throw Error("Expected a local or argument name.");
        string name = contents;
        Next();
        return name;
    }

    private bool Take(string expected) { if (token != expected) return false; Next(); return true; }
    private void Expect(string expected) { if (!Take(expected)) throw Error($"Expected '{expected}'."); }
    private NotSupportedException Error(string message) => new($"{message} At character {position + 1}.");
    private static int? Precedence(string op) => op switch
    {
        "orelse" => 1,
        "andalso" => 2,
        "=" or "<>" or "<" or "<=" or ">" or ">=" or "is" or "isnot" => 3,
        "&" => 4,
        "+" or "-" => 5,
        "mod" => 6,
        "\\" => 7,
        "*" or "/" => 8,
        _ => null
    };

    private void Next()
    {
        while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
        if (position == text.Length) { token = contents = ""; return; }
        int start = position;
        char c = text[position++];
        if (c == '"')
        {
            var literal = new StringBuilder();
            while (position < text.Length)
            {
                char next = text[position++];
                if (next != '"') { literal.Append(next); continue; }
                if (position < text.Length && text[position] == '"') { literal.Append('"'); position++; continue; }
                token = "string"; contents = literal.ToString(); return;
            }
            throw Error("Unterminated string literal.");
        }
        if (c == '[')
        {
            start = position;
            while (position < text.Length && text[position] != ']') position++;
            if (position == text.Length || position == start) throw Error("Invalid bracketed identifier.");
            token = "name"; contents = text.Substring(start, position++ - start); return;
        }
        if (char.IsLetter(c) || c == '_')
        {
            while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] == '_')) position++;
            contents = text.Substring(start, position - start);
            string keyword = contents.ToLowerInvariant();
            token = keyword is "andalso" or "orelse" or "not" or "mod" or "is" or "isnot" or "true" or "false" or "nothing" ? keyword : "name";
            return;
        }
        if (char.IsDigit(c))
        {
            while (position < text.Length && (char.IsDigit(text[position]) || text[position] == '.')) position++;
            if (position < text.Length && "dDfFrRlL".IndexOf(text[position]) >= 0) position++;
            token = "number"; contents = text.Substring(start, position - start); return;
        }
        token = c.ToString();
        if (position < text.Length && (c is '<' or '>') && (text[position] == '=' || c == '<' && text[position] == '>'))
            token += text[position++];
        contents = token;
    }
}
