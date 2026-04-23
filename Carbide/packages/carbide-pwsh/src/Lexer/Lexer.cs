using System.Numerics;
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
        ["shl"] = TokenKind.OpShl,
        ["shr"] = TokenKind.OpShr,
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
        ["isplit"] = TokenKind.OpSplit,
        ["csplit"] = TokenKind.OpSplit,
    };

    private static readonly Dictionary<string, long> NumericSuffixMultipliers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kb"] = 1024L,
        ["mb"] = 1024L * 1024L,
        ["gb"] = 1024L * 1024L * 1024L,
        ["tb"] = 1024L * 1024L * 1024L * 1024L,
        ["pb"] = 1024L * 1024L * 1024L * 1024L * 1024L,
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
        if (IsRedirectionStart())
            return LexRedirection();
        if (IsDigit(ch) || (ch == '.' && IsDigit(Peek(1))))
            return LexNumber();
        if (IsDoubleQuoteChar(ch))
            return LexDoubleQuoted(ch, MatchingDoubleQuote(ch));
        if (IsSingleQuoteChar(ch))
            return LexSingleQuoted(ch, MatchingSingleQuote(ch));
        if (ch == '@' && Peek(1) == '"')
            return LexDoubleQuotedHereString();
        if (ch == '@' && Peek(1) == '\'')
            return LexSingleQuotedHereString();
        if (ch == '$')
            return LexDollar();
        if (ch == '@' && Peek(1) == '(') { return TwoCharToken(TokenKind.AtLParen, "@("); }
        if (ch == '@' && Peek(1) == '{') { return TwoCharToken(TokenKind.AtLBrace, "@{"); }
        if (ch == '@' && IsIdentifierStart(Peek(1)))
            return LexSplat();
        if (ch == '@') return OneCharTextToken(TokenKind.Identifier, "@", "@");
        if (ch == '`') return LexBacktickText();
        if (ch == '(' ) return OneCharToken(TokenKind.LParen, "(");
        if (ch == ')' ) return OneCharToken(TokenKind.RParen, ")");
        if (ch == '[' ) return OneCharToken(TokenKind.LBracket, "[");
        if (ch == ']' ) return OneCharToken(TokenKind.RBracket, "]");
        if (ch == '{' ) return OneCharToken(TokenKind.LBrace, "{");
        if (ch == '}' ) return OneCharToken(TokenKind.RBrace, "}");
        if (ch == ',' ) return OneCharToken(TokenKind.Comma, ",");
        if (ch == ';' ) return OneCharToken(TokenKind.Semicolon, ";");

        if (ch == ':' && Peek(1) == ':') return TwoCharToken(TokenKind.ColonColon, "::");
        if (ch == ':') return OneCharToken(TokenKind.Colon, ":");
        if (ch == '?' && Peek(1) == '?') return TwoCharToken(TokenKind.QuestionQuestion, "??");
        if (ch == '?') return OneCharToken(TokenKind.Question, "?");
        if (ch == '.' && Peek(1) == '.') return TwoCharToken(TokenKind.DotDot, "..");
        if (ch == '.' && (Peek(1) == '\\' || Peek(1) == '/')) return LexGenericText();
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
        if (ch == '\\') return OneCharToken(TokenKind.Backslash, "\\");
        if (ch == '%') return OneCharToken(TokenKind.Percent, "%");
        if (ch == '!') return OneCharToken(TokenKind.Bang, "!");
        if (ch == '|' && Peek(1) == '|') return TwoCharToken(TokenKind.OpOr, "||");
        if (ch == '|') return OneCharToken(TokenKind.Pipe, "|");
        if (ch == '&' && Peek(1) == '&') return TwoCharToken(TokenKind.OpAnd, "&&");
        if (ch == '&') return OneCharToken(TokenKind.Ampersand, "&");
        if (ch == '^') return OneCharTextToken(TokenKind.Identifier, "^", "^");
        if (ch == '~') return OneCharTextToken(TokenKind.Identifier, "~", "~");

        if (IsDashLike(ch))
            return LexDashOperatorOrMinus();

        if (IsIdentifierStart(ch))
            return LexIdentifier();

        if (CanStartGenericText(ch))
            return LexGenericText();

        throw new PwshParseException($"Unexpected character '{ch}'.", CurrentLocation(1));
    }

    // ---------- Number ----------

    private Token LexNumber()
    {
        var start = SnapshotLocation();
        var startPos = _pos;
        bool isFloat = false;
        bool isHex = false;
        bool isVersion = false;

        if (_source[_pos] == '0' && (Peek(1) == 'x' || Peek(1) == 'X'))
        {
            isHex = true;
            Advance(); Advance();
            while (_pos < _source.Length && IsHexDigit(_source[_pos])) Advance();
        }
        else
        {
            if (_source[_pos] == '.')
            {
                isFloat = true;
                Advance();
                while (_pos < _source.Length && IsDigit(_source[_pos])) Advance();
            }
            else
            {
                while (_pos < _source.Length && IsDigit(_source[_pos])) Advance();

                var probe = _pos;
                int segments = 1;
                while (probe < _source.Length && _source[probe] == '.' && probe + 1 < _source.Length && IsDigit(_source[probe + 1]))
                {
                    probe++;
                    while (probe < _source.Length && IsDigit(_source[probe])) probe++;
                    segments++;
                }

                if (segments is >= 3 and <= 4)
                {
                    while (_pos < probe) Advance();
                    isVersion = true;
                }
                else if (segments > 4)
                {
                    while (_pos < probe) Advance();
                    var oidText = _source.Substring(startPos, _pos - startPos);
                    return new Token(TokenKind.Identifier, oidText, oidText, LocationFrom(start));
                }
                else if (_pos < _source.Length && _source[_pos] == '.' && IsDigit(Peek(1)))
                {
                    isFloat = true;
                    Advance();
                    while (_pos < _source.Length && IsDigit(_source[_pos])) Advance();
                }
                else if (_pos < _source.Length && _source[_pos] == '.' && Peek(1) != '.')
                {
                    isFloat = true;
                    Advance();
                }
            }

            if (!isVersion && _pos < _source.Length && (_source[_pos] == 'e' || _source[_pos] == 'E'))
            {
                isFloat = true;
                Advance();
                if (_pos < _source.Length && (_source[_pos] == '+' || _source[_pos] == '-'))
                    Advance();
                while (_pos < _source.Length && IsDigit(_source[_pos])) Advance();
            }
        }

        var numericText = _source.Substring(startPos, _pos - startPos);
        long numericSuffixMultiplier = 1;
        bool hasNumericSuffix = !isVersion && TryConsumeNumericSuffix(out numericSuffixMultiplier);
        string? numericTypeSuffix = !isVersion && !hasNumericSuffix ? TryConsumeNumericTypeSuffix() : null;
        bool hasLongSuffix = !isVersion && !hasNumericSuffix && numericTypeSuffix is null && !isFloat && TryConsumeLongSuffix();
        var text = _source.Substring(startPos, _pos - startPos);
        object value;
        if (isVersion)
        {
            if (!Version.TryParse(numericText, out var version))
                throw new PwshParseException($"Invalid version literal '{numericText}'.", LocationFrom(start));
            value = version;
        }
        else if (isHex)
        {
            var hexBody = numericText.AsSpan(2);
            if (long.TryParse(hexBody, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var lv))
            {
                value = hasLongSuffix || lv < int.MinValue || lv > int.MaxValue ? lv : (object)(int)lv;
            }
            else
            {
                if (BigInteger.TryParse(hexBody, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var big))
                {
                    value = big;
                }
                else
                {
                    throw new PwshParseException($"Invalid hex literal '{numericText}'.", LocationFrom(start));
                }
            }
        }
        else if (isFloat)
        {
            if (string.Equals(numericTypeSuffix, "d", StringComparison.OrdinalIgnoreCase))
            {
                if (!decimal.TryParse(numericText, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var dec))
                    throw new PwshParseException($"Invalid numeric literal '{numericText}'.", LocationFrom(start));
                value = dec;
            }
            else
            {
                if (!double.TryParse(numericText, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d))
                    throw new PwshParseException($"Invalid numeric literal '{numericText}'.", LocationFrom(start));
                value = d;
            }
        }
        else if (string.Equals(numericTypeSuffix, "n", StringComparison.OrdinalIgnoreCase))
        {
            if (!BigInteger.TryParse(numericText, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var big))
                throw new PwshParseException($"Invalid numeric literal '{numericText}'.", LocationFrom(start));
            value = big;
        }
        else if (string.Equals(numericTypeSuffix, "d", StringComparison.OrdinalIgnoreCase))
        {
            if (!decimal.TryParse(numericText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var dec))
                throw new PwshParseException($"Invalid numeric literal '{numericText}'.", LocationFrom(start));
            value = dec;
        }
        else
        {
            if (long.TryParse(numericText, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var lv))
            {
                value = hasLongSuffix || lv < int.MinValue || lv > int.MaxValue ? lv : (object)(int)lv;
            }
            else if (BigInteger.TryParse(numericText, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var big))
            {
                value = big;
            }
            else if (double.TryParse(numericText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
            {
                value = d;
            }
            else
            {
                throw new PwshParseException($"Invalid numeric literal '{numericText}'.", LocationFrom(start));
            }
        }

        if (string.Equals(numericTypeSuffix, "uy", StringComparison.OrdinalIgnoreCase))
        {
            value = value switch
            {
                int i => unchecked((byte)i),
                long l => unchecked((byte)l),
                BigInteger big => (byte)big,
                _ => value,
            };
        }

        if (hasNumericSuffix)
        {
            value = value switch
            {
                int i => checked((long)i * numericSuffixMultiplier),
                long l => checked(l * numericSuffixMultiplier),
                double d => d * numericSuffixMultiplier,
                decimal m => m * numericSuffixMultiplier,
                _ => value,
            };
        }

        return new Token(TokenKind.Number, text, value, LocationFrom(start));
    }

    // ---------- Strings ----------

    private Token LexDoubleQuoted(char opener, char closer)
    {
        var start = SnapshotLocation();
        Advance();
        var parts = new List<StringPart>();
        var buf = new StringBuilder();

        while (_pos < _source.Length)
        {
            var c = _source[_pos];
            if (IsMatchingQuote(c, opener, closer))
            {
                if (c == '"' && Peek(1) == '"')
                {
                    buf.Append('"');
                    Advance();
                    Advance();
                    continue;
                }
                Advance();
                if (buf.Length > 0) { parts.Add(new LiteralPart(buf.ToString())); buf.Clear(); }
                return new Token(TokenKind.String, "\"...\"", parts, LocationFrom(start));
            }
            if (c == '`')
            {
                Advance();
                if (_pos >= _source.Length)
                    throw new PwshParseException("Unterminated backtick escape in string.", LocationFrom(start));
                if (TryReadUnicodeEscapeAfterBacktick(out var unicode))
                {
                    buf.Append(unicode);
                    continue;
                }
                var esc = _source[_pos]; Advance();
                buf.Append(DecodeBacktickEscape(esc));
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
                    if (cc == '`')
                    {
                        sb.Append(cc);
                        Advance();
                        if (_pos >= _source.Length)
                            throw new PwshIncompleteInputException("Unterminated $(...) subexpression in string.", LocationFrom(exprLoc));
                        if (_source[_pos] == 'u' && Peek(1) == '{')
                        {
                            sb.Append(_source[_pos]); Advance();
                            sb.Append(_source[_pos]); Advance();
                            while (_pos < _source.Length && _source[_pos] != '}')
                            {
                                sb.Append(_source[_pos]); Advance();
                            }
                            if (_pos < _source.Length)
                            {
                                sb.Append(_source[_pos]); Advance();
                            }
                            continue;
                        }
                        sb.Append(_source[_pos]); Advance();
                        continue;
                    }
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
                    var nameText = ReadBracedVariableName(start);
                    if (_pos < _source.Length) Advance(); // }
                    parts.Add(ParseVariableFragment(nameText));
                }
                else
                {
                    var nameSb = new StringBuilder();
                    while (_pos < _source.Length && IsIdentifierPart(_source[_pos]))
                    {
                        nameSb.Append(_source[_pos]); Advance();
                    }
                    if (_pos < _source.Length && _source[_pos] == '?')
                    {
                        nameSb.Append('?');
                        Advance();
                    }
                    var first = nameSb.ToString();
                    // Scope prefix? e.g. $env:PATH in string interpolation
                    if (_pos < _source.Length && _source[_pos] == ':' && Peek(1) != ':' &&
                        (IsIdentifierStart(Peek(1)) || IsScopedSpecialVariableChar(Peek(1))))
                    {
                        Advance(); // :
                        var nm = new StringBuilder();
                        if (_pos < _source.Length && IsScopedSpecialVariableChar(_source[_pos]))
                        {
                            nm.Append(_source[_pos]);
                            Advance();
                        }
                        else
                        {
                            while (_pos < _source.Length && IsIdentifierPart(_source[_pos]))
                            {
                                nm.Append(_source[_pos]); Advance();
                            }
                            if (_pos < _source.Length && _source[_pos] == '?')
                            {
                                nm.Append('?');
                                Advance();
                            }
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

    private Token LexSingleQuoted(char opener, char closer)
    {
        var start = SnapshotLocation();
        Advance();
        var sb = new StringBuilder();
        while (_pos < _source.Length)
        {
            var c = _source[_pos];
            if (IsMatchingQuote(c, opener, closer))
            {
                Advance();
                if (c == '\'' && _pos < _source.Length && _source[_pos] == '\'')
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

    private string ReadBracedVariableName((int line, int col, int pos) start)
    {
        var nameSb = new StringBuilder();
        while (_pos < _source.Length && _source[_pos] != '}')
        {
            if (_source[_pos] == '`')
            {
                Advance();
                if (_pos >= _source.Length)
                    throw new PwshParseException("Unterminated backtick escape in ${...} variable reference.", LocationFrom(start));
                if (TryReadUnicodeEscapeAfterBacktick(out var unicode))
                {
                    nameSb.Append(unicode);
                    continue;
                }

                var escaped = _source[_pos];
                Advance();
                nameSb.Append(DecodeBacktickEscape(escaped));
                continue;
            }

            nameSb.Append(_source[_pos]);
            Advance();
        }

        if (_pos >= _source.Length)
            throw new PwshParseException("Unterminated ${...} variable reference.", LocationFrom(start));

        return nameSb.ToString();
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
            var nameText = ReadBracedVariableName(start);
            Advance(); // }
            var v = ParseVariableFragment(nameText);
            return new Token(TokenKind.Variable, "${" + nameText + "}", (v.Scope, v.Name), LocationFrom(start));
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
            return new Token(TokenKind.Identifier, "$", "$", LocationFrom(start));

        if (_pos < _source.Length && _source[_pos] == '?')
        {
            nm.Append('?');
            Advance();
            first = nm.ToString();
        }

        string? scope = null;
        string name = first;
        if (_pos < _source.Length && _source[_pos] == ':' && Peek(1) != ':' &&
            (IsIdentifierStart(Peek(1)) || IsScopedSpecialVariableChar(Peek(1))))
        {
            Advance(); // :
            scope = first;
            var nm2 = new StringBuilder();
            if (_pos < _source.Length && IsScopedSpecialVariableChar(_source[_pos]))
            {
                nm2.Append(_source[_pos]);
                Advance();
            }
            else
            {
                while (_pos < _source.Length && IsIdentifierPart(_source[_pos]))
                {
                    nm2.Append(_source[_pos]); Advance();
                }
                if (_pos < _source.Length && _source[_pos] == '?')
                {
                    nm2.Append('?');
                    Advance();
                }
            }
            name = nm2.ToString();
        }

        return new Token(TokenKind.Variable, "$" + first + (scope != null ? ":" + name : ""), (scope, name), LocationFrom(start));
    }

    private Token LexSplat()
    {
        var start = SnapshotLocation();
        Advance(); // @

        var nm = new StringBuilder();
        while (_pos < _source.Length && IsIdentifierPart(_source[_pos]))
        {
            nm.Append(_source[_pos]); Advance();
        }
        var first = nm.ToString();
        if (first.Length == 0)
            throw new PwshParseException("Expected a variable name after '@'.", LocationFrom(start));

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

        return new Token(TokenKind.SplatVariable, "@" + first + (scope != null ? ":" + name : ""), (scope, name), LocationFrom(start));
    }

    // ---------- Dashed operator vs minus ----------

    private Token LexDashOperatorOrMinus()
    {
        var start = SnapshotLocation();
        // `--` is decrement.
        if (IsDashLike(Peek(1)))
        {
            Advance(); Advance();
            var text = _source.Substring(start.pos, _pos - start.pos);
            return new Token(TokenKind.MinusMinus, text, null, LocationFrom(start));
        }
        // If we see -word where word is a known dashed operator, emit the operator token.
        if (Peek(1) != '\0' && IsIdentifierStart(Peek(1)))
        {
            var probe = _pos + 1;
            var sb = new StringBuilder();
            while (probe < _source.Length && char.IsLetter(_source[probe]))
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
                var text = _source.Substring(start.pos, _pos - start.pos);
                return new Token(kind, text, null, LocationFrom(start));
            }
        }
        // Plain minus.
        Advance();
        var minusText = _source.Substring(start.pos, _pos - start.pos);
        return new Token(TokenKind.Minus, minusText, null, LocationFrom(start));
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

    private Token OneCharTextToken(TokenKind kind, string text, object? value)
    {
        var start = SnapshotLocation();
        Advance();
        return new Token(kind, text, value, LocationFrom(start));
    }

    private Token LexBacktickText()
    {
        var start = SnapshotLocation();
        Advance();
        if (_pos >= _source.Length)
            throw new PwshParseException("Unterminated backtick escape.", LocationFrom(start));

        if (IsDigit(_source[_pos]))
        {
            return new Token(TokenKind.Identifier, "`", "`", LocationFrom(start));
        }

        string decoded;
        if (TryReadUnicodeEscapeAfterBacktick(out var unicode))
        {
            decoded = unicode;
        }
        else
        {
            var esc = _source[_pos];
            Advance();
            decoded = DecodeBacktickEscape(esc).ToString();
        }

        var text = _source.Substring(start.pos, _pos - start.pos);
        return new Token(TokenKind.Identifier, text, decoded, LocationFrom(start));
    }

    private Token LexGenericText()
    {
        var start = SnapshotLocation();
        var decoded = new StringBuilder();
        while (_pos < _source.Length && !IsGenericTextTerminator(_source[_pos]))
        {
            if (_source[_pos] == '`')
            {
                Advance();
                if (_pos >= _source.Length)
                    throw new PwshParseException("Unterminated backtick escape.", LocationFrom(start));

                if (TryReadUnicodeEscapeAfterBacktick(out var unicode))
                {
                    decoded.Append(unicode);
                    continue;
                }

                var esc = _source[_pos];
                Advance();
                decoded.Append(DecodeBacktickEscape(esc));
                continue;
            }

            decoded.Append(_source[_pos]);
            Advance();
        }

        var text = _source.Substring(start.pos, _pos - start.pos);
        return new Token(TokenKind.Identifier, text, decoded.ToString(), LocationFrom(start));
    }

    private static char DecodeBacktickEscape(char esc) => esc switch
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
    };

    private bool IsRedirectionStart()
    {
        if (_pos >= _source.Length) return false;
        var ch = _source[_pos];
        if (ch == '>') return true;
        if (ch == '*' && Peek(1) == '>') return true;
        if (!IsDigit(ch)) return false;
        if (_pos >= 2 && _source[_pos - 1] == '.' && _source[_pos - 2] == '.')
            return false;
        if (!HasDigitLedRedirectionContext())
            return false;

        var probe = _pos;
        while (probe < _source.Length && IsDigit(_source[probe])) probe++;
        return probe < _source.Length && _source[probe] == '>';
    }

    private bool HasDigitLedRedirectionContext()
    {
        var probe = _pos - 1;
        while (probe >= 0 &&
               char.IsWhiteSpace(_source[probe]) &&
               _source[probe] is not '\r' and not '\n')
        {
            probe--;
        }

        if (probe < 0)
            return false;

        var prev = _source[probe];
        if (prev is '\r' or '\n')
            return false;

        return prev is not (';' or '{' or '(' or '[' or ',' or '=' or ':' or '?' or '+' or '-' or '*' or '/' or '%' or '!' or '&' or '|' or '<' or '>');
    }

    private Token LexRedirection()
    {
        var start = SnapshotLocation();
        int? fromStream = null;

        if (_source[_pos] == '*')
        {
            fromStream = -1;
            Advance();
        }
        else if (IsDigit(_source[_pos]))
        {
            var digits = new StringBuilder();
            while (_pos < _source.Length && IsDigit(_source[_pos]))
            {
                digits.Append(_source[_pos]);
                Advance();
            }
            fromStream = int.Parse(digits.ToString(), System.Globalization.CultureInfo.InvariantCulture);
        }

        if (_pos >= _source.Length || _source[_pos] != '>')
            throw new PwshParseException("Expected '>' in redirection operator.", LocationFrom(start));
        Advance();

        bool append = false;
        if (_pos < _source.Length && _source[_pos] == '>')
        {
            append = true;
            Advance();
        }

        int? mergeToStream = null;
        if (!append && _pos < _source.Length && _source[_pos] == '&')
        {
            Advance();
            if (_pos >= _source.Length)
                throw new PwshIncompleteInputException("Unterminated redirection operator.", LocationFrom(start));
            if (_source[_pos] == '*')
            {
                mergeToStream = -1;
                Advance();
            }
            else if (IsDigit(_source[_pos]))
            {
                var digits = new StringBuilder();
                while (_pos < _source.Length && IsDigit(_source[_pos]))
                {
                    digits.Append(_source[_pos]);
                    Advance();
                }
                mergeToStream = int.Parse(digits.ToString(), System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                throw new PwshParseException("Expected a stream designator after '>&'.", LocationFrom(start));
            }
        }

        var text = _source.Substring(start.pos, _pos - start.pos);
        return new Token(
            TokenKind.Redirection,
            text,
            new RedirectionTokenData(fromStream, append, mergeToStream),
            LocationFrom(start));
    }

    private void SkipHorizontalWhitespaceAndComments()
    {
        while (_pos < _source.Length)
        {
            var c = _source[_pos];
            if (char.IsWhiteSpace(c) && c is not '\r' and not '\n') { Advance(); continue; }
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

    private static bool IsMatchingQuote(char candidate, char opener, char closer)
        => candidate == closer || candidate == opener;

    private static bool IsDoubleQuoteChar(char c) => c is '"' or '\u201C' or '\u201D';
    private static bool IsSingleQuoteChar(char c) => c is '\'' or '\u2018' or '\u2019';

    private static char MatchingDoubleQuote(char c) => c switch
    {
        '\u201C' => '\u201D',
        '\u201D' => '\u201D',
        _ => '"',
    };

    private static char MatchingSingleQuote(char c) => c switch
    {
        '\u2018' => '\u2019',
        '\u2019' => '\u2019',
        _ => '\'',
    };

    private static bool IsDigit(char c) => c >= '0' && c <= '9';
    private static bool IsHexDigit(char c) => IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    private static bool IsIdentifierStart(char c) => c == '_' || char.IsLetter(c);
    private static bool IsIdentifierPart(char c) => c == '_' || char.IsLetterOrDigit(c);
    private static bool IsDashLike(char c) => c is '-' or '\u2013' or '\u2014' or '\u2212';
    private static bool IsScopedSpecialVariableChar(char c) => c is '?' or '^' or '$';

    private bool TryReadUnicodeEscapeAfterBacktick(out string unicode)
    {
        unicode = "";
        if (_pos >= _source.Length || _source[_pos] != 'u' || Peek(1) != '{')
            return false;

        var start = SnapshotLocation();
        Advance(); // u
        Advance(); // {

        var hex = new StringBuilder();
        while (_pos < _source.Length && _source[_pos] != '}')
        {
            if (!IsHexDigit(_source[_pos]))
                throw new PwshParseException("Invalid unicode escape after backtick.", LocationFrom(start));

            hex.Append(_source[_pos]);
            Advance();
        }

        if (_pos >= _source.Length || _source[_pos] != '}' || hex.Length == 0)
            throw new PwshParseException("Unterminated unicode escape after backtick.", LocationFrom(start));

        Advance(); // }

        var codePoint = Convert.ToInt32(hex.ToString(), 16);
        unicode = codePoint <= 0xFFFF
            ? new string((char)codePoint, 1)
            : char.ConvertFromUtf32(codePoint);
        return true;
    }

    private static bool CanStartGenericText(char c)
        => !char.IsWhiteSpace(c) &&
           c is not '\r' and not '\n' and
           not '(' and not ')' and
           not '{' and not '}' and
           not ';' and not ',' and not '|';

    private static bool IsGenericTextTerminator(char c)
        => char.IsWhiteSpace(c) ||
           c is '\r' or '\n' or ';' or ',' or '|' or '(' or ')' or '{' or '}';

    private bool TryConsumeNumericSuffix(out long multiplier)
    {
        foreach (var (suffix, value) in NumericSuffixMultipliers)
        {
            if (_pos + suffix.Length > _source.Length)
                continue;
            if (!_source.AsSpan(_pos, suffix.Length).Equals(suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            var end = _pos + suffix.Length;
            if (end < _source.Length && IsIdentifierPart(_source[end]))
                continue;

            _pos = end;
            _col += suffix.Length;
            multiplier = value;
            return true;
        }

        multiplier = 1;
        return false;
    }

    private string? TryConsumeNumericTypeSuffix()
    {
        foreach (var suffix in new[] { "uy", "n", "d" })
        {
            if (_pos + suffix.Length > _source.Length)
                continue;
            if (!_source.AsSpan(_pos, suffix.Length).Equals(suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            var end = _pos + suffix.Length;
            if (end < _source.Length && IsIdentifierPart(_source[end]))
                continue;

            _pos = end;
            _col += suffix.Length;
            return suffix;
        }

        return null;
    }

    private bool TryConsumeLongSuffix()
    {
        if (_pos >= _source.Length)
            return false;
        if (_source[_pos] is not ('l' or 'L'))
            return false;
        var end = _pos + 1;
        if (end < _source.Length && IsIdentifierPart(_source[end]))
            return false;
        Advance();
        return true;
    }
}
