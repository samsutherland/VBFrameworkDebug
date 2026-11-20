using DesktopClr;

internal static class Program
{
    private static int Main()
    {
        try { return Run(); }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static int Run()
    {
        object Parse(string text, bool binary = false)
        {
            return new ValueExpression(text, name => throw new InvalidOperationException("Unexpected target read: " + name), binary).Read().Primitive!;
        }

        int count = 0;
        foreach (var (text, expected) in new (string, object)[] {
            ("1 + 2 * 3", 7), ("(1 + 2) * 3", 9), ("10L + 1", 11L),
            ("5 / 2", 2.5), ("5 \\ 2", 2), ("-5 Mod 2", -1),
            ("1.5D + 2", 3.5m), ("5D / 2D", 2.5m), ("-1.5D", -1.5m),
            ("Not 1 = 2", true), ("1 < 2 AndAlso 2 < 3", true),
            ("False AndAlso missing = 1", false), ("True OrElse missing = 1", true),
            ("Not False AndAlso False OrElse True", true), ("Nothing Is Nothing", true),
            ("\"a\" & \"b\"", "ab"), ("\"a\" = \"a\"", true), ("\"a\" < \"b\"", true),
            ("1 + 2L", 3L), ("1.0R + 2", 3.0),
        })
        {
            object actual = Parse(text, true);
            if (actual.GetType() != expected.GetType() || !actual.Equals(expected))
                throw new Exception($"{text}: expected {expected} ({expected.GetType()}), got {actual} ({actual.GetType()}).");
            count++;
        }
        foreach (string text in new[] { "2147483647 + 1", "9223372036854775807L + 1L", "1 / 0",
            "1 \\ 0", "1 Mod 0", "1 + True", "1 Is Nothing", "1.5F + 2.5F",
            "1 ^ 2", "1 =", "True OrElse (1 +)", new string('(', 40) + "1" + new string(')', 40),
            new string('1', 4097), "\"a\" = \"b\"" })
        {
            try { Parse(text); }
            catch (Exception ex) when (ex is NotSupportedException or
                ArithmeticException or ArgumentException or FormatException) { count++; continue; }
            throw new Exception("Expected expression rejection: " + text);
        }
        foreach (Action action in new Action[] {
            () => Evaluation.Binary("+", Evaluation.Constant((short)1), Evaluation.Constant((short)2), false),
            () => Evaluation.Binary("&", Evaluation.Constant(new string('a', 3000)), Evaluation.Constant(new string('b', 3000)), false),
        })
        {
            try { action(); }
            catch (NotSupportedException) { count++; continue; }
            throw new Exception("Expected unsupported conversion or oversized result rejection.");
        }
        foreach (var (condition, hits) in new[] { ("3", "3,4,5"), (">= 3", "3,4,5"), ("== 3", "3"), ("% 2", "2,4") })
        {
            var request = new BreakpointRequest(1, null, condition, null);
            if (string.Join(",", Enumerable.Range(1, 5).Where(i => request.Hit(i))) != hits)
                throw new Exception("Incorrect hit-count predicate: " + condition);
            count++;
        }
        var paths = new SourcePaths();
        paths.Configure(new Dictionary<string, string> {
            [@"C:\build\src"] = @"C:\local",
            [@"C:\build\src\sub"] = @"D:\specific"
        });
        foreach (var (source, expected) in new[] {
            (@"C:\build\src\app.vb", @"C:\local\app.vb"),
            (@"C:\BUILD\SRC\sub\app.vb", @"D:\specific\app.vb"),
            (@"C:\build\srcOther\app.vb", @"C:\build\srcOther\app.vb"),
            ("", ""), ("virtual", "virtual")
        })
        {
            if (paths.Map(source) != expected) throw new Exception("Incorrect source mapping: " + source);
            count++;
        }
        Console.WriteLine($"Pure evaluator tests passed: {count}");
        return 0;
    }
}
