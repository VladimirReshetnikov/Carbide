using System.Globalization;
using CarbideBash.Errors;
using CarbideShellCore.Env;

namespace CarbideBash.Runtime;

/// <summary>
/// Integer expression evaluator backing <c>$((...))</c>. Supports the operators bash treats
/// as part of its core arithmetic dialect: parentheses, unary <c>-</c>/<c>+</c>/<c>~</c>
/// /<c>!</c>, binary <c>* / %</c>, <c>+ -</c>, <c>&lt;&lt; &gt;&gt;</c>, comparison
/// <c>&lt; &lt;= &gt; &gt;=</c>, equality <c>== !=</c>, bitwise <c>&amp; ^ |</c>, logical
/// <c>&amp;&amp; ||</c>, conditional <c>?:</c>, and assignment (<c>=</c>, <c>+=</c>,
/// <c>-=</c>, <c>*=</c>, <c>/=</c>, <c>%=</c>).
/// </summary>
internal static class ArithmeticEvaluator
{
    public static long Evaluate(string expr, EnvVarStore env)
    {
        var p = new Parser(expr, env);
        var value = p.ParseExpression();
        p.ExpectEnd();
        return value;
    }

    private sealed class Parser
    {
        private readonly string _src;
        private readonly EnvVarStore _env;
        private int _pos;

        public Parser(string src, EnvVarStore env) { _src = src ?? ""; _env = env; }

        public long ParseExpression() => ParseAssignment();

        private long ParseAssignment()
        {
            int save = _pos;
            SkipWs();
            int nameStart = _pos;
            while (_pos < _src.Length && (char.IsLetterOrDigit(_src[_pos]) || _src[_pos] == '_')) _pos++;
            int nameEnd = _pos;
            SkipWs();
            if (nameEnd > nameStart && _pos < _src.Length)
            {
                var op = _src[_pos];
                var op2 = _pos + 1 < _src.Length ? _src[_pos + 1] : '\0';
                if (op == '=' && op2 != '=')
                {
                    _pos++;
                    var rhs = ParseAssignment();
                    var name = _src.Substring(nameStart, nameEnd - nameStart);
                    _env.Set(name, rhs.ToString(CultureInfo.InvariantCulture));
                    return rhs;
                }
                if ((op == '+' || op == '-' || op == '*' || op == '/' || op == '%') && op2 == '=')
                {
                    _pos += 2;
                    var rhs = ParseAssignment();
                    var name = _src.Substring(nameStart, nameEnd - nameStart);
                    long cur = long.TryParse(_env.Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
                    long val = op switch
                    {
                        '+' => cur + rhs,
                        '-' => cur - rhs,
                        '*' => cur * rhs,
                        '/' => rhs == 0 ? throw new BashRuntimeException("division by zero") : cur / rhs,
                        '%' => rhs == 0 ? throw new BashRuntimeException("division by zero") : cur % rhs,
                        _ => rhs,
                    };
                    _env.Set(name, val.ToString(CultureInfo.InvariantCulture));
                    return val;
                }
            }
            _pos = save;
            return ParseTernary();
        }

        private long ParseTernary()
        {
            var cond = ParseLogicalOr();
            SkipWs();
            if (Match('?'))
            {
                var t = ParseExpression();
                SkipWs();
                if (!Match(':')) throw new BashParseException("Expected ':' in ternary.");
                var f = ParseExpression();
                return cond != 0 ? t : f;
            }
            return cond;
        }

        private long ParseLogicalOr()
        {
            var l = ParseLogicalAnd();
            while (true)
            {
                SkipWs();
                if (PeekTwo("||")) { _pos += 2; var r = ParseLogicalAnd(); l = (l != 0 || r != 0) ? 1 : 0; }
                else break;
            }
            return l;
        }

        private long ParseLogicalAnd()
        {
            var l = ParseBitOr();
            while (true)
            {
                SkipWs();
                if (PeekTwo("&&")) { _pos += 2; var r = ParseBitOr(); l = (l != 0 && r != 0) ? 1 : 0; }
                else break;
            }
            return l;
        }

        private long ParseBitOr()
        {
            var l = ParseBitXor();
            while (true)
            {
                SkipWs();
                if (_pos < _src.Length && _src[_pos] == '|' && Peek(1) != '|') { _pos++; l |= ParseBitXor(); }
                else break;
            }
            return l;
        }

        private long ParseBitXor()
        {
            var l = ParseBitAnd();
            while (true)
            {
                SkipWs();
                if (Match('^')) l ^= ParseBitAnd();
                else break;
            }
            return l;
        }

        private long ParseBitAnd()
        {
            var l = ParseEquality();
            while (true)
            {
                SkipWs();
                if (_pos < _src.Length && _src[_pos] == '&' && Peek(1) != '&') { _pos++; l &= ParseEquality(); }
                else break;
            }
            return l;
        }

        private long ParseEquality()
        {
            var l = ParseComparison();
            while (true)
            {
                SkipWs();
                if (PeekTwo("==")) { _pos += 2; l = l == ParseComparison() ? 1 : 0; }
                else if (PeekTwo("!=")) { _pos += 2; l = l != ParseComparison() ? 1 : 0; }
                else break;
            }
            return l;
        }

        private long ParseComparison()
        {
            var l = ParseShift();
            while (true)
            {
                SkipWs();
                if (PeekTwo("<=")) { _pos += 2; l = l <= ParseShift() ? 1 : 0; }
                else if (PeekTwo(">=")) { _pos += 2; l = l >= ParseShift() ? 1 : 0; }
                else if (_pos < _src.Length && _src[_pos] == '<' && Peek(1) != '<') { _pos++; l = l < ParseShift() ? 1 : 0; }
                else if (_pos < _src.Length && _src[_pos] == '>' && Peek(1) != '>') { _pos++; l = l > ParseShift() ? 1 : 0; }
                else break;
            }
            return l;
        }

        private long ParseShift()
        {
            var l = ParseAdditive();
            while (true)
            {
                SkipWs();
                if (PeekTwo("<<")) { _pos += 2; l <<= (int)ParseAdditive(); }
                else if (PeekTwo(">>")) { _pos += 2; l >>= (int)ParseAdditive(); }
                else break;
            }
            return l;
        }

        private long ParseAdditive()
        {
            var l = ParseMultiplicative();
            while (true)
            {
                SkipWs();
                if (Match('+')) l += ParseMultiplicative();
                else if (Match('-')) l -= ParseMultiplicative();
                else break;
            }
            return l;
        }

        private long ParseMultiplicative()
        {
            var l = ParseUnary();
            while (true)
            {
                SkipWs();
                if (Match('*')) l *= ParseUnary();
                else if (Match('/')) { var r = ParseUnary(); if (r == 0) throw new BashRuntimeException("division by zero"); l /= r; }
                else if (Match('%')) { var r = ParseUnary(); if (r == 0) throw new BashRuntimeException("division by zero"); l %= r; }
                else break;
            }
            return l;
        }

        private long ParseUnary()
        {
            SkipWs();
            if (Match('-')) return -ParseUnary();
            if (Match('+')) return ParseUnary();
            if (Match('!')) return ParseUnary() == 0 ? 1 : 0;
            if (Match('~')) return ~ParseUnary();
            return ParsePrimary();
        }

        private long ParsePrimary()
        {
            SkipWs();
            if (Match('('))
            {
                var v = ParseExpression();
                SkipWs();
                if (!Match(')')) throw new BashParseException("Expected ')' in arithmetic.");
                return v;
            }
            if (_pos < _src.Length && char.IsDigit(_src[_pos]))
            {
                int start = _pos;
                while (_pos < _src.Length && (char.IsDigit(_src[_pos]) || _src[_pos] == 'x' || (_src[_pos] >= 'a' && _src[_pos] <= 'f') || (_src[_pos] >= 'A' && _src[_pos] <= 'F'))) _pos++;
                var text = _src.Substring(start, _pos - start);
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    return long.Parse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return long.Parse(text, CultureInfo.InvariantCulture);
            }
            if (_pos < _src.Length && (char.IsLetter(_src[_pos]) || _src[_pos] == '_'))
            {
                int start = _pos;
                while (_pos < _src.Length && (char.IsLetterOrDigit(_src[_pos]) || _src[_pos] == '_')) _pos++;
                var name = _src.Substring(start, _pos - start);
                var raw = _env.Get(name);
                if (raw is null) return 0;
                return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
            }
            throw new BashParseException($"Unexpected char '{(_pos < _src.Length ? _src[_pos].ToString() : "EOF")}' in arithmetic.");
        }

        private bool Match(char c)
        {
            SkipWs();
            if (_pos < _src.Length && _src[_pos] == c) { _pos++; return true; }
            return false;
        }

        public void ExpectEnd()
        {
            SkipWs();
            if (_pos < _src.Length) throw new BashParseException($"Unexpected tail in arithmetic: '{_src[_pos..]}'.");
        }

        private bool PeekTwo(string s)
            => _pos + 1 < _src.Length && _src[_pos] == s[0] && _src[_pos + 1] == s[1];

        private char Peek(int offset) => _pos + offset < _src.Length ? _src[_pos + offset] : '\0';

        private void SkipWs()
        {
            while (_pos < _src.Length && char.IsWhiteSpace(_src[_pos])) _pos++;
        }
    }
}
