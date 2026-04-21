using System.Text;
using CarbidePwsh.Errors;

namespace CarbidePwsh.Lexer;

/// <summary>
/// Hand-rolled tokenizer for the Phase 1 PowerShell-subset language. Emits a flat token stream
/// including explicit <see cref="TokenKind.NewLine"/> tokens — the parser decides where newlines
/// terminate statements and where they're swallowed (inside parentheses, arrays, hashtables).
/// </summary>
public sealed class Lexer
{
    private readonly string _source;
    private int _pos;
    private int _line = 1;
    private int _col = 1;
    // Set after emitting a '.' or '::' token. Suppresses hyphen-folding for the next
    // identifier so member names stay unhyphenated: `$a.foo-bar` is `$a.foo - bar`, not
    // `$a."foo-bar"`.
    private bool _suppressHyphenFoldOnce;

    private static readonly Dictionary<string, TokenKind> DashedOps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eq"] = TokenKind.OpEq,
        ["ne"] = TokenKind.OpNe,
        ["lt"] = TokenKind.OpLt,
        ["le"] = TokenKind.OpLe,
        ["gt"] = TokenKind.OpGt,
        ["ge"] = TokenKind.OpGe,
        ["ieq"] = TokenKind.OpIeq,
        ["ine"] = TokenKind.OpIne,
        ["ilt"] = TokenKind.OpIlt,
        ["ile"] = TokenKind.OpIle,
        ["igt"] = TokenKind.OpIgt,
        ["ige"] = TokenKind.OpIge,
        ["ceq"] = TokenKind.OpCeq,
        ["cne"] = TokenKind.OpCne,
        ["clt"] = TokenKind.OpClt,
        ["cle"] = TokenKind.OpCle,
        ["cgt"] = TokenKind.OpCgt,
        ["cge"] = TokenKind.OpCge,
        ["and"] = TokenKind.OpAnd,
        ["or"] = TokenKind.OpOr,
        ["xor"] = TokenKind.OpXor,
        ["not"] = TokenKind.OpNot,
        ["band"] = TokenKind.OpBand,
        ["bor"] = TokenKind.OpBor,
        ["bxor"] = TokenKind.OpBxor,
        ["bnot"] = TokenKind.OpBnot,
        ["is"] = TokenKind.OpIs,
        ["isnot"] = TokenKind.OpIsNot,
        ["as"] = TokenKind.OpAs,

        // Phase 3 — regex, glob, format, collection operators
        ["match"] = TokenKind.OpMatch,
        ["imatch"] = TokenKind.OpIMatch,
        ["cmatch"] = TokenKind.OpCMatch,
        ["notmatch"] = TokenKind.OpNotMatch,
        ["inotmatch"] = TokenKind.OpINotMatch,
        ["cnotmatch"] = TokenKind.OpCNotMatch,
        ["replace"] = TokenKind.OpReplace,
        ["ireplace"] = TokenKind.OpIReplace,
        ["creplace"] = TokenKind.OpCReplace,
        ["like"] = TokenKind.OpLike,
        ["ilike"] = TokenKind.OpILike,
        ["clike"] = TokenKind.OpCLike,
        ["notlike"] = TokenKind.OpNotLike,
        ["inotlike"] = TokenKind.OpINotLike,
        ["cnotlike"] = TokenKind.OpCNotLike,
        ["contains"] = TokenKind.OpContains,
        ["icontains"] = TokenKind.OpICContains,
        ["ccontains"] = TokenKind.OpCContains,
        ["notcontains"] = TokenKind.OpNotContains,
        ["inotcontains"] = TokenKind.OpINotContains,
        ["cnotcontains"] = TokenKind.OpCNotContains,
        ["in"] = TokenKind.OpIn,
        ["notin"] = TokenKind.OpNotIn,
        ["cin"] = TokenKind.OpCIn,
        ["cnotin"] = TokenKind.OpCNotIn,
        ["iin"] = TokenKind.OpIIn,
        ["inotin"] = TokenKind.OpINotIn,
        ["f"] = TokenKind.OpFormat,
        ["join"] = TokenKind.OpJoin,
        ["split"] = TokenKind.OpSplit,
    };

    public Lexer(string source)
    {
        _source = source;
    }

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            SkipHorizontalWhitespaceAndComments();
            if (_pos >= _source.Length)
            {
                tokens.Add(Token.Eof(CurrentLocation(0)));
                return tokens;
            }
            var ch = _source[_pos];
            if (ch == '\r' || ch == '\n')
            {
                var start = SnapshotLocation();
                if (ch == '\r' && Peek(1) == '\n') Advance();
                Advance();
                _line++;
                _col = 1;
                tokens.Add(new Token(TokenKind.NewLine, "\n", null, LocationFrom(start)));
                _suppressHyphenFoldOnce = false;
                continue;
            }
            if (ch == '`' && (Peek(1) == '\r' || Peek(1) == '\n'))
            {
                // Line continuation: consume the backtick and the newline.
                Advance();
                if (_source[_pos] == '\r' && Peek(1) == '\n') Advance();
                Advance();
                _line++;
                _col = 1;
                continue;
            }
            var tok = LexOne();
            tokens.Add(tok);
            // After emitting a '.' or '::', the next identifier is treated as a member name
            // without hyphen folding. Other tokens clear the flag.
            _suppressHyphenFoldOnce = tok.Kind is TokenKind.Dot or TokenKind.ColonColon;
        }
    }

    private Token LexOne()
    {
        var ch = _source[_pos];
        if (IsDigit(ch) || (ch == '.' && IsDigit(Peek(1))))
            return LexNumber();
        if (ch == '"')
            return LexDoubleQuoted();
        if (ch == '\'')
            return LexSingleQuoted();
        if (ch == '@' && Peek(1) == '"')
            return LexDoubleQuotedHereString();
        if (ch == '@' && Peek(1) == '\'')
            return LexSingleQuotedHereString();
        if (ch == '$')
            return LexDollar();
        if (ch == '@' && Peek(1) == '(') { return TwoCharToken(TokenKind.AtLParen, "@("); }
        if (ch == '@' && Peek(1) == '{') { return TwoCharToken(TokenKind.AtLBrace, "@{"); }
        if (ch == '(' ) return OneCharToken(TokenKind.LParen, "(");
        if (ch == ')' ) return OneCharToken(TokenKind.RParen, ")");
        if (ch == '[' ) return OneCharToken(TokenKind.LBracket, "[");
        if (ch == ']' ) return OneCharToken(TokenKind.RBracket, "]");
        if (ch == '{' ) return OneCharToken(TokenKind.LBrace, "{");
        if (ch == '}' ) return OneCharToken(TokenKind.RBrace, "}");
        if (ch == ',' ) return OneCharToken(TokenKind.Comma, ",");
        if (ch == ';' ) return OneCharToken(TokenKind.Semicolon, ";");

        if (ch == ':' && Peek(1) == ':') return TwoCharToken(TokenKind.ColonColon, "::");
        if (ch == '.' && Peek(1) == '.') return TwoCharToken(TokenKind.DotDot, "..");
        if (ch == '.') return OneCharToken(TokenKind.Dot, ".");

        if (ch == '=') return OneCharToken(TokenKind.Equal, "=");
        if (ch == '+' && Peek(1) == '=') return TwoCharToken(TokenKind.PlusEqual, "+=");
        if (ch == '-' && Peek(1) == '=') return TwoCharToken(TokenKind.MinusEqual, "-=");
        if (ch == '*' && Peek(1) == '=') return TwoCharToken(TokenKind.StarEqual, "*=");
        if (ch == '/' && Peek(1) == '=') return TwoCharToken(TokenKind.SlashEqual, "/=");
        if (ch == '%' && Peek(1) == '=') return TwoCharToken(TokenKind.PercentEqual, "%=");

        if (ch == '+' && Peek(1) == '+') return TwoCharToken(TokenKind.PlusPlus, "++");
        if (ch == '+') return OneCharToken(TokenKind.Plus, "+");
        if (ch == '*') return OneCharToken(TokenKind.Star, "*");
        if (ch == '/') return OneCharToken(TokenKind.Slash, "/");
        if (ch == '%') return OneCharToken(TokenKind.Percent, "%");
        if (ch == '!') return OneCharToken(TokenKind.Bang, "!");
        if (ch == '|') return OneCharToken(TokenKind.Pipe, "|");
        if (ch == '&') return OneCharToken(TokenKind.Ampersand, "&");

        if (ch == '-')
            return LexDashOperatorOrMinus();

        if (IsIdentifierStart(ch))
            return LexIdentifier();

        throw new PwshParseException($"Unexpected character '{ch}'.", CurrentLocation(1));
    }

    // ---------- Number ----------

    private Token LexNumber()
    {
        var start = SnapshotLocation();
        var startPos = _pos;
        bool isFloat = false;
        bool isHex = false;

        if (_source[_pos] == '0' && (Peek(1) == 'x' || Peek(1) == 'X'))
        {
            isHex = true;
            Advance(); Advance();
            while (_pos < _source.Length && IsHexDigit(_source[_pos])) Advance();
        }
        else
        {
            while (_pos < _source.Length && IsDigit(_source[_pos])) Advance();
            if (_pos < _source.Length && _source[_pos] == '.' && IsDigit(Peek(1)))
            {
                isFloat = true;
                Advance();
                while (_pos < _source.Length && IsDigit(_source[_pos])) Advance();
            }
            if (_pos < _source.Length && (_source[_pos] == 'e' || _source[_pos] == 'E'))
            {
                isFloat = true;
                Advance();
                if (_pos < _source.Length && (_source[_pos] == '+' || _source[_pos] == '-'))
                    Advance();
                while (_pos < _source.Length && IsDigit(_source[_pos])) Advance();
            }
        }

        var text = _source.Substring(startPos, _pos - startPos);
        object value;
        if (isHex)
        {
            var hexBody = text.AsSpan(2);
            if (long.TryParse(hexBody, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var lv))
            {
                value = lv >= int.MinValue && lv <= int.MaxValue ? (int)lv : (object)lv;
            }
            else
            {
                throw new PwshParseException($"Invalid hex literal '{text}'.", LocationFrom(start));
            }
        }
        else if (isFloat)
        {
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
                throw new PwshParseException($"Invalid numeric literal '{text}'.", LocationFrom(start));
            value = d;
        }
        else
        {
            if (long.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var lv))
            {
                value = lv >= int.MinValue && lv <= int.MaxValue ? (int)lv : (object)lv;
            }
            else if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
            {
                value = d;
            }
            else
            {
                throw new PwshParseException($"Invalid numeric literal '{text}'.", LocationFrom(start));
            }
        }

        return new Token(TokenKind.Number, text, value, LocationFrom(start));
    }

    // ---------- Strings ----------

    private Token LexDoubleQuoted()
    {
        var start = SnapshotLocation();
        Advance(); // consume opening "
        var parts = new List<StringPart>();
        var buf = new StringBuilder();

        while (_pos < _source.Length)
        {
            var c = _source[_pos];
            if (c == '"')
            {
                Advance();
                if (buf.Length > 0) { parts.Add(new LiteralPart(buf.ToString())); buf.Clear(); }
                return new Token(TokenKind.String, "\"...\"", parts, LocationFrom(start));
            }
            if (c == '`')
            {
                Advance();
                if (_pos >= _source.Length)
                    throw new PwshParseException("Unterminated backtick escape in string.", LocationFrom(start));
                var esc = _source[_pos]; Advance();
                buf.Append(esc switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '0' => '\0',
                    'a' => '\a',
                    'b' => '\b',
                    'f' => '\f',
                    'v' => '\v',
                    '"' => '"',
                    '\'' => '\'',
                    '`' => '`',
                    '$' => '$',
                    _ => esc,
                });
                continue;
            }
            if (c == '$' && Peek(1) == '(')
            {
                if (buf.Length > 0) { parts.Add(new LiteralPart(buf.ToString())); buf.Clear(); }
                var exprLoc = SnapshotLocation();
                Advance(); Advance(); // consume $(
                var depth = 1;
                var sb = new StringBuilder();
                while (_pos < _source.Length && depth > 0)
                {
                    var cc = _source[_pos];
                    if (cc == '(') depth++;
                    else if (cc == ')')
                    {
                        depth--;
                        if (depth == 0) break;
                    }
                    else if (cc == '"')
                    {
                        // Consume nested double-quoted string verbatim to preserve balance of parens inside.
                        sb.Append(cc);
                        Advance();
                        while (_pos < _source.Length && _source[_pos] != '"')
                        {
                            if (_source[_pos] == '`' && _pos + 1 < _source.Length)
                            {
                                sb.Append(_source[_pos]); Advance();
                                sb.Append(_source[_pos]); Advance();
                                continue;
                            }
                            sb.Append(_source[_pos]); Advance();
                        }
                        if (_pos < _source.Length) { sb.Append(_source[_pos]); Advance(); }
                        continue;
                    }
                    else if (cc == '\'')
                    {
                        sb.Append(cc); Advance();
                        while (_pos < _source.Length && _source[_pos] != '\'')
                        {
                            sb.Append(_source[_pos]); Advance();
                        }
                        if (_pos < _source.Length) { sb.Append(_source[_pos]); Advance(); }
                        continue;
                    }
                    if (cc == '\n') { _line++; _col = 0; }
                    sb.Append(cc); Advance();
                }
                if (depth != 0)
                    throw new PwshIncompleteInputException("Unterminated $(...) subexpression in string.", LocationFrom(exprLoc));
                Advance(); // consume )
                parts.Add(new ExpressionPart(sb.ToString(), LocationFrom(exprLoc)));
                continue;
            }
            if (c == '$' && (IsIdentifierStart(Peek(1)) || Peek(1) == '{'))
            {
                if (buf.Length > 0) { parts.Add(new LiteralPart(buf.ToString())); buf.Clear(); }
                Advance(); // $
                if (_pos < _source.Length && _source[_pos] == '{')
                {
                    Advance(); // {
                    var nameSb = new StringBuilder();
                    while (_pos < _source.Length && _source[_pos] != '}')
                    {
                        nameSb.Append(_source[_pos]); Advance();
                    }
                    if (_pos < _source.Length) Advance(); // }
                    parts.Add(ParseVariableFragment(nameSb.ToString()));
                }
                else
                {
                    var nameSb = new StringBuilder();
                    while (_pos < _source.Length && IsIdentifierPart(_source[_pos]))
                    {
                        nameSb.Append(_source[_pos]); Advance();
                    }
                    var first = nameSb.ToString();
                    // Scope prefix? e.g. $env:PATH in string interpolation
                    if (_pos < _source.Length && _source[_pos] == ':' && Peek(1) != ':' && IsIdentifierStart(Peek(1)))
                    {
                        Advance(); // :
                        var nm = new StringBuilder();
                        while (_pos < _source.Length && IsIdentifierPart(_source[_pos]))
                        {
                            nm.Append(_source[_pos]); Advance();
                        }
                        parts.Add(new VariablePart(first, nm.ToString()));
                    }
                    else
                    {
                        parts.Add(new VariablePart(null, first));
                    }
                }
                continue;
            }
            if (c == '\n') { _line++; _col = 0; }
            buf.Append(c); Advance();
        }

        throw new PwshIncompleteInputException("Unterminated double-quoted string.", LocationFrom(start));
    }

    private Token LexSingleQuoted()
    {
        var start = SnapshotLocation();
        Advance(); // consume opening '
        var sb = new StringBuilder();
        while (_pos < _source.Length)
        {
            var c = _source[_pos];
            if (c == '\'')
            {
                Advance();
                if (_pos < _source.Length && _source[_pos] == '\'')
                {
                    sb.Append('\''); Advance(); continue;
                }
                return new Token(TokenKind.String, "'...'",
                    new List<StringPart> { new LiteralPart(sb.ToString()) },
                    LocationFrom(start));
            }
            if (c == '\n') { _line++; _col = 0; }
            sb.Append(c); Advance();
        }
        throw new PwshIncompleteInputException("Unterminated single-quoted string.", LocationFrom(start));
    }

    private Token LexDoubleQuotedHereString()
    {
        // @" ... "@   — multi-line, with interpolation. Closing "@ must be at col 1.
        var start = SnapshotLocation();
        Advance(); Advance(); // @"
        // Skip the rest of the line until the first newline.
        while (_pos < _source.Length && _source[_pos] != '\n' && _source[_pos] != '\r') Advance();
        if (_pos < _source.Length && _source[_pos] == '\r') Advance();
        if (_pos < _source.Length && _source[_pos] == '\n') { Advance(); _line++; _col = 1; }

        var body = new StringBuilder();
        while (_pos < _source.Length)
        {
            // Check for closing "@ at column 1.
            if (_col == 1 && _pos + 1 < _source.Length && _source[_pos] == '"' && _source[_pos + 1] == '@')
            {
                Advance(); Advance();
                return new Token(TokenKind.String, "@\"...\"@",
                    new List<StringPart> { new LiteralPart(body.ToString()) },
                    LocationFrom(start));
            }
            if (_source[_pos] == '\n') { _line++; _col = 0; }
            body.Append(_source[_pos]); Advance();
        }
        throw new PwshIncompleteInputException("Unterminated double-quoted here-string.", LocationFrom(start));
    }

    private Token LexSingleQuotedHereString()
    {
        var start = SnapshotLocation();
        Advance(); Advance(); // @'
        while (_pos < _source.Length && _source[_pos] != '\n' && _source[_pos] != '\r') Advance();
        if (_pos < _source.Length && _source[_pos] == '\r') Advance();
        if (_pos < _source.Length && _source[_pos] == '\n') { Advance(); _line++; _col = 1; }

        var body = new StringBuilder();
        while (_pos < _source.Length)
        {
            if (_col == 1 && _pos + 1 < _source.Length && _source[_pos] == '\'' && _source[_pos + 1] == '@')
            {
                Advance(); Advance();
                return new Token(TokenKind.String, "@'...'@",
                    new List<StringPart> { new LiteralPart(body.ToString()) },
                    LocationFrom(start));
            }
            if (_source[_pos] == '\n') { _line++; _col = 0; }
            body.Append(_source[_pos]); Advance();
        }
        throw new PwshIncompleteInputException("Unterminated single-quoted here-string.", LocationFrom(start));
    }

    private static VariablePart ParseVariableFragment(string inside)
    {
        var idx = inside.IndexOf(':');
        if (idx >= 0)
            return new VariablePart(inside.Substring(0, idx), inside.Substring(idx + 1));
        return new VariablePart(null, inside);
    }

    // ---------- Dollar (variable or $() ) ----------

    private Token LexDollar()
    {
        var start = SnapshotLocation();
        Advance(); // $
        if (_pos < _source.Length && _source[_pos] == '(')
        {
            Advance();
            return new Token(TokenKind.DollarLParen, "$(", null, LocationFrom(start));
        }
        if (_pos < _source.Length && _source[_pos] == '{')
        {
            Advance();
            var nameSb = new StringBuilder();
            while (_pos < _source.Length && _source[_pos] != '}')
            {
                nameSb.Append(_source[_pos]); Advance();
            }
            if (_pos >= _source.Length)
                throw new PwshParseException("Unterminated ${...} variable reference.", LocationFrom(start));
            Advance(); // }
            var v = ParseVariableFragment(nameSb.ToString());
            return new Token(TokenKind.Variable, "${" + nameSb + "}", (v.Scope, v.Name), LocationFrom(start));
        }

        // Special single-char automatic variables: $?, $^, $$.
        if (_pos < _source.Length)
        {
            var ch0 = _source[_pos];
            if (ch0 == '?' || ch0 == '^' || ch0 == '$')
            {
                Advance();
                return new Token(TokenKind.Variable, "$" + ch0, ((string?)null, ch0.ToString()), LocationFrom(start));
            }
        }

        var nm = new StringBuilder();
        while (_pos < _source.Length && IsIdentifierPart(_source[_pos]))
        {
            nm.Append(_source[_pos]); Advance();
        }
        var first = nm.ToString();
        if (first.Length == 0)
            throw new PwshParseException("Expected a variable name after '$'.", LocationFrom(start));

        string? scope = null;
        string name = first;
        if (_pos < _source.Length && _source[_pos] == ':' && Peek(1) != ':' && IsIdentifierStart(Peek(1)))
        {
            Advance(); // :
            var nm2 = new StringBuilder();
            while (_pos < _source.Length && IsIdentifierPart(_source[_pos]))
            {
                nm2.Append(_source[_pos]); Advance();
            }
            scope = first;
            name = nm2.ToString();
        }

        return new Token(TokenKind.Variable, "$" + first + (scope != null ? ":" + name : ""), (scope, name), LocationFrom(start));
    }

    // ---------- Dashed operator vs minus ----------

    private Token LexDashOperatorOrMinus()
    {
        var start = SnapshotLocation();
        // `--` is decrement.
        if (Peek(1) == '-')
        {
            Advance(); Advance();
            return new Token(TokenKind.MinusMinus, "--", null, LocationFrom(start));
        }
        // If we see -word where word is a known dashed operator, emit the operator token.
        if (Peek(1) != '\0' && IsIdentifierStart(Peek(1)))
        {
            var probe = _pos + 1;
            var sb = new StringBuilder();
            while (probe < _source.Length && IsIdentifierPart(_source[probe]))
            {
                sb.Append(_source[probe]);
                probe++;
            }
            var word = sb.ToString();
            if (DashedOps.TryGetValue(word, out var kind))
            {
                // Advance past - and the word.
                _pos = probe;
                _col += 1 + word.Length;
                return new Token(kind, "-" + word, null, LocationFrom(start));
            }
        }
        // Plain minus.
        Advance();
        return new Token(TokenKind.Minus, "-", null, LocationFrom(start));
    }

    // ---------- Identifier ----------

    private Token LexIdentifier()
    {
        var start = SnapshotLocation();
        var startPos = _pos;
        while (_pos < _source.Length && IsIdentifierPart(_source[_pos])) Advance();

        // Fold hyphenated command-name sequences: if the next chars are `-Identifier` with no
        // whitespace and the word after the hyphen isn't a known dashed operator, absorb them
        // into the current identifier. Enables `Get-ChildItem`, `ConvertTo-Json`, etc. to lex
        // as single tokens. Adjacency is source-index based so a space breaks the fold.
        // Suppressed for the first identifier after `.` or `::` so member-access receivers
        // stay unhyphenated (e.g. `$a.foo-bar` is `$a.foo - bar`, not `$a."foo-bar"`).
        if (!_suppressHyphenFoldOnce)
        {
            while (_pos + 1 < _source.Length
                   && _source[_pos] == '-'
                   && IsIdentifierStart(_source[_pos + 1]))
            {
                var probe = _pos + 1;
                var sb = new StringBuilder();
                while (probe < _source.Length && IsIdentifierPart(_source[probe]))
                {
                    sb.Append(_source[probe]);
                    probe++;
                }
                if (DashedOps.ContainsKey(sb.ToString())) break;
                Advance(); // -
                while (_pos < probe) Advance();
            }
        }

        var text = _source.Substring(startPos, _pos - startPos);
        return new Token(TokenKind.Identifier, text, text, LocationFrom(start));
    }

    // ---------- Utility ----------

    private Token OneCharToken(TokenKind kind, string text)
    {
        var start = SnapshotLocation();
        Advance();
        return new Token(kind, text, null, LocationFrom(start));
    }

    private Token TwoCharToken(TokenKind kind, string text)
    {
        var start = SnapshotLocation();
        Advance(); Advance();
        return new Token(kind, text, null, LocationFrom(start));
    }

    private void SkipHorizontalWhitespaceAndComments()
    {
        while (_pos < _source.Length)
        {
            var c = _source[_pos];
            if (c == ' ' || c == '\t') { Advance(); continue; }
            if (c == '#')
            {
                while (_pos < _source.Length && _source[_pos] != '\n' && _source[_pos] != '\r') Advance();
                continue;
            }
            if (c == '<' && Peek(1) == '#')
            {
                Advance(); Advance();
                while (_pos < _source.Length)
                {
                    if (_source[_pos] == '#' && Peek(1) == '>')
                    {
                        Advance(); Advance();
                        break;
                    }
                    if (_source[_pos] == '\n') { _line++; _col = 0; }
                    Advance();
                }
                continue;
            }
            return;
        }
    }

    private void Advance()
    {
        _pos++;
        _col++;
    }

    private char Peek(int delta)
    {
        var idx = _pos + delta;
        if (idx < 0 || idx >= _source.Length) return '\0';
        return _source[idx];
    }

    private (int line, int col, int pos) SnapshotLocation() => (_line, _col, _pos);

    private SourceLocation LocationFrom((int line, int col, int pos) start)
        => new(start.line, start.col, start.pos, _pos - start.pos);

    private SourceLocation CurrentLocation(int length)
        => new(_line, _col, _pos, length);

    private static bool IsDigit(char c) => c >= '0' && c <= '9';
    private static bool IsHexDigit(char c) => IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    private static bool IsIdentifierStart(char c) => c == '_' || char.IsLetter(c);
    private static bool IsIdentifierPart(char c) => c == '_' || char.IsLetterOrDigit(c);
}
