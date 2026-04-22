using System.Text;
using CarbideCmd.Errors;

namespace CarbideCmd.Lexer;

/// <summary>
/// Line-oriented cmd lexer. Input is a full script; output is a flat token stream that the
/// parser walks. The lexer preserves raw argument text (including <c>%VAR%</c> markers) and
/// defers variable expansion to the interpreter. Quoted strings retain their quotes so the
/// interpreter can honor them when forwarding to cross-shell launchers.
/// <para>
/// Comments (<c>REM ...</c>, <c>::</c>) and the <c>@</c> echo-suppress prefix are handled at
/// the lexer layer: <c>REM</c> consumes the rest of the line and emits no word tokens; <c>::</c>
/// does the same; <c>@</c> emits a single <see cref="TokenKind.At"/> that the parser absorbs.
/// </para>
/// </summary>
public sealed class CmdLexer
{
    private readonly string _src;
    private int _pos;
    private int _line = 1;
    private int _col = 1;

    public CmdLexer(string source)
    {
        _src = source ?? "";
    }

    public static List<Token> Tokenize(string source)
    {
        var l = new CmdLexer(source);
        var tokens = new List<Token>();
        while (true)
        {
            var t = l.Next();
            tokens.Add(t);
            if (t.Kind == TokenKind.EndOfFile) break;
        }
        return tokens;
    }

    public Token Next()
    {
        SkipInlineWhitespace();

        if (_pos >= _src.Length)
            return new Token(TokenKind.EndOfFile, "", _line, _col);

        var c = _src[_pos];

        if (c == '\r' || c == '\n') return ReadNewline();

        // Label at start of line.
        if (c == ':' && IsAtLineStart())
        {
            if (_pos + 1 < _src.Length && _src[_pos + 1] == ':')
            {
                SkipToEndOfLine();
                return Next();
            }
            return ReadLabel();
        }

        // :: comment anywhere that's the first non-whitespace of a line.
        if (c == ':' && _pos + 1 < _src.Length && _src[_pos + 1] == ':' && IsAtLineStart())
        {
            SkipToEndOfLine();
            return Next();
        }

        switch (c)
        {
            case '@' when IsAtLineStart():
                Advance();
                return new Token(TokenKind.At, "", _line, _col - 1);
            case '|':
                Advance();
                if (Peek() == '|') { Advance(); return new Token(TokenKind.PipePipe, "", _line, _col - 2); }
                return new Token(TokenKind.Pipe, "", _line, _col - 1);
            case '&':
                Advance();
                if (Peek() == '&') { Advance(); return new Token(TokenKind.AmpAmp, "", _line, _col - 2); }
                return new Token(TokenKind.Amp, "", _line, _col - 1);
            case '>':
                Advance();
                if (Peek() == '>') { Advance(); return new Token(TokenKind.RedirAppend, "", _line, _col - 2); }
                return new Token(TokenKind.RedirOut, "", _line, _col - 1);
            case '<':
                Advance();
                return new Token(TokenKind.RedirIn, "", _line, _col - 1);
            case '(':
                Advance();
                return new Token(TokenKind.LParen, "", _line, _col - 1);
            case ')':
                Advance();
                return new Token(TokenKind.RParen, "", _line, _col - 1);
        }

        // `2>` and `2>&1` forms.
        if (c == '2' && _pos + 1 < _src.Length && _src[_pos + 1] == '>')
        {
            if (_pos + 3 < _src.Length && _src[_pos + 2] == '&' && _src[_pos + 3] == '1')
            {
                var startCol = _col;
                Advance(); Advance(); Advance(); Advance();
                return new Token(TokenKind.RedirMerge, "", _line, startCol);
            }
            else
            {
                var startCol = _col;
                Advance(); Advance();
                return new Token(TokenKind.RedirErr, "", _line, startCol);
            }
        }

        return ReadWord();
    }

    private Token ReadLabel()
    {
        var startLine = _line;
        var startCol = _col;
        Advance(); // consume ':'
        var sb = new StringBuilder();
        while (_pos < _src.Length)
        {
            var ch = _src[_pos];
            if (ch == '\r' || ch == '\n' || char.IsWhiteSpace(ch)) break;
            sb.Append(ch);
            Advance();
        }
        return new Token(TokenKind.Label, sb.ToString(), startLine, startCol);
    }

    private Token ReadNewline()
    {
        var startLine = _line;
        var startCol = _col;
        if (_src[_pos] == '\r' && Peek(1) == '\n')
        {
            Advance();
            Advance();
        }
        else
        {
            Advance();
        }
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

            if (ch == '\r' || ch == '\n') break;
            if (char.IsWhiteSpace(ch)) break;

            if (IsOperatorChar(ch))
            {
                // Don't swallow operators.
                break;
            }

            if (ch == '^' && _pos + 1 < _src.Length)
            {
                // Escape next char. The escape itself is consumed; the next char becomes literal.
                Advance();
                if (_pos < _src.Length)
                {
                    var next = _src[_pos];
                    if (next == '\r' || next == '\n')
                    {
                        // Line continuation — consume newline and continue the word.
                        if (next == '\r' && Peek(1) == '\n') Advance();
                        Advance();
                        _line++;
                        _col = 1;
                        continue;
                    }
                    sb.Append(next);
                    Advance();
                }
                continue;
            }

            if (ch == '"')
            {
                sb.Append('"');
                Advance();
                while (_pos < _src.Length && _src[_pos] != '"' && _src[_pos] != '\r' && _src[_pos] != '\n')
                {
                    sb.Append(_src[_pos]);
                    Advance();
                }
                if (_pos < _src.Length && _src[_pos] == '"')
                {
                    sb.Append('"');
                    Advance();
                }
                else
                {
                    throw new CmdParseException("Unterminated double-quoted string.", _line, _col);
                }
                continue;
            }

            sb.Append(ch);
            Advance();
        }

        var text = sb.ToString();
        // Recognize REM as a line-comment keyword if it's the first token of the line.
        if (text.Equals("REM", StringComparison.OrdinalIgnoreCase) && IsAtLineStartBefore(startCol))
        {
            SkipToEndOfLine();
            return Next();
        }

        return new Token(TokenKind.Word, text, startLine, startCol);
    }

    private bool IsOperatorChar(char ch)
        => ch == '|' || ch == '&' || ch == '<' || ch == '>' || ch == '(' || ch == ')';

    private bool IsAtLineStart()
    {
        var p = _pos - 1;
        while (p >= 0)
        {
            var ch = _src[p];
            if (ch == '\n' || ch == '\r') return true;
            if (!char.IsWhiteSpace(ch)) return false;
            p--;
        }
        return true;
    }

    private bool IsAtLineStartBefore(int startCol) => startCol == 1 || IsAtLineStart();

    private void SkipInlineWhitespace()
    {
        while (_pos < _src.Length)
        {
            var ch = _src[_pos];
            if (ch == ' ' || ch == '\t') Advance();
            else break;
        }
    }

    private void SkipToEndOfLine()
    {
        while (_pos < _src.Length && _src[_pos] != '\r' && _src[_pos] != '\n')
            Advance();
    }

    private char Peek(int offset = 0) => _pos + offset < _src.Length ? _src[_pos + offset] : '\0';

    private void Advance() { _pos++; _col++; }
}
