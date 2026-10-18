using System.Globalization;

namespace Spotlight.Core.Search;

/// <summary>Spotlight-style inline calculator: "12*(3+4)" shows "= 84". Supports + - * / % ^,
/// parentheses, decimals and the full-width operators a Chinese IME produces.</summary>
public static class Calculator
{
    public static bool TryEvaluate(string input, out string result)
    {
        result = "";
        var s = Normalize(input);
        if (s.Length < 3 || !s.Any(char.IsDigit) || !s.Any(c => "+-*/%^(".Contains(c))) return false;
        if (s.Any(c => !(char.IsDigit(c) || "+-*/%^(). ".Contains(c)))) return false;
        try
        {
            int pos = 0;
            double v = ParseExpr(s, ref pos);
            SkipWs(s, ref pos);
            if (pos != s.Length || double.IsNaN(v) || double.IsInfinity(v)) return false;
            result = Math.Round(v, 10).ToString("G15", CultureInfo.InvariantCulture);
            return true;
        }
        catch (FormatException) { return false; }
    }

    private static string Normalize(string s) => s.Trim().TrimEnd('=')
        .Replace('×', '*').Replace('÷', '/').Replace('（', '(').Replace('）', ')')
        .Replace('＋', '+').Replace('－', '-').Replace('＊', '*').Replace('／', '/').Replace('．', '.');

    private static void SkipWs(string s, ref int p) { while (p < s.Length && s[p] == ' ') p++; }

    private static double ParseExpr(string s, ref int p)
    {
        double v = ParseTerm(s, ref p);
        while (true)
        {
            SkipWs(s, ref p);
            if (p < s.Length && (s[p] == '+' || s[p] == '-'))
            {
                char op = s[p++];
                double r = ParseTerm(s, ref p);
                v = op == '+' ? v + r : v - r;
            }
            else return v;
        }
    }

    private static double ParseTerm(string s, ref int p)
    {
        double v = ParsePower(s, ref p);
        while (true)
        {
            SkipWs(s, ref p);
            if (p < s.Length && (s[p] == '*' || s[p] == '/' || s[p] == '%'))
            {
                char op = s[p++];
                double r = ParsePower(s, ref p);
                v = op switch { '*' => v * r, '/' => v / r, _ => v % r };
            }
            else return v;
        }
    }

    private static double ParsePower(string s, ref int p)
    {
        double b = ParseUnary(s, ref p);
        SkipWs(s, ref p);
        if (p < s.Length && s[p] == '^')
        {
            p++;
            return Math.Pow(b, ParsePower(s, ref p)); // right-associative
        }
        return b;
    }

    private static double ParseUnary(string s, ref int p)
    {
        SkipWs(s, ref p);
        if (p < s.Length && s[p] == '-') { p++; return -ParseUnary(s, ref p); }
        if (p < s.Length && s[p] == '+') { p++; return ParseUnary(s, ref p); }
        if (p < s.Length && s[p] == '(')
        {
            p++;
            double v = ParseExpr(s, ref p);
            SkipWs(s, ref p);
            if (p >= s.Length || s[p] != ')') throw new FormatException();
            p++;
            return v;
        }
        int start = p;
        while (p < s.Length && (char.IsDigit(s[p]) || s[p] == '.')) p++;
        if (start == p) throw new FormatException();
        return double.Parse(s.AsSpan(start, p - start), NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
