using System.Text;
using CarbideBash.Errors;

namespace CarbideBash.Lexer;

/// <summary>
/// Line-aware bash lexer for the Phase 1 subset. Produces word tokens with quotes preserved;
/// parameter, command, and arithmetic expansion happens later in the expansion stage.
/// Comments beginning with <c>#</c> consume to end-of-line and emit nothing.
/// </summary>
public sealed class BashLexer
{
    private readonly string _src;
    private int _pos;
    private int _line = 1;
    private int _col = 1;

    public BashLexer(string source) { _src = source ?? ""; }

    public static List<Token> Tokenize(string source)
    {
        var l = new BashLexer(source);
        var tokens = new List<Token>();
        while (true)
        {
            var t = l.Next();
            tokens.Add(t);
            if (t.Kind == TokenKind.EndOfFile) break;
            if (t.Kind == TokenKind.Heredoc || t.Kind == TokenKind.HeredocDash)
            {
                // Read the delimiter word, any additional same-line tokens, then the
                // terminating newline. The body starts on the next source line.
                var delim = l.Next();
                tokens.Add(delim);
                if (delim.Kind != TokenKind.Word) continue;
                while (true)
                {
                    var follow = l.Next();
                    tokens.Add(follow);
                    if (follow.Kind == TokenKind.Newline || follow.Kind == TokenKind.EndOfFile) break;
                }
                var body = l.ReadHeredocBody(delim.Text, t.Kind == TokenKind.HeredocDash);
                tokens.Add(new Token(TokenKind.HeredocBody, body, delim.Line, delim.Column));
            }
        }
        return tokens;
    }

    /// <summary>
    /// Greedily consume source lines from the current cursor until a line whose trimmed
    /// content equals <paramref name="delimiter"/>. If <paramref name="stripLeadingTabs"/>
    /// is set, each body line has its leading tabs stripped (matching <c>&lt;&lt;-</c>
    /// semantics). The delimiter line itself is consumed but not included in the body.
    /// </summary>
    public string ReadHeredocBody(string delimiter, bool stripLeadingTabs)
    {
        var unquoted = StripDelimiterQuotes(delimiter);
        var sb = new StringBuilder();
        while (_pos < _src.Length)
        {
            int lineStart = _pos;
            while (_pos < _src.Length && _src[_pos] != '\n' && _src[_pos] != '\r') _pos++;
            var line = _src.Substring(lineStart, _pos - lineStart);
            var trimmed = stripLeadingTabs ? line.TrimStart('\t') : line;
            // End-of-line handling.
            int eol = _pos;
            if (_pos < _src.Length && _src[_pos] == '\r') _pos++;
            if (_pos < _src.Length && _src[_pos] == '\n') { _pos++; _line++; _col = 1; }
            if (trimmed == unquoted) return sb.ToString();
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(trimmed);
        }
        // EOF before delimiter — emit what we have.
        return sb.ToString();
    }

    private static string StripDelimiterQuotes(string d)
    {
        if (d.Length >= 2 && d[0] == '"' && d[^1] == '"') return d.Substring(1, d.Length - 2);
        if (d.Length >= 2 && d[0] == '\'' && d[^1] == '\'') return d.Substring(1, d.Length - 2);
        return d;
    }

    public Token Next()
    {
        SkipInlineWhitespaceAndComments();
        if (_pos >= _src.Length) return new Token(TokenKind.EndOfFile, "", _line, _col);
        var c = _src[_pos];

        if (c == '\r' || c == '\n') return ReadNewline();

        switch (c)
        {
            case '|':
                Advance();
                if (Peek() == '|') { Advance(); return new Token(TokenKind.OrIf, "", _line, _col - 2); }
                return new Token(TokenKind.Pipe, "", _line, _col - 1);
            case '&':
                Advance();
                if (Peek() == '&') { Advance(); return new Token(TokenKind.AndIf, "", _line, _col - 2); }
                if (Peek() == '>') { Advance(); return new Token(TokenKind.RedirAll, "", _line, _col - 2); }
                return new Token(TokenKind.Ampersand, "", _line, _col - 1);
            case ';':
                Advance();
                return new Token(TokenKind.Semicolon, "", _line, _col - 1);
            case '>':
                Advance();
                if (Peek() == '>') { Advance(); return new Token(TokenKind.RedirAppend, "", _line, _col - 2); }
                return new Token(TokenKind.RedirOut, "", _line, _col - 1);
            case '<':
                Advance();
                if (Peek() == '<')
                {
                    Advance();
                    if (Peek() == '<') { Advance(); return new Token(TokenKind.HereString, "", _line, _col - 3); }
                    if (Peek() == '-') { Advance(); return new Token(TokenKind.HeredocDash, "", _line, _col - 3); }
                    return new Token(TokenKind.Heredoc, "", _line, _col - 2);
                }
                return new Token(TokenKind.RedirIn, "", _line, _col - 1);
            case '(':
                Advance();
                return new Token(TokenKind.LParen, "", _line, _col - 1);
            case ')':
                Advance();
                return new Token(TokenKind.RParen, "", _line, _col - 1);
            case '{':
                // Real bash treats `{` as a reserved word only when followed by whitespace
                // and at a statement boundary; otherwise it's part of a word (e.g. brace
                // expansion `{a,b}`).
                if (IsAtWordBoundary() && _pos + 1 < _src.Length && char.IsWhiteSpace(_src[_pos + 1]))
                {
                    Advance();
                    return new Token(TokenKind.LBrace, "", _line, _col - 1);
                }
                break;
            case '}':
                // Matching rule: `}` ends a block only when it's its own word (preceded by
                // whitespace, `;`, or newline).
                if (IsAtWordBoundary(true))
                {
                    Advance();
                    return new Token(TokenKind.RBrace, "", _line, _col - 1);
                }
                break;
        }

        if (c == '2' && Peek(1) == '>')
        {
            var startCol = _col;
            Advance(); Advance();
            return new Token(TokenKind.RedirErr, "", _line, startCol);
        }

        return ReadWord();
    }

    private Token ReadNewline()
    {
        var startLine = _line;
        var startCol = _col;
        if (_src[_pos] == '\r' && Peek(1) == '\n') { Advance(); Advance(); }
        else Advance();
        _line++;
        _col = 1;
        return new Token(TokenKind.Newline, "", startLine, startCol);
    }

    private Token ReadWord()
    {
        var startLine = _line;
        var startCol = _col;
        var sb = new StringBuilder();

        while (_pos < _src.Length)
        {
            var ch = _src[_pos];
            if (char.IsWhiteSpace(ch)) break;
            if (ch == '|' || ch == '&' || ch == ';' || ch == '<' || ch == '>' || ch == '(' || ch == ')') break;
            if (ch == '#' && sb.Length == 0) break;

            if (ch == '\\' && _pos + 1 < _src.Length)
            {
                var next = _src[_pos + 1];
                if (next == '\n' || next == '\r')
                {
                    // Line continuation: eat both.
                    if (next == '\r' && Peek(2) == '\n') Advance();
                    Advance(); Advance();
                    _line++;
                    _col = 1;
                    continue;
                }
                sb.Append('\\');
                sb.Append(next);
                Advance(); Advance();
                continue;
            }

            if (ch == '\'')
            {
                sb.Append('\'');
                Advance();
                while (_pos < _src.Length && _src[_pos] != '\'')
                {
                    sb.Append(_src[_pos]);
                    if (_src[_pos] == '\n') { _line++; _col = 0; }
                    Advance();
                }
                if (_pos >= _src.Length) throw new BashParseException("Unterminated single-quoted string.", _line, _col);
                sb.Append('\'');
                Advance();
                continue;
            }

            if (ch == '"')
            {
                sb.Append('"');
                Advance();
                while (_pos < _src.Length && _src[_pos] != '"')
                {
                    if (_src[_pos] == '\\' && _pos + 1 < _src.Length)
                    {
                        sb.Append(_src[_pos]);
                        sb.Append(_src[_pos + 1]);
                        Advance(); Advance();
                        continue;
                    }
                    sb.Append(_src[_pos]);
                    if (_src[_pos] == '\n') { _line++; _col = 0; }
                    Advance();
                }
                if (_pos >= _src.Length) throw new BashParseException("Unterminated double-quoted string.", _line, _col);
                sb.Append('"');
                Advance();
                continue;
            }

            if (ch == '$' && Peek(1) == '(')
            {
                // $( ... ) — capture with paren nesting so $(echo $(foo)) parses cleanly.
                sb.Append('$');
                sb.Append('(');
                Advance(); Advance();
                int depth = 1;
                while (_pos < _src.Length && depth > 0)
                {
                    var cur = _src[_pos];
                    if (cur == '(') depth++;
                    else if (cur == ')') { depth--; if (depth == 0) { sb.Append(')'); Advance(); break; } }
                    sb.Append(cur);
                    if (cur == '\n') { _line++; _col = 0; }
                    Advance();
                }
                continue;
            }

            if (ch == '`')
            {
                sb.Append('`');
                Advance();
                while (_pos < _src.Length && _src[_pos] != '`')
                {
                    sb.Append(_src[_pos]);
                    if (_src[_pos] == '\n') { _line++; _col = 0; }
                    Advance();
                }
                if (_pos < _src.Length && _src[_pos] == '`') { sb.Append('`'); Advance(); }
                continue;
            }

            sb.Append(ch);
            Advance();
        }

        return new Token(TokenKind.Word, sb.ToString(), startLine, startCol);
    }

    private void SkipInlineWhitespaceAndComments()
    {
        while (_pos < _src.Length)
        {
            var ch = _src[_pos];
            if (ch == ' ' || ch == '\t') { Advance(); continue; }
            if (ch == '#' && (_pos == 0 || char.IsWhiteSpace(_src[_pos - 1]) || _src[_pos - 1] == '\n' || _src[_pos - 1] == ';'))
            {
                while (_pos < _src.Length && _src[_pos] != '\n' && _src[_pos] != '\r') Advance();
                continue;
            }
            break;
        }
    }

    private bool IsAtWordBoundary(bool trailing = false)
    {
        if (trailing)
        {
            if (_pos == 0) return true;
            var prev = _src[_pos - 1];
            return prev == ';' || prev == '&' || prev == '\n' || char.IsWhiteSpace(prev);
        }
        return _pos == 0 || char.IsWhiteSpace(_src[_pos - 1]) || _src[_pos - 1] == ';' || _src[_pos - 1] == '\n';
    }

    private char Peek(int offset = 0) => _pos + offset < _src.Length ? _src[_pos + offset] : '\0';
    private void Advance() { _pos++; _col++; }
}
