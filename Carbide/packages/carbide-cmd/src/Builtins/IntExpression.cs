using System.Globalization;
using CarbideCmd.Errors;
using CarbideShellCore.Env;

namespace CarbideCmd.Builtins;

/// <summary>
/// Minimal integer-expression evaluator for <c>SET /A</c>. Supports the operators real cmd
/// treats as part of the Phase-1 core: parentheses, unary minus/plus/~/!, <c>*</c>, <c>/</c>,
/// <c>%</c>, <c>+</c>, <c>-</c>, <c>&lt;&lt;</c>, <c>&gt;&gt;</c>, <c>&amp;</c>, <c>^</c>,
/// <c>|</c>. Identifiers resolve against the shared <see cref="EnvVarStore"/>; missing names
/// evaluate to 0 (matching real cmd).
/// </summary>
internal static class IntExpression
{
    public static long Evaluate(string expression, EnvVarStore env)
    {
        var parser = new Parser(expression, env);
        var value = parser.ParseOr();
        parser.Expect(-1);
        return value;
    }

    private sealed class Parser
    {
        private readonly string _src;
        private readonly EnvVarStore _env;
        private int _pos;

        public Parser(string src, EnvVarStore env) { _src = src ?? ""; _env = env; }

        public long ParseOr()
        {
            var left = ParseXor();
            while (Match('|'))
            {
                var right = ParseXor();
                left |= right;
            }
            return left;
        }

        private long ParseXor()
        {
            var left = ParseAnd();
            while (Match('^'))
            {
                var right = ParseAnd();
                left ^= right;
            }
            return left;
        }

        private long ParseAnd()
        {
            var left = ParseShift();
            while (Match('&'))
            {
                var right = ParseShift();
                left &= right;
            }
            return left;
        }

        private long ParseShift()
        {
            var left = ParseAdditive();
            while (true)
            {
                SkipWs();
                if (PeekTwo("<<")) { _pos += 2; left <<= (int)ParseAdditive(); continue; }
                if (PeekTwo(">>")) { _pos += 2; left >>= (int)ParseAdditive(); continue; }
                break;
            }
            return left;
        }

        private long ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (true)
            {
                SkipWs();
                if (Match('+')) left += ParseMultiplicative();
                else if (Match('-')) left -= ParseMultiplicative();
                else break;
            }
            return left;
        }

        private long ParseMultiplicative()
        {
            var left = ParseUnary();
            while (true)
            {
                SkipWs();
                if (Match('*')) left *= ParseUnary();
                else if (Match('/'))
                {
                    var r = ParseUnary();
                    if (r == 0) throw new CmdRuntimeException("Divide by zero error.");
                    left /= r;
                }
                else if (Match('%'))
                {
                    var r = ParseUnary();
                    if (r == 0) throw new CmdRuntimeException("Divide by zero error.");
                    left %= r;
                }
                else break;
            }
            return left;
        }

        private long ParseUnary()
        {
            SkipWs();
            if (Match('-')) return -ParseUnary();
            if (Match('+')) return ParseUnary();
            if (Match('~')) return ~ParseUnary();
            if (Match('!')) return ParseUnary() == 0 ? 1 : 0;
            return ParsePrimary();
        }

        private long ParsePrimary()
        {
            SkipWs();
            if (Match('('))
            {
                var v = ParseOr();
                SkipWs();
                if (!Match(')')) throw new CmdRuntimeException("Missing ')' in SET /A.");
                return v;
            }
            if (_pos < _src.Length && char.IsDigit(_src[_pos]))
            {
                int start = _pos;
                while (_pos < _src.Length && char.IsDigit(_src[_pos])) _pos++;
                return long.Parse(_src.AsSpan(start, _pos - start), CultureInfo.InvariantCulture);
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
            if (_pos >= _src.Length) return 0;
            throw new CmdRuntimeException($"Unexpected character '{_src[_pos]}' in SET /A expression.");
        }

        private bool Match(char c)
        {
            SkipWs();
            if (_pos < _src.Length && _src[_pos] == c) { _pos++; return true; }
            return false;
        }

        public void Expect(int eof)
        {
            SkipWs();
            if (_pos != _src.Length) throw new CmdRuntimeException($"Unexpected tail in SET /A: '{_src[_pos..]}'");
        }

        private bool PeekTwo(string s)
            => _pos + 1 < _src.Length && _src[_pos] == s[0] && _src[_pos + 1] == s[1];

        private void SkipWs()
        {
            while (_pos < _src.Length && char.IsWhiteSpace(_src[_pos])) _pos++;
        }
    }
}
