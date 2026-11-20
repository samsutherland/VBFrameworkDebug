using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DesktopClr;

internal sealed class BreakpointRequest
{
    public int Line { get; }
    public string? Condition { get; }
    public string? HitCondition { get; }
    public string? LogMessage { get; }
    public Func<long, bool> Hit { get; }
    public List<(string Text, bool Expression)> LogParts { get; } = new();
    public (int, string?, string?, string?) Key => (Line, Condition, HitCondition, LogMessage);

    public BreakpointRequest(int line, string? condition, string? hitCondition, string? logMessage)
    {
        Line = line;
        Condition = string.IsNullOrWhiteSpace(condition) ? null : condition;
        HitCondition = string.IsNullOrWhiteSpace(hitCondition) ? null : hitCondition;
        LogMessage = logMessage;
        if (Condition != null) ValueExpression.Validate(Condition);
        Hit = ParseHit(HitCondition);
        if (LogMessage != null) ParseLog(LogMessage);
    }

    private static Func<long, bool> ParseHit(string? text)
    {
        if (text == null) return _ => true;
        var match = Regex.Match(text, @"^\s*(==|>=|%)?\s*([1-9][0-9]*)\s*$", RegexOptions.CultureInvariant);
        if (!match.Success || !long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long count))
            throw new ArgumentException("Hit condition must be N, == N, >= N, or % N with a positive integer.");
        return match.Groups[1].Value switch
        {
            "==" => hit => hit == count,
            "%" => hit => hit % count == 0,
            _ => hit => hit >= count
        };
    }

    private void ParseLog(string text)
    {
        if (text.Length > 4096) throw new ArgumentException("Log messages are limited to 4096 characters.");
        var literal = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '{' or '}')
            {
                if (i + 1 < text.Length && text[i + 1] == c) { literal.Append(c); i++; continue; }
                if (c == '}') throw new ArgumentException("Unescaped closing brace in log message.");
                if (literal.Length > 0) { LogParts.Add((literal.ToString(), false)); literal.Clear(); }
                int start = ++i;
                bool quoted = false;
                for (; i < text.Length; i++)
                {
                    if (text[i] == '"') quoted = !quoted;
                    if (!quoted && text[i] == '}') break;
                }
                if (i == text.Length) throw new ArgumentException("Unterminated logpoint expression.");
                string expression = text.Substring(start, i - start);
                ValueExpression.Validate(expression);
                LogParts.Add((expression, true));
            }
            else literal.Append(c);
        }
        if (literal.Length > 0) LogParts.Add((literal.ToString(), false));
    }
}
