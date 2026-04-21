using CarbidePwsh.Errors;
using CarbidePwsh.Lexer;
using CarbidePwsh.Parser.Ast;

namespace CarbidePwsh.Parser;

/// <summary>
/// Recursive-descent parser for the Phase 1 language. Consumes the token list produced by
/// <see cref="Lexer"/> and emits a <see cref="ScriptAst"/>. Newlines at the top level act as
/// statement separators; inside any grouping construct (parens, brackets, braces, subexpressions)
/// they are treated as whitespace.
/// </summary>
public sealed class Parser
{
    private readonly List<Token> _tokens;
    private int _cursor;

    public Parser(List<Token> tokens)
    {
        _tokens = tokens;
        _cursor = 0;
    }

    public static ScriptAst ParseString(string source)
    {
        var tokens = new Lexer.Lexer(source).Tokenize();
        return new Parser(tokens).ParseScript();
    }

    private Token Current => _tokens[_cursor];
    private Token Peek(int delta) => _tokens[Math.Min(_cursor + delta, _tokens.Count - 1)];

    private bool IsAt(TokenKind kind) => Current.Kind == kind;

    private Token Consume(TokenKind kind)
    {
        if (Current.Kind != kind)
            throw new PwshParseException($"Expected {kind}, got {Current.Kind} '{Current.Text}'.", Current.Location);
        var t = Current; _cursor++;
        return t;
    }

    private Token Advance()
    {
        var t = Current;
        if (t.Kind != TokenKind.EndOfInput) _cursor++;
        return t;
    }

    private void SkipNewlinesAndSemicolons()
    {
        while (IsAt(TokenKind.NewLine) || IsAt(TokenKind.Semicolon)) _cursor++;
    }

    private void SkipNewlines()
    {
        while (IsAt(TokenKind.NewLine)) _cursor++;
    }

    // ---------- Script / statements ----------

    public ScriptAst ParseScript()
    {
        var start = Current.Location;
        var statements = new List<StatementAst>();
        SkipNewlinesAndSemicolons();
        while (!IsAt(TokenKind.EndOfInput))
        {
            var s = ParseStatement();
            statements.Add(s);
            if (IsAt(TokenKind.EndOfInput)) break;
            if (!IsAt(TokenKind.NewLine) && !IsAt(TokenKind.Semicolon))
                throw new PwshParseException(
                    $"Expected statement separator, got {Current.Kind} '{Current.Text}'.", Current.Location);
            SkipNewlinesAndSemicolons();
        }
        return new ScriptAst(statements, start);
    }

    private StatementAst ParseStatement()
    {
        var loc = Current.Location;

        // Statement-start Identifier → command mode. Phase 1 had no Identifier-as-expression,
        // so this simple rule covers `Get-ChildItem`, `cd /tmp`, etc. without false positives.
        if (IsAt(TokenKind.Identifier))
        {
            return ParseCommandPipeline(loc);
        }

        var expr = ParseLogicalOr();
        if (IsAssignmentOp(Current.Kind))
        {
            var op = Current.Kind switch
            {
                TokenKind.Equal => AssignmentOp.Assign,
                TokenKind.PlusEqual => AssignmentOp.AddAssign,
                TokenKind.MinusEqual => AssignmentOp.SubtractAssign,
                TokenKind.StarEqual => AssignmentOp.MultiplyAssign,
                TokenKind.SlashEqual => AssignmentOp.DivideAssign,
                TokenKind.PercentEqual => AssignmentOp.ModuloAssign,
                _ => throw new InvalidOperationException(),
            };
            Advance();
            SkipNewlines();
            // Assignment RHS may itself be a pipeline: `$x = Get-ChildItem | Sort-Object`.
            var rhs = ParseRhsPipeline();
            return new AssignmentStatementAst(expr, op, rhs, loc);
        }

        // Pipeline starting with expression: `@(1,2,3) | Where-Object {...}`.
        if (IsAt(TokenKind.Pipe))
        {
            var stages = new List<AstNode> { expr };
            while (IsAt(TokenKind.Pipe))
            {
                Advance();
                SkipNewlines();
                stages.Add(ParsePipelineStage());
            }
            return new PipelineAst(stages, loc);
        }

        return new ExpressionStatementAst(expr, loc);
    }

    /// <summary>Parse the RHS of an assignment, which may contain a pipeline. Returns an
    /// <see cref="ExpressionAst"/>; pipeline RHS is wrapped in a <see cref="SubExpressionAst"/>
    /// that executes the pipeline and yields its final result.</summary>
    private ExpressionAst ParseRhsPipeline()
    {
        var loc = Current.Location;
        // If the RHS starts with an identifier, it's a command-mode pipeline.
        if (IsAt(TokenKind.Identifier))
        {
            var cmdStmt = ParseCommandPipeline(loc);
            return new SubExpressionAst(new ScriptAst(new[] { cmdStmt }, loc), loc);
        }
        var expr = ParseLogicalOr();
        if (!IsAt(TokenKind.Pipe)) return expr;
        var stages = new List<AstNode> { expr };
        while (IsAt(TokenKind.Pipe))
        {
            Advance();
            SkipNewlines();
            stages.Add(ParsePipelineStage());
        }
        var pipeline = new PipelineAst(stages, loc);
        return new SubExpressionAst(new ScriptAst(new StatementAst[] { pipeline }, loc), loc);
    }

    private StatementAst ParseCommandPipeline(SourceLocation loc)
    {
        var first = ParseCommand();
        if (!IsAt(TokenKind.Pipe))
        {
            // Single command — still return a PipelineAst for uniformity.
            return new PipelineAst(new AstNode[] { first }, loc);
        }
        var stages = new List<AstNode> { first };
        while (IsAt(TokenKind.Pipe))
        {
            Advance();
            SkipNewlines();
            stages.Add(ParsePipelineStage());
        }
        return new PipelineAst(stages, loc);
    }

    private AstNode ParsePipelineStage()
    {
        // Command or expression. Same rule as statement-start.
        if (IsAt(TokenKind.Identifier))
            return ParseCommand();
        return ParseLogicalOr();
    }

    private static bool IsAssignmentOp(TokenKind k) => k is
        TokenKind.Equal or TokenKind.PlusEqual or TokenKind.MinusEqual or
        TokenKind.StarEqual or TokenKind.SlashEqual or TokenKind.PercentEqual;

    // ---------- Expression precedence ladder ----------

    private ExpressionAst ParseLogicalOr()
    {
        var left = ParseLogicalAnd();
        while (IsAt(TokenKind.OpOr) || IsAt(TokenKind.OpXor))
        {
            var op = Current.Kind == TokenKind.OpOr ? BinaryOp.Or : BinaryOp.Xor;
            var loc = Current.Location;
            Advance();
            SkipNewlines();
            var right = ParseLogicalAnd();
            left = new BinaryExpressionAst(left, op, right, loc);
        }
        return left;
    }

    private ExpressionAst ParseLogicalAnd()
    {
        var left = ParseBitwise();
        while (IsAt(TokenKind.OpAnd))
        {
            var loc = Current.Location;
            Advance();
            SkipNewlines();
            var right = ParseBitwise();
            left = new BinaryExpressionAst(left, BinaryOp.And, right, loc);
        }
        return left;
    }

    private ExpressionAst ParseBitwise()
    {
        var left = ParseComparison();
        while (IsAt(TokenKind.OpBand) || IsAt(TokenKind.OpBor) || IsAt(TokenKind.OpBxor))
        {
            var op = Current.Kind switch
            {
                TokenKind.OpBand => BinaryOp.BAnd,
                TokenKind.OpBor => BinaryOp.BOr,
                _ => BinaryOp.BXor,
            };
            var loc = Current.Location;
            Advance();
            SkipNewlines();
            var right = ParseComparison();
            left = new BinaryExpressionAst(left, op, right, loc);
        }
        return left;
    }

    private ExpressionAst ParseComparison()
    {
        var left = ParseAdditive();
        if (TryConsumeComparisonOp(out var op, out var loc))
        {
            SkipNewlines();
            var right = ParseAdditive();
            return new BinaryExpressionAst(left, op, right, loc);
        }
        return left;
    }

    private bool TryConsumeComparisonOp(out BinaryOp op, out SourceLocation loc)
    {
        op = default; loc = default;
        switch (Current.Kind)
        {
            case TokenKind.OpEq: op = BinaryOp.Equal; break;
            case TokenKind.OpNe: op = BinaryOp.NotEqual; break;
            case TokenKind.OpLt: op = BinaryOp.LessThan; break;
            case TokenKind.OpLe: op = BinaryOp.LessOrEqual; break;
            case TokenKind.OpGt: op = BinaryOp.GreaterThan; break;
            case TokenKind.OpGe: op = BinaryOp.GreaterOrEqual; break;
            case TokenKind.OpIeq: op = BinaryOp.IEqual; break;
            case TokenKind.OpIne: op = BinaryOp.INotEqual; break;
            case TokenKind.OpIlt: op = BinaryOp.ILessThan; break;
            case TokenKind.OpIle: op = BinaryOp.ILessOrEqual; break;
            case TokenKind.OpIgt: op = BinaryOp.IGreaterThan; break;
            case TokenKind.OpIge: op = BinaryOp.IGreaterOrEqual; break;
            case TokenKind.OpCeq: op = BinaryOp.CEqual; break;
            case TokenKind.OpCne: op = BinaryOp.CNotEqual; break;
            case TokenKind.OpClt: op = BinaryOp.CLessThan; break;
            case TokenKind.OpCle: op = BinaryOp.CLessOrEqual; break;
            case TokenKind.OpCgt: op = BinaryOp.CGreaterThan; break;
            case TokenKind.OpCge: op = BinaryOp.CGreaterOrEqual; break;
            case TokenKind.OpIs: op = BinaryOp.Is; break;
            case TokenKind.OpIsNot: op = BinaryOp.IsNot; break;
            case TokenKind.OpAs: op = BinaryOp.As; break;
            default: return false;
        }
        loc = Current.Location;
        Advance();
        return true;
    }

    private ExpressionAst ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (IsAt(TokenKind.Plus) || IsAt(TokenKind.Minus))
        {
            var op = Current.Kind == TokenKind.Plus ? BinaryOp.Add : BinaryOp.Subtract;
            var loc = Current.Location;
            Advance();
            SkipNewlines();
            var right = ParseMultiplicative();
            left = new BinaryExpressionAst(left, op, right, loc);
        }
        return left;
    }

    private ExpressionAst ParseMultiplicative()
    {
        var left = ParseRange();
        while (IsAt(TokenKind.Star) || IsAt(TokenKind.Slash) || IsAt(TokenKind.Percent))
        {
            var op = Current.Kind switch
            {
                TokenKind.Star => BinaryOp.Multiply,
                TokenKind.Slash => BinaryOp.Divide,
                _ => BinaryOp.Modulo,
            };
            var loc = Current.Location;
            Advance();
            SkipNewlines();
            var right = ParseRange();
            left = new BinaryExpressionAst(left, op, right, loc);
        }
        return left;
    }

    private ExpressionAst ParseRange()
    {
        var left = ParseUnary();
        if (IsAt(TokenKind.DotDot))
        {
            var loc = Current.Location;
            Advance();
            SkipNewlines();
            var right = ParseUnary();
            return new RangeExpressionAst(left, right, loc);
        }
        return left;
    }

    private ExpressionAst ParseUnary()
    {
        if (IsAt(TokenKind.Minus))
        {
            var loc = Current.Location;
            Advance();
            var inner = ParseUnary();
            return new UnaryExpressionAst(UnaryOp.Negate, inner, loc);
        }
        if (IsAt(TokenKind.Plus))
        {
            var loc = Current.Location;
            Advance();
            var inner = ParseUnary();
            return new UnaryExpressionAst(UnaryOp.Plus, inner, loc);
        }
        if (IsAt(TokenKind.Bang) || IsAt(TokenKind.OpNot))
        {
            var loc = Current.Location;
            Advance();
            var inner = ParseUnary();
            return new UnaryExpressionAst(UnaryOp.Not, inner, loc);
        }
        if (IsAt(TokenKind.OpBnot))
        {
            var loc = Current.Location;
            Advance();
            var inner = ParseUnary();
            return new UnaryExpressionAst(UnaryOp.BNot, inner, loc);
        }
        // [Type]expr is a cast when followed by an atom-start token.
        if (IsAt(TokenKind.LBracket) && LooksLikeTypeLiteral())
        {
            var save = _cursor;
            var typeLit = ParseTypeLiteral();
            if (IsCastFollower(Current.Kind))
            {
                var operand = ParseUnary();
                return new CastExpressionAst(typeLit, operand, typeLit.Location);
            }
            // Not a cast: continue parsing type literal (or member access on it) via postfix.
            return ParsePostfixContinuation(typeLit);
        }
        return ParsePostfix();
    }

    private static bool IsCastFollower(TokenKind k) => k is
        TokenKind.Variable or TokenKind.Number or TokenKind.String or
        TokenKind.LParen or TokenKind.DollarLParen or TokenKind.AtLParen or TokenKind.AtLBrace or
        TokenKind.Minus or TokenKind.Plus or TokenKind.Bang or TokenKind.OpNot or
        TokenKind.Identifier or TokenKind.LBracket;

    private bool LooksLikeTypeLiteral()
    {
        // Simple lookahead: `[` Identifier with optional . sequence followed by `]` indicates a
        // type literal. We scan without advancing the cursor.
        if (Current.Kind != TokenKind.LBracket) return false;
        var i = _cursor + 1;
        int depth = 1;
        while (i < _tokens.Count && depth > 0)
        {
            var t = _tokens[i];
            if (t.Kind == TokenKind.LBracket) depth++;
            else if (t.Kind == TokenKind.RBracket) depth--;
            else if (t.Kind == TokenKind.NewLine) { /* skip */ }
            else if (t.Kind != TokenKind.Identifier && t.Kind != TokenKind.Dot && t.Kind != TokenKind.Comma)
            {
                return false;
            }
            i++;
        }
        return depth == 0;
    }

    private TypeLiteralAst ParseTypeLiteral()
    {
        var start = Current.Location;
        Consume(TokenKind.LBracket);
        var typeName = ParseTypeName();
        var genericArgs = new List<TypeLiteralAst>();
        int arrayRank = 0;
        // Phase 1: accept [Name] and [Name.Sub], no generic args inside the type literal itself.
        // (Generic args would need nested [T] at this position.)
        Consume(TokenKind.RBracket);
        return new TypeLiteralAst(typeName, genericArgs, arrayRank, start);
    }

    private string ParseTypeName()
    {
        var first = Consume(TokenKind.Identifier).Text;
        var sb = new System.Text.StringBuilder(first);
        while (IsAt(TokenKind.Dot))
        {
            Advance();
            var next = Consume(TokenKind.Identifier).Text;
            sb.Append('.').Append(next);
        }
        return sb.ToString();
    }

    // ---------- Postfix (member access, invocation, indexing) ----------

    private ExpressionAst ParsePostfix()
    {
        var expr = ParsePrimary();
        return ParsePostfixContinuation(expr);
    }

    private ExpressionAst ParsePostfixContinuation(ExpressionAst expr)
    {
        while (true)
        {
            if (IsAt(TokenKind.Dot))
            {
                var loc = Current.Location;
                Advance();
                var name = Consume(TokenKind.Identifier).Text;
                List<ExpressionAst>? args = null;
                bool invocation = false;
                if (IsAt(TokenKind.LParen))
                {
                    args = ParseInvocationArgs();
                    invocation = true;
                }
                expr = new MemberAccessAst(expr, name, IsStatic: false, IsInvocation: invocation, Arguments: args, Location: loc);
                continue;
            }
            if (IsAt(TokenKind.ColonColon))
            {
                var loc = Current.Location;
                Advance();
                var name = Consume(TokenKind.Identifier).Text;
                List<ExpressionAst>? args = null;
                bool invocation = false;
                if (IsAt(TokenKind.LParen))
                {
                    args = ParseInvocationArgs();
                    invocation = true;
                }
                expr = new MemberAccessAst(expr, name, IsStatic: true, IsInvocation: invocation, Arguments: args, Location: loc);
                continue;
            }
            if (IsAt(TokenKind.LBracket))
            {
                var loc = Current.Location;
                Advance();
                SkipNewlines();
                var index = ParseLogicalOr();
                SkipNewlines();
                Consume(TokenKind.RBracket);
                expr = new IndexerAst(expr, index, loc);
                continue;
            }
            return expr;
        }
    }

    private List<ExpressionAst> ParseInvocationArgs()
    {
        Consume(TokenKind.LParen);
        SkipNewlines();
        var args = new List<ExpressionAst>();
        if (!IsAt(TokenKind.RParen))
        {
            args.Add(ParseLogicalOr());
            SkipNewlines();
            while (IsAt(TokenKind.Comma))
            {
                Advance();
                SkipNewlines();
                args.Add(ParseLogicalOr());
                SkipNewlines();
            }
        }
        Consume(TokenKind.RParen);
        return args;
    }

    // ---------- Primary ----------

    private ExpressionAst ParsePrimary()
    {
        var t = Current;
        switch (t.Kind)
        {
            case TokenKind.Number:
                Advance();
                return new NumberLiteralAst(t.Value!, t.Location);
            case TokenKind.String:
                Advance();
                var parts = (IReadOnlyList<StringPart>)t.Value!;
                var isSingle = t.Text.StartsWith('\'');
                return new StringLiteralAst(parts, isSingle, t.Location);
            case TokenKind.Variable:
                Advance();
                var (scope, name) = ((string? Scope, string Name))t.Value!;
                if (scope == null && name.Equals("true", StringComparison.OrdinalIgnoreCase))
                    return new BooleanLiteralAst(true, t.Location);
                if (scope == null && name.Equals("false", StringComparison.OrdinalIgnoreCase))
                    return new BooleanLiteralAst(false, t.Location);
                if (scope == null && name.Equals("null", StringComparison.OrdinalIgnoreCase))
                    return new NullLiteralAst(t.Location);
                return new VariableAst(scope, name, t.Location);
            case TokenKind.LParen:
                Advance();
                SkipNewlines();
                var inner = ParseLogicalOr();
                SkipNewlines();
                if (!IsAt(TokenKind.RParen))
                {
                    if (IsAt(TokenKind.EndOfInput))
                        throw new PwshIncompleteInputException("Unterminated '(' — expected ')'.", t.Location);
                    throw new PwshParseException($"Expected ')', got {Current.Kind} '{Current.Text}'.", Current.Location);
                }
                Advance();
                return new ParenExpressionAst(inner, t.Location);
            case TokenKind.DollarLParen:
                Advance();
                SkipNewlinesAndSemicolons();
                var body = new List<StatementAst>();
                while (!IsAt(TokenKind.RParen) && !IsAt(TokenKind.EndOfInput))
                {
                    body.Add(ParseStatement());
                    SkipNewlinesAndSemicolons();
                }
                if (IsAt(TokenKind.EndOfInput))
                    throw new PwshIncompleteInputException("Unterminated $(...) subexpression.", t.Location);
                Consume(TokenKind.RParen);
                return new SubExpressionAst(new ScriptAst(body, t.Location), t.Location);
            case TokenKind.AtLParen:
                return ParseArrayLiteral();
            case TokenKind.AtLBrace:
                return ParseHashtableLiteral();
            case TokenKind.LBrace:
                return ParseScriptBlock();
            case TokenKind.LBracket:
                // A type literal that did not attach to a cast: parse and return.
                var lit = ParseTypeLiteral();
                return lit;
        }
        if (t.Kind == TokenKind.EndOfInput)
            throw new PwshIncompleteInputException("Unexpected end of input.", t.Location);
        throw new PwshParseException($"Unexpected token {t.Kind} '{t.Text}'.", t.Location);
    }

    private ScriptBlockAst ParseScriptBlock()
    {
        var start = Consume(TokenKind.LBrace).Location;
        SkipNewlinesAndSemicolons();
        var statements = new List<StatementAst>();
        while (!IsAt(TokenKind.RBrace) && !IsAt(TokenKind.EndOfInput))
        {
            statements.Add(ParseStatement());
            if (IsAt(TokenKind.RBrace) || IsAt(TokenKind.EndOfInput)) break;
            if (!IsAt(TokenKind.NewLine) && !IsAt(TokenKind.Semicolon))
                throw new PwshParseException(
                    $"Expected statement separator inside script block, got {Current.Kind} '{Current.Text}'.", Current.Location);
            SkipNewlinesAndSemicolons();
        }
        if (IsAt(TokenKind.EndOfInput))
            throw new PwshIncompleteInputException("Unterminated script block.", start);
        Consume(TokenKind.RBrace);
        return new ScriptBlockAst(new ScriptAst(statements, start), start);
    }

    private ArrayExpressionAst ParseArrayLiteral()
    {
        var start = Consume(TokenKind.AtLParen).Location;
        SkipNewlinesAndSemicolons();
        var elements = new List<ExpressionAst>();
        while (!IsAt(TokenKind.RParen) && !IsAt(TokenKind.EndOfInput))
        {
            elements.Add(ParseLogicalOr());
            SkipNewlinesAndSemicolons();
            if (IsAt(TokenKind.Comma))
            {
                Advance();
                SkipNewlinesAndSemicolons();
            }
        }
        if (IsAt(TokenKind.EndOfInput))
            throw new PwshIncompleteInputException("Unterminated @(...) array literal.", start);
        Consume(TokenKind.RParen);
        return new ArrayExpressionAst(elements, start);
    }

    private HashtableExpressionAst ParseHashtableLiteral()
    {
        var start = Consume(TokenKind.AtLBrace).Location;
        SkipNewlinesAndSemicolons();
        var entries = new List<(ExpressionAst, ExpressionAst)>();
        while (!IsAt(TokenKind.RBrace) && !IsAt(TokenKind.EndOfInput))
        {
            var key = ParseHashtableKey();
            SkipNewlines();
            Consume(TokenKind.Equal);
            SkipNewlines();
            var value = ParseLogicalOr();
            entries.Add((key, value));
            SkipNewlinesAndSemicolons();
            // Allow optional comma as well.
            if (IsAt(TokenKind.Comma))
            {
                Advance();
                SkipNewlinesAndSemicolons();
            }
        }
        if (IsAt(TokenKind.EndOfInput))
            throw new PwshIncompleteInputException("Unterminated @{...} hashtable literal.", start);
        Consume(TokenKind.RBrace);
        return new HashtableExpressionAst(entries, start);
    }

    private ExpressionAst ParseHashtableKey()
    {
        // Accept bare identifiers as string keys (PowerShell convention).
        if (IsAt(TokenKind.Identifier))
        {
            var t = Advance();
            return new StringLiteralAst(new List<StringPart> { new LiteralPart(t.Text) }, IsSingleQuoted: true, t.Location);
        }
        return ParseLogicalOr();
    }

    // ---------- Command mode ----------

    private CommandAst ParseCommand()
    {
        var nameToken = Consume(TokenKind.Identifier);
        var elements = new List<CommandElementAst>();

        while (IsCommandElementStart())
        {
            // `-Name` parameter introducer: Minus immediately followed by Identifier (no
            // whitespace) and not a known dashed operator.
            if (IsAt(TokenKind.Minus)
                && Peek(1).Kind == TokenKind.Identifier
                && Current.Location.Offset + 1 == Peek(1).Location.Offset)
            {
                var minusLoc = Current.Location;
                Advance();
                var idToken = Advance();
                elements.Add(new CommandParameterAst(idToken.Text, minusLoc));
                continue;
            }

            // Argument expression.
            var argLoc = Current.Location;
            var expr = ParseCommandArgument();
            elements.Add(new CommandArgumentAst(expr, argLoc));
        }

        return new CommandAst(nameToken.Text, elements, nameToken.Location);
    }

    private bool IsCommandElementStart()
    {
        if (IsAt(TokenKind.EndOfInput) || IsAt(TokenKind.NewLine) ||
            IsAt(TokenKind.Semicolon) || IsAt(TokenKind.Pipe) ||
            IsAt(TokenKind.RParen) || IsAt(TokenKind.RBrace) || IsAt(TokenKind.RBracket))
            return false;
        return true;
    }

    private ExpressionAst ParseCommandArgument()
    {
        var t = Current;
        switch (t.Kind)
        {
            case TokenKind.Variable:
            case TokenKind.Number:
            case TokenKind.String:
            case TokenKind.LParen:
            case TokenKind.AtLParen:
            case TokenKind.AtLBrace:
            case TokenKind.DollarLParen:
            case TokenKind.LBrace:
                return ParsePostfix();

            case TokenKind.LBracket when LooksLikeTypeLiteral():
                return ParsePostfix();

            default:
                return ParseBareWord();
        }
    }

    private ExpressionAst ParseBareWord()
    {
        var start = Current.Location;
        var startOffset = start.Offset;
        int endOffset = startOffset;
        var sb = new System.Text.StringBuilder();

        while (!IsAt(TokenKind.EndOfInput) &&
               !IsAt(TokenKind.NewLine) &&
               !IsAt(TokenKind.Semicolon) &&
               !IsAt(TokenKind.Pipe) &&
               !IsAt(TokenKind.RParen) &&
               !IsAt(TokenKind.RBrace) &&
               !IsAt(TokenKind.RBracket))
        {
            // First token: always consume. Subsequent: only if adjacent (no whitespace).
            if (sb.Length > 0 && Current.Location.Offset != endOffset)
                break;
            sb.Append(Current.Text);
            endOffset = Current.Location.Offset + Current.Location.Length;
            Advance();
        }

        if (sb.Length == 0)
            throw new PwshParseException($"Unexpected token {Current.Kind} '{Current.Text}' in command argument.", Current.Location);

        // Produce a single-quoted-shaped string literal so the interpreter treats the text as
        // a literal (no interpolation).
        var loc = new SourceLocation(start.Line, start.Column, start.Offset, endOffset - start.Offset);
        return new StringLiteralAst(
            new List<StringPart> { new LiteralPart(sb.ToString()) },
            IsSingleQuoted: true,
            loc);
    }
}
