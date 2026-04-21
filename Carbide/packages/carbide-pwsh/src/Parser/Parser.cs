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

        // Statement-start Identifier → either a keyword statement (if/while/try/…) or command
        // mode. Phase 1 had no Identifier-as-expression, so this simple rule covers
        // `Get-ChildItem`, `cd /tmp`, etc. without false positives.
        if (IsAt(TokenKind.Identifier))
        {
            var kw = Current.Text.ToLowerInvariant();
            switch (kw)
            {
                case "if": return ParseIfStatement();
                case "while": return ParseWhileStatement();
                case "do": return ParseDoStatement();
                case "for": return ParseForStatement();
                case "foreach": return ParseForEachStatement();
                case "switch": return ParseSwitchStatement();
                case "function": case "filter": return ParseFunctionDefinition();
                case "try": return ParseTryStatement();
                case "throw": return ParseThrowStatement();
                case "return": return ParseReturnStatement();
                case "break": return ParseBreakStatement();
                case "continue": return ParseContinueStatement();
                case "class": return ParseClassDefinition();
                case "enum": return ParseEnumDefinition();
            }
            return ParseCommandPipeline(loc);
        }

        // Dot-sourcing: `. ./script.ps1` at statement start.
        if (IsAt(TokenKind.Dot) && IsDotSourceCandidate())
        {
            return ParseDotSourceStatement(loc);
        }

        // Call operator: `& $block arg1 arg2` or `& 'path.ps1' arg1`.
        if (IsAt(TokenKind.Ampersand))
        {
            return ParseCallOperatorStatement(loc);
        }

        // Path-like command at statement start: `./foo`, `/usr/bin/foo`, or just `foo.ps1`.
        // Detect by first token being Dot or Slash (adjacent to the next token), which can't
        // start an expression.
        if ((IsAt(TokenKind.Dot) || IsAt(TokenKind.Slash)) && IsPathLikeCommand())
        {
            return ParsePathCommandStatement(loc);
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
        if (IsAt(TokenKind.Identifier))
        {
            var kw = Current.Text.ToLowerInvariant();
            // Statement keywords produce collected values as the RHS.
            if (kw is "if" or "while" or "do" or "for" or "foreach" or "switch" or "try")
            {
                var stmt = ParseStatement();
                return new SubExpressionAst(new ScriptAst(new[] { stmt }, loc), loc);
            }
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
            // Some operators take their right-hand side as a comma-list: `-replace 'p', 'r'`,
            // `'{0}-{1}' -f $a, $b`. Collect trailing commas into an array argument.
            if (OpTakesCommaList(op) && IsAt(TokenKind.Comma))
            {
                var items = new List<ExpressionAst> { right };
                while (IsAt(TokenKind.Comma))
                {
                    Advance();
                    SkipNewlines();
                    items.Add(ParseAdditive());
                }
                right = new ArrayExpressionAst(items, loc);
            }
            return new BinaryExpressionAst(left, op, right, loc);
        }
        return left;
    }

    private static bool OpTakesCommaList(BinaryOp op) => op is
        BinaryOp.Replace or BinaryOp.IReplace or BinaryOp.CReplace or
        BinaryOp.Format or
        BinaryOp.Split or
        BinaryOp.Join;

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
            case TokenKind.OpMatch: op = BinaryOp.Match; break;
            case TokenKind.OpIMatch: op = BinaryOp.IMatch; break;
            case TokenKind.OpCMatch: op = BinaryOp.CMatch; break;
            case TokenKind.OpNotMatch: op = BinaryOp.NotMatch; break;
            case TokenKind.OpINotMatch: op = BinaryOp.INotMatch; break;
            case TokenKind.OpCNotMatch: op = BinaryOp.CNotMatch; break;
            case TokenKind.OpReplace: op = BinaryOp.Replace; break;
            case TokenKind.OpIReplace: op = BinaryOp.IReplace; break;
            case TokenKind.OpCReplace: op = BinaryOp.CReplace; break;
            case TokenKind.OpLike: op = BinaryOp.Like; break;
            case TokenKind.OpILike: op = BinaryOp.ILike; break;
            case TokenKind.OpCLike: op = BinaryOp.CLike; break;
            case TokenKind.OpNotLike: op = BinaryOp.NotLike; break;
            case TokenKind.OpINotLike: op = BinaryOp.INotLike; break;
            case TokenKind.OpCNotLike: op = BinaryOp.CNotLike; break;
            case TokenKind.OpContains: op = BinaryOp.Contains; break;
            case TokenKind.OpICContains: op = BinaryOp.ICContains; break;
            case TokenKind.OpCContains: op = BinaryOp.CContains; break;
            case TokenKind.OpNotContains: op = BinaryOp.NotContains; break;
            case TokenKind.OpINotContains: op = BinaryOp.INotContains; break;
            case TokenKind.OpCNotContains: op = BinaryOp.CNotContains; break;
            case TokenKind.OpIn: op = BinaryOp.In; break;
            case TokenKind.OpNotIn: op = BinaryOp.NotIn; break;
            case TokenKind.OpCIn: op = BinaryOp.CIn; break;
            case TokenKind.OpCNotIn: op = BinaryOp.CNotIn; break;
            case TokenKind.OpIIn: op = BinaryOp.IIn; break;
            case TokenKind.OpINotIn: op = BinaryOp.INotIn; break;
            case TokenKind.OpFormat: op = BinaryOp.Format; break;
            case TokenKind.OpJoin: op = BinaryOp.Join; break;
            case TokenKind.OpSplit: op = BinaryOp.Split; break;
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
        expr = ParsePostfixContinuation(expr);
        // Post-increment / post-decrement: `$i++`, `$i--`.
        if (IsAt(TokenKind.PlusPlus))
        {
            var loc = Current.Location;
            Advance();
            return new UnaryExpressionAst(UnaryOp.PostIncrement, expr, loc);
        }
        if (IsAt(TokenKind.MinusMinus))
        {
            var loc = Current.Location;
            Advance();
            return new UnaryExpressionAst(UnaryOp.PostDecrement, expr, loc);
        }
        return expr;
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
            {
                Advance();
                SkipNewlines();
                // Allow a command/pipeline inside parens. If the first token is an identifier
                // that's not a keyword in expression context, treat the whole paren as a
                // sub-pipeline whose final value is yielded as a subexpression.
                if (IsAt(TokenKind.Identifier) && !IsExpressionKeyword(Current.Text))
                {
                    var pipelineStmt = ParseCommandPipeline(t.Location);
                    SkipNewlines();
                    if (!IsAt(TokenKind.RParen))
                    {
                        if (IsAt(TokenKind.EndOfInput))
                            throw new PwshIncompleteInputException("Unterminated '(' — expected ')'.", t.Location);
                        throw new PwshParseException($"Expected ')', got {Current.Kind} '{Current.Text}'.", Current.Location);
                    }
                    Advance();
                    return new SubExpressionAst(new ScriptAst(new StatementAst[] { pipelineStmt }, t.Location), t.Location);
                }
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
            }
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
            case TokenKind.Ampersand:
            {
                // `& target` in expression position: evaluate by invoking a ScriptBlock or
                // path target, yielding the invocation's result. Args cannot be passed in this
                // form; for args, use `& target arg1 arg2` at statement start.
                var callLoc = Current.Location;
                Advance();
                var target = ParsePostfix();
                var elements = new List<CommandElementAst> { new CommandArgumentAst(target, callLoc) };
                var cmd = new CommandAst("&", elements, callLoc);
                var pipeline = new PipelineAst(new AstNode[] { cmd }, callLoc);
                return new SubExpressionAst(new ScriptAst(new StatementAst[] { pipeline }, callLoc), callLoc);
            }
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

    // ---------- Phase 3: keyword statements ----------

    private bool IsKeyword(string keyword)
        => IsAt(TokenKind.Identifier) && Current.Text.Equals(keyword, StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> _expressionKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "while", "do", "until", "for", "foreach", "in", "switch", "default",
        "function", "filter", "param", "begin", "process", "end",
        "try", "catch", "finally", "throw", "return", "break", "continue",
        "class", "enum", "else", "elseif",
    };

    private static bool IsExpressionKeyword(string text) => _expressionKeywords.Contains(text);

    private Token ConsumeKeyword(string keyword)
    {
        if (!IsKeyword(keyword))
            throw new PwshParseException($"Expected keyword '{keyword}'.", Current.Location);
        return Advance();
    }

    private bool IsDotSourceCandidate()
    {
        // Dot-source `.` must be followed by whitespace and then a command-like token:
        // `. ./script.ps1`. If `Dot` is adjacent to the next token, it's member access.
        if (!IsAt(TokenKind.Dot)) return false;
        var next = Peek(1);
        if (Current.Location.Offset + 1 == next.Location.Offset) return false;
        return true;
    }

    private IfStatementAst ParseIfStatement()
    {
        var start = ConsumeKeyword("if").Location;
        var branches = new List<(ExpressionAst, ScriptAst)>();
        ScriptAst? elseBody = null;

        void ParseBranch()
        {
            Consume(TokenKind.LParen);
            SkipNewlines();
            var cond = ParseLogicalOr();
            SkipNewlines();
            Consume(TokenKind.RParen);
            SkipNewlines();
            var body = ParseBraceBlock();
            branches.Add((cond, body));
        }

        ParseBranch();
        while (true)
        {
            SkipNewlines();
            if (IsKeyword("elseif"))
            {
                Advance();
                ParseBranch();
                continue;
            }
            if (IsKeyword("else"))
            {
                Advance();
                SkipNewlines();
                elseBody = ParseBraceBlock();
            }
            break;
        }
        return new IfStatementAst(branches, elseBody, start);
    }

    private WhileStatementAst ParseWhileStatement()
    {
        var start = ConsumeKeyword("while").Location;
        Consume(TokenKind.LParen);
        SkipNewlines();
        var cond = ParseLogicalOr();
        SkipNewlines();
        Consume(TokenKind.RParen);
        SkipNewlines();
        var body = ParseBraceBlock();
        return new WhileStatementAst(cond, body, start);
    }

    private DoWhileStatementAst ParseDoStatement()
    {
        var start = ConsumeKeyword("do").Location;
        SkipNewlines();
        var body = ParseBraceBlock();
        SkipNewlines();
        bool isUntil;
        if (IsKeyword("while")) { Advance(); isUntil = false; }
        else if (IsKeyword("until")) { Advance(); isUntil = true; }
        else throw new PwshParseException("Expected 'while' or 'until' after do-block.", Current.Location);
        Consume(TokenKind.LParen);
        SkipNewlines();
        var cond = ParseLogicalOr();
        SkipNewlines();
        Consume(TokenKind.RParen);
        return new DoWhileStatementAst(body, cond, isUntil, start);
    }

    private ForStatementAst ParseForStatement()
    {
        var start = ConsumeKeyword("for").Location;
        Consume(TokenKind.LParen);
        SkipNewlines();

        StatementAst? init = null;
        ExpressionAst? cond = null;
        StatementAst? update = null;

        if (!IsAt(TokenKind.Semicolon))
        {
            init = ParseStatement();
        }
        Consume(TokenKind.Semicolon);
        SkipNewlines();
        if (!IsAt(TokenKind.Semicolon))
        {
            cond = ParseLogicalOr();
        }
        Consume(TokenKind.Semicolon);
        SkipNewlines();
        if (!IsAt(TokenKind.RParen))
        {
            update = ParseStatement();
        }
        SkipNewlines();
        Consume(TokenKind.RParen);
        SkipNewlines();
        var body = ParseBraceBlock();
        return new ForStatementAst(init, cond, update, body, start);
    }

    private ForEachStatementAst ParseForEachStatement()
    {
        var start = ConsumeKeyword("foreach").Location;
        Consume(TokenKind.LParen);
        SkipNewlines();
        var varToken = Consume(TokenKind.Variable);
        var (scope, name) = ((string? Scope, string Name))varToken.Value!;
        if (scope != null)
            throw new PwshParseException("foreach variable cannot have a scope qualifier.", varToken.Location);
        if (!IsKeyword("in"))
            throw new PwshParseException("Expected 'in' in foreach statement.", Current.Location);
        Advance();
        SkipNewlines();
        var collection = ParseLogicalOr();
        SkipNewlines();
        Consume(TokenKind.RParen);
        SkipNewlines();
        var body = ParseBraceBlock();
        return new ForEachStatementAst(name, collection, body, start);
    }

    private SwitchStatementAst ParseSwitchStatement()
    {
        var start = ConsumeKeyword("switch").Location;
        Consume(TokenKind.LParen);
        SkipNewlines();
        var cond = ParseLogicalOr();
        SkipNewlines();
        Consume(TokenKind.RParen);
        SkipNewlines();
        Consume(TokenKind.LBrace);
        SkipNewlinesAndSemicolons();

        var cases = new List<(ExpressionAst, ScriptAst)>();
        ScriptAst? defaultBody = null;
        while (!IsAt(TokenKind.RBrace) && !IsAt(TokenKind.EndOfInput))
        {
            if (IsKeyword("default"))
            {
                Advance();
                SkipNewlines();
                defaultBody = ParseBraceBlock();
                SkipNewlinesAndSemicolons();
                continue;
            }
            var pattern = ParseLogicalOr();
            SkipNewlines();
            var body = ParseBraceBlock();
            cases.Add((pattern, body));
            SkipNewlinesAndSemicolons();
        }
        if (IsAt(TokenKind.EndOfInput))
            throw new PwshIncompleteInputException("Unterminated switch statement.", start);
        Consume(TokenKind.RBrace);
        return new SwitchStatementAst(cond, cases, defaultBody, start);
    }

    private BreakStatementAst ParseBreakStatement()
    {
        var start = ConsumeKeyword("break").Location;
        string? label = null;
        if (IsAt(TokenKind.Identifier) && !IsStatementTerminator())
        {
            label = Current.Text; Advance();
        }
        return new BreakStatementAst(label, start);
    }

    private ContinueStatementAst ParseContinueStatement()
    {
        var start = ConsumeKeyword("continue").Location;
        string? label = null;
        if (IsAt(TokenKind.Identifier) && !IsStatementTerminator())
        {
            label = Current.Text; Advance();
        }
        return new ContinueStatementAst(label, start);
    }

    private ReturnStatementAst ParseReturnStatement()
    {
        var start = ConsumeKeyword("return").Location;
        ExpressionAst? value = null;
        if (!IsStatementTerminator() && !IsAt(TokenKind.RBrace))
        {
            value = ParseLogicalOr();
        }
        return new ReturnStatementAst(value, start);
    }

    private ThrowStatementAst ParseThrowStatement()
    {
        var start = ConsumeKeyword("throw").Location;
        ExpressionAst? value = null;
        if (!IsStatementTerminator() && !IsAt(TokenKind.RBrace))
        {
            value = ParseLogicalOr();
        }
        return new ThrowStatementAst(value, start);
    }

    private bool IsStatementTerminator()
        => IsAt(TokenKind.EndOfInput) || IsAt(TokenKind.NewLine) || IsAt(TokenKind.Semicolon);

    private TryStatementAst ParseTryStatement()
    {
        var start = ConsumeKeyword("try").Location;
        SkipNewlines();
        var tryBody = ParseBraceBlock();
        SkipNewlines();

        var catches = new List<CatchClauseAst>();
        ScriptAst? finallyBody = null;

        while (IsKeyword("catch"))
        {
            var catchLoc = Advance().Location;
            var filters = new List<TypeLiteralAst>();
            SkipNewlines();
            while (IsAt(TokenKind.LBracket))
            {
                filters.Add(ParseTypeLiteral());
                SkipNewlines();
                if (IsAt(TokenKind.Comma)) { Advance(); SkipNewlines(); continue; }
                break;
            }
            SkipNewlines();
            var body = ParseBraceBlock();
            catches.Add(new CatchClauseAst(filters, body, catchLoc));
            SkipNewlines();
        }

        if (IsKeyword("finally"))
        {
            Advance();
            SkipNewlines();
            finallyBody = ParseBraceBlock();
        }

        if (catches.Count == 0 && finallyBody == null)
        {
            // If we're at EOF, the user probably just hasn't typed the catch/finally yet.
            // Signal incomplete so the multi-line REPL keeps reading.
            if (IsAt(TokenKind.EndOfInput))
                throw new PwshIncompleteInputException("Expected 'catch' or 'finally' after 'try' block.", start);
            throw new PwshParseException("'try' must be followed by at least one 'catch' or 'finally'.", start);
        }

        return new TryStatementAst(tryBody, catches, finallyBody, start);
    }

    private FunctionDefinitionAst ParseFunctionDefinition()
    {
        var start = Advance().Location; // 'function' or 'filter'
        if (!IsAt(TokenKind.Identifier))
            throw new PwshParseException("Expected function name.", Current.Location);
        var name = Advance().Text;
        SkipNewlines();
        Consume(TokenKind.LBrace);
        SkipNewlinesAndSemicolons();

        // Optional param(...) block right after {.
        var parameters = new List<ParameterAst>();
        if (IsKeyword("param"))
        {
            Advance();
            SkipNewlines();
            Consume(TokenKind.LParen);
            SkipNewlines();
            if (!IsAt(TokenKind.RParen))
            {
                parameters.Add(ParseParameter());
                SkipNewlines();
                while (IsAt(TokenKind.Comma))
                {
                    Advance();
                    SkipNewlines();
                    parameters.Add(ParseParameter());
                    SkipNewlines();
                }
            }
            Consume(TokenKind.RParen);
            SkipNewlinesAndSemicolons();
        }

        ScriptAst? beginBlock = null;
        ScriptAst? processBlock = null;
        ScriptAst? endBlock = null;
        ScriptAst? simpleBody = null;

        // Pipeline-participating form: one or more of begin/process/end labels at top level.
        if (IsKeyword("begin") || IsKeyword("process") || IsKeyword("end"))
        {
            while (!IsAt(TokenKind.RBrace) && !IsAt(TokenKind.EndOfInput))
            {
                if (IsKeyword("begin")) { Advance(); SkipNewlines(); beginBlock = ParseBraceBlock(); }
                else if (IsKeyword("process")) { Advance(); SkipNewlines(); processBlock = ParseBraceBlock(); }
                else if (IsKeyword("end")) { Advance(); SkipNewlines(); endBlock = ParseBraceBlock(); }
                else break;
                SkipNewlinesAndSemicolons();
            }
        }
        else
        {
            var statements = new List<StatementAst>();
            while (!IsAt(TokenKind.RBrace) && !IsAt(TokenKind.EndOfInput))
            {
                statements.Add(ParseStatement());
                if (IsAt(TokenKind.RBrace) || IsAt(TokenKind.EndOfInput)) break;
                SkipNewlinesAndSemicolons();
            }
            simpleBody = new ScriptAst(statements, start);
        }

        if (IsAt(TokenKind.EndOfInput))
            throw new PwshIncompleteInputException("Unterminated function body.", start);
        Consume(TokenKind.RBrace);

        return new FunctionDefinitionAst(name, parameters, beginBlock, processBlock, endBlock, simpleBody, start);
    }

    private ParameterAst ParseParameter()
    {
        var loc = Current.Location;
        TypeLiteralAst? type = null;
        if (IsAt(TokenKind.LBracket) && LooksLikeTypeLiteral())
        {
            type = ParseTypeLiteral();
            SkipNewlines();
        }
        var varToken = Consume(TokenKind.Variable);
        var (_, name) = ((string? Scope, string Name))varToken.Value!;
        ExpressionAst? defaultValue = null;
        SkipNewlines();
        if (IsAt(TokenKind.Equal))
        {
            Advance();
            SkipNewlines();
            defaultValue = ParseLogicalOr();
        }
        return new ParameterAst(name, type, defaultValue, loc);
    }

    private ClassDefinitionAst ParseClassDefinition()
    {
        var start = ConsumeKeyword("class").Location;
        if (!IsAt(TokenKind.Identifier))
            throw new PwshParseException("Expected class name.", Current.Location);
        var name = Advance().Text;
        SkipNewlines();
        Consume(TokenKind.LBrace);
        SkipNewlinesAndSemicolons();

        var properties = new List<ClassPropertyAst>();
        var methods = new List<ClassMethodAst>();

        while (!IsAt(TokenKind.RBrace) && !IsAt(TokenKind.EndOfInput))
        {
            var memberLoc = Current.Location;
            TypeLiteralAst? typeLit = null;
            if (IsAt(TokenKind.LBracket) && LooksLikeTypeLiteral())
            {
                typeLit = ParseTypeLiteral();
                SkipNewlines();
            }

            // Either a property (`$name` … `;`/newline) or a method (identifier `(`) or a
            // constructor (identifier equal to the class name followed by `(`).
            if (IsAt(TokenKind.Variable))
            {
                var vt = Advance();
                var (_, propName) = ((string? Scope, string Name))vt.Value!;
                ExpressionAst? defaultVal = null;
                SkipNewlines();
                if (IsAt(TokenKind.Equal))
                {
                    Advance();
                    SkipNewlines();
                    defaultVal = ParseLogicalOr();
                }
                properties.Add(new ClassPropertyAst(propName, typeLit, defaultVal, IsStatic: false, memberLoc));
                SkipNewlinesAndSemicolons();
                continue;
            }

            if (IsAt(TokenKind.Identifier))
            {
                var methodName = Advance().Text;
                Consume(TokenKind.LParen);
                SkipNewlines();
                var parameters = new List<ParameterAst>();
                if (!IsAt(TokenKind.RParen))
                {
                    parameters.Add(ParseParameter());
                    SkipNewlines();
                    while (IsAt(TokenKind.Comma))
                    {
                        Advance();
                        SkipNewlines();
                        parameters.Add(ParseParameter());
                        SkipNewlines();
                    }
                }
                Consume(TokenKind.RParen);
                SkipNewlines();
                var body = ParseBraceBlock();
                bool isConstructor = methodName.Equals(name, StringComparison.OrdinalIgnoreCase);
                methods.Add(new ClassMethodAst(methodName, parameters, typeLit, body, IsStatic: false, IsConstructor: isConstructor, memberLoc));
                SkipNewlinesAndSemicolons();
                continue;
            }

            throw new PwshParseException($"Unexpected token {Current.Kind} '{Current.Text}' in class body.", Current.Location);
        }

        if (IsAt(TokenKind.EndOfInput))
            throw new PwshIncompleteInputException("Unterminated class definition.", start);
        Consume(TokenKind.RBrace);
        return new ClassDefinitionAst(name, properties, methods, start);
    }

    private EnumDefinitionAst ParseEnumDefinition()
    {
        var start = ConsumeKeyword("enum").Location;
        if (!IsAt(TokenKind.Identifier))
            throw new PwshParseException("Expected enum name.", Current.Location);
        var name = Advance().Text;
        SkipNewlines();
        Consume(TokenKind.LBrace);
        SkipNewlinesAndSemicolons();

        var members = new List<EnumMemberAst>();
        while (!IsAt(TokenKind.RBrace) && !IsAt(TokenKind.EndOfInput))
        {
            if (!IsAt(TokenKind.Identifier))
                throw new PwshParseException("Expected enum member name.", Current.Location);
            var memberLoc = Current.Location;
            var memberName = Advance().Text;
            long? value = null;
            SkipNewlines();
            if (IsAt(TokenKind.Equal))
            {
                Advance();
                SkipNewlines();
                var valToken = Consume(TokenKind.Number);
                value = Convert.ToInt64(valToken.Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            members.Add(new EnumMemberAst(memberName, value, memberLoc));
            SkipNewlinesAndSemicolons();
            if (IsAt(TokenKind.Comma)) { Advance(); SkipNewlinesAndSemicolons(); }
        }
        if (IsAt(TokenKind.EndOfInput))
            throw new PwshIncompleteInputException("Unterminated enum definition.", start);
        Consume(TokenKind.RBrace);
        return new EnumDefinitionAst(name, members, start);
    }

    private ScriptAst ParseBraceBlock()
    {
        var start = Current.Location;
        Consume(TokenKind.LBrace);
        SkipNewlinesAndSemicolons();
        var statements = new List<StatementAst>();
        while (!IsAt(TokenKind.RBrace) && !IsAt(TokenKind.EndOfInput))
        {
            statements.Add(ParseStatement());
            if (IsAt(TokenKind.RBrace) || IsAt(TokenKind.EndOfInput)) break;
            if (!IsAt(TokenKind.NewLine) && !IsAt(TokenKind.Semicolon))
                throw new PwshParseException(
                    $"Expected statement separator, got {Current.Kind} '{Current.Text}'.", Current.Location);
            SkipNewlinesAndSemicolons();
        }
        if (IsAt(TokenKind.EndOfInput))
            throw new PwshIncompleteInputException("Unterminated brace block.", start);
        Consume(TokenKind.RBrace);
        return new ScriptAst(statements, start);
    }

    private bool IsPathLikeCommand()
    {
        // First token is Dot or Slash; we expect it to be adjacent to something that makes it
        // a path (e.g. `./foo`, `/usr/...`). Not adjacent → leave for other paths.
        if (!(IsAt(TokenKind.Dot) || IsAt(TokenKind.Slash))) return false;
        var next = Peek(1);
        return Current.Location.Offset + Current.Location.Length == next.Location.Offset;
    }

    private StatementAst ParsePathCommandStatement(SourceLocation loc)
    {
        // The command name is a bare-word made of all adjacent tokens starting from the
        // leading Dot/Slash. Reuse ParseBareWord's adjacency logic.
        var nameAst = (StringLiteralAst)ParseBareWord();
        string name = "";
        foreach (var part in nameAst.Parts)
            if (part is Lexer.LiteralPart lp) name += lp.Text;
        var elements = new List<CommandElementAst>();
        while (IsCommandElementStart())
        {
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
            var argLoc = Current.Location;
            var expr = ParseCommandArgument();
            elements.Add(new CommandArgumentAst(expr, argLoc));
        }
        var cmd = new CommandAst(name, elements, loc);
        if (!IsAt(TokenKind.Pipe))
            return new PipelineAst(new AstNode[] { cmd }, loc);
        var stages = new List<AstNode> { cmd };
        while (IsAt(TokenKind.Pipe))
        {
            Advance();
            SkipNewlines();
            stages.Add(ParsePipelineStage());
        }
        return new PipelineAst(stages, loc);
    }

    private StatementAst ParseCallOperatorStatement(SourceLocation loc)
    {
        Consume(TokenKind.Ampersand);
        // Target is an expression: a script block ({ ... }), a variable ($block), or a
        // string path. We pass it + args to the interpreter via a synthetic CommandAst
        // whose name is the sentinel "&" and whose first positional is the target.
        var target = ParsePostfix();
        var elements = new List<CommandElementAst> { new CommandArgumentAst(target, loc) };
        while (IsCommandElementStart())
        {
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
            var argLoc = Current.Location;
            var expr = ParseCommandArgument();
            elements.Add(new CommandArgumentAst(expr, argLoc));
        }
        return new PipelineAst(new AstNode[] { new CommandAst("&", elements, loc) }, loc);
    }

    private StatementAst ParseDotSourceStatement(SourceLocation loc)
    {
        Consume(TokenKind.Dot);
        // The rest of the line is a command-mode invocation; we wrap it as a CommandAst with
        // a sentinel name `.` that the interpreter interprets as dot-source.
        var elements = new List<CommandElementAst>();
        while (IsCommandElementStart())
        {
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
            var argLoc = Current.Location;
            var expr = ParseCommandArgument();
            elements.Add(new CommandArgumentAst(expr, argLoc));
        }
        return new PipelineAst(new AstNode[] { new CommandAst(".", elements, loc) }, loc);
    }
}
