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
            if (!AllowsImplicitStatementSeparator(s) && !IsAt(TokenKind.NewLine) && !IsAt(TokenKind.Semicolon))
                throw new PwshParseException(
                    $"Expected statement separator, got {Current.Kind} '{Current.Text}'.", Current.Location);
            SkipNewlinesAndSemicolons();
        }
        return new ScriptAst(statements, start);
    }

    private StatementAst ParseStatement()
    {
        // Skip any leading attribute specifiers (`[CmdletBinding(...)]`, `[ValidateSet(...)]`,
        // …). Phase 1 treats them as no-ops; they exist purely so real-world scripts parse.
        while (IsAttributeSpec())
        {
            ConsumeAttributeSpec();
            SkipNewlines();
        }

        var loc = Current.Location;

        if (TryParseStatementLabel(out var label))
        {
            if (IsKeyword("while")) return ParseWhileStatement(label);
            if (IsKeyword("do")) return ParseDoStatement(label);
            if (IsKeyword("for")) return ParseForStatement(label);
            if (IsKeyword("foreach")) return ParseForEachStatement(label);
            if (IsKeyword("switch")) return ParseSwitchStatement(label);
            throw new PwshParseException("A statement label must target while, do, for, foreach, or switch.", Current.Location);
        }

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
                case "param": return ParseTopLevelParamBlock();
                case "try": return ParseTryStatement();
                case "throw": return ParseThrowStatement();
                case "return": return ParseReturnStatement();
                case "break": return ParseBreakStatement();
                case "continue": return ParseContinueStatement();
                case "workflow":
                    throw new PwshParseException("Workflow is not supported in PowerShell 6+.", Current.Location);
                case "class": return ParseClassDefinition();
                case "enum": return ParseEnumDefinition();
            }
            return ParseCommandPipeline(loc);
        }

        if (IsCommandPipelineStart())
        {
            return ParseCommandPipeline(loc);
        }

        var expr = ParseLogicalOr();

        // Top-level comma operator: `1, 2, 3` forms an array, matching real pwsh. Comma has
        // the lowest precedence of any operator, so it attaches here after ParseLogicalOr.
        if (IsAt(TokenKind.Comma))
        {
            var elems = new List<ExpressionAst> { expr };
            while (IsAt(TokenKind.Comma))
            {
                Advance();
                SkipNewlines();
                elems.Add(ParseLogicalOr());
            }
            expr = new ArrayExpressionAst(elems, loc);
        }

        if (IsAssignmentOp(Current.Kind) || IsNullCoalesceAssignmentOp())
        {
            if (IsNullCoalesceAssignmentOp())
            {
                Advance();
                Advance();
                SkipNewlines();
                var coalesceRhs = ParseRhsPipeline();
                var value = new BinaryExpressionAst(expr, BinaryOp.Coalesce, coalesceRhs, loc);
                return new AssignmentStatementAst(expr, AssignmentOp.Assign, value, loc);
            }

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

        SkipNewlinesBeforePipelineOrRedirection();
        if (IsAt(TokenKind.Pipe) || IsAt(TokenKind.Redirection) || IsBackgroundSuffix())
            return ParseExpressionPipelineOrRedirection(expr, loc);

        return new ExpressionStatementAst(expr, loc);
    }

    /// <summary>Parse the RHS of an assignment, which may contain a pipeline. Returns an
    /// <see cref="ExpressionAst"/>; pipeline RHS is wrapped in a <see cref="SubExpressionAst"/>
    /// that executes the pipeline and yields its final result.</summary>
    private ExpressionAst ParseRhsPipeline()
    {
        return ParseCommandCapableValueExpression(allowCommaList: true, allowPipeline: true);
    }

    private ExpressionAst ParseAssignmentExpression()
    {
        var loc = Current.Location;
        var left = ParseLogicalOr();
        if (!IsAssignmentOp(Current.Kind) && !IsNullCoalesceAssignmentOp())
            return left;

        if (IsNullCoalesceAssignmentOp())
        {
            Advance();
            Advance();
            SkipNewlines();
            var coalesceRhs = ParseRhsPipeline();
            return new AssignmentExpressionAst(left, AssignmentOp.Assign, new BinaryExpressionAst(left, BinaryOp.Coalesce, coalesceRhs, loc), loc);
        }

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
        var rhs = ParseRhsPipeline();
        return new AssignmentExpressionAst(left, op, rhs, loc);
    }

    private StatementAst ParseCommandPipeline(SourceLocation loc)
    {
        var statement = ParseSimpleCommandPipeline(loc);
        if (!IsPipelineChainOperator())
            return statement;

        var expr = WrapStatementAsSubExpression(statement, loc);
        while (IsPipelineChainOperator())
        {
            var op = Current.Kind == TokenKind.OpAnd ? BinaryOp.And : BinaryOp.Or;
            var opLoc = Current.Location;
            Advance();
            SkipNewlines();
            var rhs = ParsePipelineChainOperand();
            expr = new BinaryExpressionAst(expr, op, rhs, opLoc);
        }

        return new ExpressionStatementAst(expr, loc);
    }

    private AstNode ParsePipelineStage()
    {
        if (IsCommandPipelineStart())
            return ParseCommandStage();
        return ParseLogicalOr();
    }

    private static bool IsAssignmentOp(TokenKind k) => k is
        TokenKind.Equal or TokenKind.PlusEqual or TokenKind.MinusEqual or
        TokenKind.StarEqual or TokenKind.SlashEqual or TokenKind.PercentEqual;

    private bool IsNullCoalesceAssignmentOp()
        => IsAt(TokenKind.QuestionQuestion) &&
           Peek(1).Kind == TokenKind.Equal &&
           Current.Location.Offset + Current.Location.Length == Peek(1).Location.Offset;

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
        if (IsAt(TokenKind.QuestionQuestion) && !IsNullCoalesceAssignmentOp())
        {
            var loc = Current.Location;
            Advance();
            SkipNewlines();
            var right = ParseLogicalOr();
            left = new BinaryExpressionAst(left, BinaryOp.Coalesce, right, loc);
        }
        if (IsAt(TokenKind.Question))
        {
            var loc = Current.Location;
            Advance();
            SkipNewlines();
            var whenTrue = ParseLogicalOr();
            SkipNewlines();
            Consume(TokenKind.Colon);
            SkipNewlines();
            var whenFalse = ParseLogicalOr();
            return new ConditionalExpressionAst(left, whenTrue, whenFalse, loc);
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
        while (IsAt(TokenKind.OpBand) || IsAt(TokenKind.OpBor) || IsAt(TokenKind.OpBxor) ||
               IsAt(TokenKind.OpShl) || IsAt(TokenKind.OpShr))
        {
            var op = Current.Kind switch
            {
                TokenKind.OpBand => BinaryOp.BAnd,
                TokenKind.OpBor => BinaryOp.BOr,
                TokenKind.OpShl => BinaryOp.Shl,
                TokenKind.OpShr => BinaryOp.Shr,
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
        left = PromoteComparisonOperandCommaList(left);
        while (TryConsumeComparisonOp(out var op, out var loc))
        {
            SkipNewlines();
            var right = ParseAdditive();
            // Comparison operators in pwsh accept array-valued operands built from comma
            // expressions, e.g. `"a", "b" -contains $x`, `1 -eq 1, 2`, `'{0}' -f $a, $b`.
            if (IsAt(TokenKind.Comma))
            {
                var items = new List<ExpressionAst> { right };
                while (IsAt(TokenKind.Comma))
                {
                    Advance();
                    SkipNewlines();
                    items.Add(ParseAssignmentExpression());
                }
                right = new ArrayExpressionAst(items, loc);
            }
            left = new BinaryExpressionAst(left, op, right, loc);
        }
        return left;
    }

    private ExpressionAst PromoteComparisonOperandCommaList(ExpressionAst left)
    {
        if (!IsAt(TokenKind.Comma) || !HasComparisonOperatorAfterCommaSequence())
            return left;

        var items = new List<ExpressionAst> { left };
        while (IsAt(TokenKind.Comma))
        {
            Advance();
            SkipNewlines();
            items.Add(ParseAdditive());
        }

        return new ArrayExpressionAst(items, left.Location);
    }

    private bool HasComparisonOperatorAfterCommaSequence()
    {
        var save = _cursor;
        try
        {
            while (IsAt(TokenKind.Comma))
            {
                Advance();
                SkipNewlines();
                _ = ParseAdditive();
            }

            return IsComparisonOperatorToken(Current.Kind);
        }
        catch (PwshParseException)
        {
            return false;
        }
        finally
        {
            _cursor = save;
        }
    }

    private static bool IsComparisonOperatorToken(TokenKind kind) => kind switch
    {
        TokenKind.OpEq or TokenKind.OpNe or
        TokenKind.OpLt or TokenKind.OpLe or TokenKind.OpGt or TokenKind.OpGe or
        TokenKind.OpIeq or TokenKind.OpIne or TokenKind.OpIlt or TokenKind.OpIle or
        TokenKind.OpIgt or TokenKind.OpIge or
        TokenKind.OpCeq or TokenKind.OpCne or TokenKind.OpClt or TokenKind.OpCle or
        TokenKind.OpCgt or TokenKind.OpCge or
        TokenKind.OpIs or TokenKind.OpIsNot or TokenKind.OpAs or
        TokenKind.OpMatch or TokenKind.OpIMatch or TokenKind.OpCMatch or
        TokenKind.OpNotMatch or TokenKind.OpINotMatch or TokenKind.OpCNotMatch or
        TokenKind.OpReplace or TokenKind.OpIReplace or TokenKind.OpCReplace or
        TokenKind.OpLike or TokenKind.OpILike or TokenKind.OpCLike or
        TokenKind.OpNotLike or TokenKind.OpINotLike or TokenKind.OpCNotLike or
        TokenKind.OpContains or TokenKind.OpICContains or TokenKind.OpCContains or
        TokenKind.OpNotContains or TokenKind.OpINotContains or TokenKind.OpCNotContains or
        TokenKind.OpIn or TokenKind.OpNotIn or
        TokenKind.OpCIn or TokenKind.OpCNotIn or
        TokenKind.OpIIn or TokenKind.OpINotIn or
        TokenKind.OpFormat or TokenKind.OpJoin or TokenKind.OpSplit => true,
        _ => false,
    };

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
        if (IsAt(TokenKind.Comma))
        {
            var loc = Current.Location;
            Advance();
            var inner = ParseUnary();
            return new ArrayExpressionAst(new[] { inner }, loc);
        }
        if (IsAt(TokenKind.PlusPlus))
        {
            var loc = Current.Location;
            Advance();
            var inner = ParseUnary();
            return new UnaryExpressionAst(UnaryOp.PreIncrement, inner, loc);
        }
        if (IsAt(TokenKind.MinusMinus))
        {
            var loc = Current.Location;
            Advance();
            var inner = ParseUnary();
            return new UnaryExpressionAst(UnaryOp.PreDecrement, inner, loc);
        }
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
        if (IsAt(TokenKind.OpJoin))
        {
            var loc = Current.Location;
            Advance();
            var inner = ParseUnary();
            return new UnaryExpressionAst(UnaryOp.Join, inner, loc);
        }
        if (IsAt(TokenKind.OpSplit))
        {
            var loc = Current.Location;
            Advance();
            var inner = ParseUnary();
            return new UnaryExpressionAst(UnaryOp.Split, inner, loc);
        }
        // [Type]expr is a cast when followed by an atom-start token.
        if (IsAt(TokenKind.LBracket) && LooksLikeTypeLiteral())
        {
            var typeLit = ParseTypeLiteral();
            var save = _cursor;
            while (IsAttributeSpec())
            {
                ConsumeAttributeSpec();
            }
            if (IsCastFollower(Current.Kind))
            {
                var operand = ParseUnary();
                return new CastExpressionAst(typeLit, operand, typeLit.Location);
            }
            _cursor = save;
            // Not a cast: continue parsing type literal (or member access on it) via postfix.
            return ParsePostfixContinuation(typeLit);
        }
        return ParsePostfix();
    }

    private static bool IsCastFollower(TokenKind k) => k is
        TokenKind.Variable or TokenKind.Number or TokenKind.String or
        TokenKind.LParen or TokenKind.DollarLParen or TokenKind.AtLParen or TokenKind.AtLBrace or
        TokenKind.LBrace or TokenKind.Minus or TokenKind.Plus or TokenKind.Bang or TokenKind.OpNot or
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
            else if (t.Kind != TokenKind.Identifier && t.Kind != TokenKind.Number &&
                     t.Kind != TokenKind.Dot && t.Kind != TokenKind.Plus && t.Kind != TokenKind.Comma)
            {
                return false;
            }
            i++;
        }
        return depth == 0;
    }

    /// <summary>
    /// Return <see langword="true"/> if the current position begins a pwsh attribute
    /// specifier like <c>[CmdletBinding(…)]</c>, <c>[Parameter(Mandatory=$true)]</c>, or
    /// <c>[ValidateSet('a','b')]</c>. Distinguished from a plain type literal by the
    /// presence of a left-paren after the type name.
    /// </summary>
    private bool IsAttributeSpec()
    {
        if (Current.Kind != TokenKind.LBracket) return false;
        int i = _cursor + 1;
        if (i >= _tokens.Count || _tokens[i].Kind != TokenKind.Identifier) return false;
        i++;
        while (i < _tokens.Count && _tokens[i].Kind == TokenKind.Dot)
        {
            i++;
            if (i >= _tokens.Count || _tokens[i].Kind != TokenKind.Identifier) return false;
            i++;
        }
        return i < _tokens.Count && _tokens[i].Kind == TokenKind.LParen;
    }

    /// <summary>
    /// Consume and discard an attribute specifier. Phase 1 treats attributes as semantic
    /// no-ops — we don't enforce <c>[ValidateSet]</c>, we don't honor
    /// <c>[CmdletBinding(SupportsShouldProcess)]</c>, and we don't mirror them into the
    /// parameter AST. The parser just has to accept them so real-world scripts parse.
    /// </summary>
    private void ConsumeAttributeSpec()
    {
        Consume(TokenKind.LBracket);
        int depth = 1;
        while (depth > 0 && !IsAt(TokenKind.EndOfInput))
        {
            if (IsAt(TokenKind.LBracket)) depth++;
            else if (IsAt(TokenKind.RBracket))
            {
                depth--;
                Advance();
                if (depth == 0) return;
                continue;
            }
            Advance();
        }
        throw new PwshParseException("Unterminated attribute specifier.", Current.Location);
    }

    private TypeLiteralAst ParseTypeLiteral()
    {
        var start = Current.Location;
        Consume(TokenKind.LBracket);
        var typeName = ParseTypeName();
        var genericArgs = new List<TypeLiteralAst>();
        int arrayRank = 0;
        // After the name, pwsh accepts two different bracketed suffixes:
        //   `[]` — array rank increment (zero inside the brackets).
        //   `[T1,T2,...]` — generic type arguments (pwsh's shorthand for `HashSet<T>`).
        // Distinguished by whether the inner `[` is immediately followed by `]`.
        while (IsAt(TokenKind.LBracket))
        {
            if (TryConsumeArrayTypeSuffix())
            {
                arrayRank++;
                continue;
            }
            // Generic arguments.
            Advance();
            SkipNewlines();
            genericArgs.Add(ParseTypeLiteralInner());
            SkipNewlines();
            while (IsAt(TokenKind.Comma))
            {
                Advance();
                SkipNewlines();
                genericArgs.Add(ParseTypeLiteralInner());
                SkipNewlines();
            }
            Consume(TokenKind.RBracket);
        }
        SkipAssemblyQualifiedTypeTail();
        Consume(TokenKind.RBracket);
        return new TypeLiteralAst(typeName, genericArgs, arrayRank, start);
    }

    /// <summary>
    /// Parse a type literal in a context that has already consumed the opening `[` — i.e.
    /// inside generic-argument brackets. Handles the same name / array / generic / trailing-
    /// bracket shape as <see cref="ParseTypeLiteral"/> without the outer brackets.
    /// </summary>
    private TypeLiteralAst ParseTypeLiteralInner()
    {
        if (IsAt(TokenKind.LBracket))
            return ParseTypeLiteral();

        var start = Current.Location;
        var typeName = ParseTypeName();
        var genericArgs = new List<TypeLiteralAst>();
        int arrayRank = 0;
        while (IsAt(TokenKind.LBracket))
        {
            if (TryConsumeArrayTypeSuffix())
            {
                arrayRank++;
                continue;
            }
            Advance();
            SkipNewlines();
            genericArgs.Add(ParseTypeLiteralInner());
            SkipNewlines();
            while (IsAt(TokenKind.Comma))
            {
                Advance();
                SkipNewlines();
                genericArgs.Add(ParseTypeLiteralInner());
                SkipNewlines();
            }
            Consume(TokenKind.RBracket);
        }
        return new TypeLiteralAst(typeName, genericArgs, arrayRank, start);
    }

    private string ParseTypeName()
    {
        var first = Consume(TokenKind.Identifier);
        var sb = new System.Text.StringBuilder(GetCommandTextTokenText(first));
        int endOffset = first.Location.Offset + first.Location.Length;
        AppendAdjacentTypeAritySuffix(sb, ref endOffset);
        while (IsAt(TokenKind.Dot) || IsAt(TokenKind.Plus))
        {
            var separator = Advance().Text;
            var next = Consume(TokenKind.Identifier);
            sb.Append(separator).Append(GetCommandTextTokenText(next));
            endOffset = next.Location.Offset + next.Location.Length;
            AppendAdjacentTypeAritySuffix(sb, ref endOffset);
        }
        return sb.ToString();
    }

    private void AppendAdjacentTypeAritySuffix(System.Text.StringBuilder sb, ref int endOffset)
    {
        while (IsAt(TokenKind.Identifier) &&
               Current.Text.StartsWith('`') &&
               Current.Location.Offset == endOffset)
        {
            sb.Append(GetCommandTextTokenText(Current));
            endOffset = Current.Location.Offset + Current.Location.Length;
            Advance();
        }

        while (IsAt(TokenKind.Number) &&
               Current.Location.Offset == endOffset &&
               sb.Length > 0 &&
               sb[^1] == '`')
        {
            sb.Append(Current.Text);
            endOffset = Current.Location.Offset + Current.Location.Length;
            Advance();
        }
    }

    private void SkipAssemblyQualifiedTypeTail()
    {
        while (!IsAt(TokenKind.RBracket) && !IsAt(TokenKind.EndOfInput))
        {
            Advance();
        }
    }

    private bool TryConsumeArrayTypeSuffix()
    {
        if (!IsAt(TokenKind.LBracket))
            return false;

        var save = _cursor;
        Advance();
        SkipNewlines();
        while (IsAt(TokenKind.Comma))
        {
            Advance();
            SkipNewlines();
        }

        if (IsAt(TokenKind.RBracket))
        {
            Advance();
            return true;
        }

        _cursor = save;
        return false;
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
            if (IsAt(TokenKind.Question) &&
                CurrentIsAdjacentToPreviousToken() &&
                Peek(1).Kind == TokenKind.LBracket &&
                Current.Location.Offset + Current.Location.Length == Peek(1).Location.Offset)
            {
                var loc = Current.Location;
                Advance();
                Consume(TokenKind.LBracket);
                SkipNewlines();
                var index = ParseAssignmentExpression();
                SkipNewlines();
                if (IsAt(TokenKind.Comma))
                {
                    var indices = new List<ExpressionAst> { index };
                    while (IsAt(TokenKind.Comma))
                    {
                        Advance();
                        SkipNewlines();
                        indices.Add(ParseAssignmentExpression());
                        SkipNewlines();
                    }
                    index = new ArrayExpressionAst(indices, loc);
                }
                SkipNewlines();
                Consume(TokenKind.RBracket);
                var access = new IndexerAst(expr, index, loc);
                expr = WrapNullConditional(expr, access, loc);
                continue;
            }
            if (IsAt(TokenKind.Question) &&
                CurrentIsAdjacentToPreviousToken() &&
                Peek(1).Kind == TokenKind.Dot &&
                Current.Location.Offset + Current.Location.Length == Peek(1).Location.Offset)
            {
                var loc = Current.Location;
                Advance();
                Consume(TokenKind.Dot);
                SkipNewlines();
                if (TryParseDynamicMemberName(out var dynamicName))
                {
                    List<ExpressionAst>? dynamicArgs = null;
                    bool dynamicInvocation = false;
                    if (IsAt(TokenKind.LParen) && CurrentIsAdjacentToPreviousToken())
                    {
                        dynamicArgs = ParseInvocationArgs();
                        dynamicInvocation = true;
                    }

                    var guarded = new DynamicMemberAccessAst(expr, dynamicName, IsStatic: false, IsInvocation: dynamicInvocation, Arguments: dynamicArgs, Location: loc);
                    expr = WrapNullConditional(expr, guarded, loc);
                    continue;
                }

                var name = ParseMemberName();
                TrySkipMemberGenericTypeArguments();
                List<ExpressionAst>? args = null;
                bool invocation = false;
                if (IsAt(TokenKind.LParen) && CurrentIsAdjacentToPreviousToken())
                {
                    args = ParseInvocationArgs();
                    invocation = true;
                }
                var member = new MemberAccessAst(expr, name, IsStatic: false, IsInvocation: invocation, Arguments: args, Location: loc);
                expr = WrapNullConditional(expr, member, loc);
                continue;
            }
            if (IsAt(TokenKind.Dot) && CurrentIsAdjacentToPreviousToken())
            {
                var loc = Current.Location;
                Advance();
                SkipNewlines();
                if (TryParseDynamicMemberName(out var dynamicName))
                {
                    List<ExpressionAst>? dynamicArgs = null;
                    bool dynamicInvocation = false;
                    if (IsAt(TokenKind.LParen) && CurrentIsAdjacentToPreviousToken())
                    {
                        dynamicArgs = ParseInvocationArgs();
                        dynamicInvocation = true;
                    }
                    expr = new DynamicMemberAccessAst(expr, dynamicName, IsStatic: false, IsInvocation: dynamicInvocation, Arguments: dynamicArgs, Location: loc);
                    continue;
                }
                var name = ParseMemberName();
                TrySkipMemberGenericTypeArguments();
                List<ExpressionAst>? args = null;
                bool invocation = false;
                if (IsAt(TokenKind.LParen) && CurrentIsAdjacentToPreviousToken())
                {
                    args = ParseInvocationArgs();
                    invocation = true;
                }
                else if (IsAt(TokenKind.LBrace) && CurrentIsAdjacentToPreviousToken() && IsIntrinsicCollectionMethodWithoutParens(name))
                {
                    args = new List<ExpressionAst> { ParsePostfix() };
                    invocation = true;
                }
                expr = new MemberAccessAst(expr, name, IsStatic: false, IsInvocation: invocation, Arguments: args, Location: loc);
                continue;
            }
            if (IsAt(TokenKind.ColonColon) && CurrentIsAdjacentToPreviousToken())
            {
                var loc = Current.Location;
                Advance();
                SkipNewlines();
                if (TryParseDynamicMemberName(out var dynamicName))
                {
                    List<ExpressionAst>? dynamicArgs = null;
                    bool dynamicInvocation = false;
                    if (IsAt(TokenKind.LParen) && CurrentIsAdjacentToPreviousToken())
                    {
                        dynamicArgs = ParseInvocationArgs();
                        dynamicInvocation = true;
                    }
                    expr = new DynamicMemberAccessAst(expr, dynamicName, IsStatic: true, IsInvocation: dynamicInvocation, Arguments: dynamicArgs, Location: loc);
                    continue;
                }
                var name = ParseMemberName();
                TrySkipMemberGenericTypeArguments();
                List<ExpressionAst>? args = null;
                bool invocation = false;
                if (IsAt(TokenKind.LParen) && CurrentIsAdjacentToPreviousToken())
                {
                    args = ParseInvocationArgs();
                    invocation = true;
                }
                expr = new MemberAccessAst(expr, name, IsStatic: true, IsInvocation: invocation, Arguments: args, Location: loc);
                continue;
            }
            if (IsAt(TokenKind.LBracket) && CurrentIsAdjacentToPreviousToken())
            {
                var loc = Current.Location;
                Advance();
                SkipNewlines();
                var index = ParseAssignmentExpression();
                SkipNewlines();
                if (IsAt(TokenKind.Comma))
                {
                    var indices = new List<ExpressionAst> { index };
                    while (IsAt(TokenKind.Comma))
                    {
                        Advance();
                        SkipNewlines();
                        indices.Add(ParseAssignmentExpression());
                        SkipNewlines();
                    }
                    index = new ArrayExpressionAst(indices, loc);
                }
                SkipNewlines();
                Consume(TokenKind.RBracket);
                expr = new IndexerAst(expr, index, loc);
                continue;
            }
            if (IsAt(TokenKind.Number) &&
                Current.Text.StartsWith(".", StringComparison.Ordinal) &&
                CurrentIsAdjacentToPreviousToken())
            {
                var token = Advance();
                expr = new MemberAccessAst(expr, token.Text[1..], IsStatic: false, IsInvocation: false, Arguments: null, Location: token.Location);
                continue;
            }
            return expr;
        }
    }

    private static ExpressionAst WrapNullConditional(ExpressionAst receiver, ExpressionAst access, SourceLocation loc)
    {
        var isNull = new BinaryExpressionAst(receiver, BinaryOp.Equal, new NullLiteralAst(loc), loc);
        return new ConditionalExpressionAst(isNull, new NullLiteralAst(loc), access, loc);
    }

    private bool CurrentIsAdjacentToPreviousToken()
    {
        if (_cursor <= 0)
            return false;
        var previous = _tokens[_cursor - 1];
        return previous.Location.Offset + previous.Location.Length == Current.Location.Offset;
    }

    private string ParseMemberName()
    {
        if (IsAt(TokenKind.Identifier))
            return GetCommandTextTokenText(Advance());
        if (IsAt(TokenKind.Number))
        {
            var token = Advance();
            return token.Text.StartsWith(".", StringComparison.Ordinal)
                ? token.Text[1..]
                : token.Text;
        }
        if (IsAt(TokenKind.String))
        {
            var token = Advance();
            var parts = (IReadOnlyList<StringPart>)token.Value!;
            return string.Concat(parts.Select(GetStringPartText));
        }
        throw new PwshParseException($"Expected member name, got {Current.Kind} '{Current.Text}'.", Current.Location);
    }

    private void TrySkipMemberGenericTypeArguments()
    {
        if (!IsAt(TokenKind.LBracket) || !CurrentIsAdjacentToPreviousToken())
            return;

        var save = _cursor;
        try
        {
            Advance();
            SkipNewlines();
            _ = ParseTypeLiteralInner();
            SkipNewlines();
            while (IsAt(TokenKind.Comma))
            {
                Advance();
                SkipNewlines();
                _ = ParseTypeLiteralInner();
                SkipNewlines();
            }

            Consume(TokenKind.RBracket);
            if (!(IsAt(TokenKind.LParen) && CurrentIsAdjacentToPreviousToken()))
            {
                _cursor = save;
            }
        }
        catch (PwshParseException)
        {
            _cursor = save;
        }
    }

    private bool TryParseDynamicMemberName(out ExpressionAst expression)
    {
        if (IsAt(TokenKind.Variable) || IsAt(TokenKind.DollarLParen) || IsAt(TokenKind.LParen))
        {
            expression = ParsePostfix();
            return true;
        }
        if (IsAt(TokenKind.LBrace))
        {
            expression = ParseBraceMemberNameExpression();
            return true;
        }

        expression = null!;
        return false;
    }

    private ExpressionAst ParseBraceMemberNameExpression()
    {
        var start = Consume(TokenKind.LBrace).Location;
        var startCursor = _cursor;
        while (!IsAt(TokenKind.RBrace) && !IsAt(TokenKind.EndOfInput))
        {
            Advance();
        }
        if (IsAt(TokenKind.EndOfInput))
            throw new PwshIncompleteInputException("Unterminated brace-wrapped member name.", start);

        var text = ReconstructTokenSource(startCursor, _cursor).Trim();
        Consume(TokenKind.RBrace);
        return new StringLiteralAst(new List<StringPart> { new LiteralPart(text) }, IsSingleQuoted: true, start);
    }

    private List<ExpressionAst> ParseInvocationArgs()
    {
        Consume(TokenKind.LParen);
        SkipNewlines();
        var args = new List<ExpressionAst>();
        if (!IsAt(TokenKind.RParen))
        {
            args.Add(ParseAssignmentExpression());
            SkipNewlines();
            while (IsAt(TokenKind.Comma))
            {
                Advance();
                SkipNewlines();
                args.Add(ParseAssignmentExpression());
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
                // Allow a command/pipeline inside parens.
                if (IsCommandPipelineStart())
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
                var inner = ParseAssignmentExpression();
                SkipNewlines();
                if (IsAt(TokenKind.Comma))
                {
                    var elems = new List<ExpressionAst> { inner };
                    while (IsAt(TokenKind.Comma))
                    {
                        Advance();
                        SkipNewlines();
                        elems.Add(ParseAssignmentExpression());
                        SkipNewlines();
                    }
                    inner = new ArrayExpressionAst(elems, t.Location);
                }
                // Parenthesized pipelines: `($xs | ForEach-Object { $_ * 2 })`. When the
                // first expression is followed by a pipeline/redirection/background suffix,
                // stitch the rest together until we reach the closing `)`.
                if (IsAt(TokenKind.Pipe) || IsAt(TokenKind.Redirection) || IsBackgroundSuffix())
                {
                    var pipeline = ParseExpressionPipelineOrRedirection(inner, t.Location);
                    SkipNewlines();
                    if (!IsAt(TokenKind.RParen))
                    {
                        if (IsAt(TokenKind.EndOfInput))
                            throw new PwshIncompleteInputException("Unterminated '(' — expected ')'.", t.Location);
                        throw new PwshParseException($"Expected ')', got {Current.Kind} '{Current.Text}'.", Current.Location);
                    }
                    Advance();
                    return new SubExpressionAst(new ScriptAst(new StatementAst[] { pipeline }, t.Location), t.Location);
                }
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
                var callLoc = Current.Location;
                return WrapPipelineAsSubExpression(ParseCommandPipeline(callLoc), callLoc);
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

        if (IsKeyword("begin") || IsKeyword("process") || IsKeyword("end") || IsKeyword("clean"))
        {
            while (!IsAt(TokenKind.RBrace) && !IsAt(TokenKind.EndOfInput))
            {
                if (!(IsKeyword("begin") || IsKeyword("process") || IsKeyword("end") || IsKeyword("clean")))
                    break;
                Advance();
                SkipNewlines();
                var block = ParseBraceBlock();
                foreach (var statement in block.Statements)
                    statements.Add(statement);
                SkipNewlinesAndSemicolons();
            }
        }

        while (!IsAt(TokenKind.RBrace) && !IsAt(TokenKind.EndOfInput))
        {
            var stmt = ParseStatement();
            statements.Add(stmt);
            if (IsAt(TokenKind.RBrace) || IsAt(TokenKind.EndOfInput)) break;
            if (!AllowsImplicitStatementSeparator(stmt) && !IsAt(TokenKind.NewLine) && !IsAt(TokenKind.Semicolon))
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
            elements.Add(ParseArrayElementExpression());
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

    /// <summary>
    /// Parse an expression in a context where pwsh allows "statement-as-expression" —
    /// hashtable values (<c>@{x = if (…) { … }}</c>), assignment RHS, command-argument
    /// expressions. The statement keywords <c>if</c>, <c>while</c>, <c>foreach</c>,
    /// <c>switch</c>, <c>try</c> fold into a <see cref="SubExpressionAst"/> that the
    /// interpreter evaluates to the last statement's value.
    /// </summary>
    private ExpressionAst ParseValueExpression() => ParseCommandCapableValueExpression(allowCommaList: true, allowPipeline: true);

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
            var value = ParseValueExpression();
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

    private bool IsCommandPipelineStart()
    {
        if (IsAt(TokenKind.Identifier))
        {
            if (!IsExpressionKeyword(Current.Text)) return true;
            if (Current.Text.Equals("foreach", StringComparison.OrdinalIgnoreCase)) return true;
        }
        if (IsAt(TokenKind.PlusEqual) || IsAt(TokenKind.MinusEqual) ||
            IsAt(TokenKind.StarEqual) || IsAt(TokenKind.SlashEqual) || IsAt(TokenKind.PercentEqual))
            return true;
        if (IsAt(TokenKind.Question)) return true;
        if (IsAt(TokenKind.Percent)) return true;
        if (IsNumericCommandStart()) return true;
        if (IsAt(TokenKind.Ampersand)) return true;
        if (IsAt(TokenKind.Dot) && IsDotSourceCandidate()) return true;
        if ((IsAt(TokenKind.Dot) || IsAt(TokenKind.Slash) || IsAt(TokenKind.Backslash)) && IsPathLikeCommand()) return true;
        return false;
    }

    private bool IsStatementLikeValueKeywordStart()
        => IsAt(TokenKind.Identifier) && Current.Text.ToLowerInvariant() is "if" or "while" or "do" or "for" or "foreach" or "switch" or "try";

    private ExpressionAst WrapPipelineAsSubExpression(StatementAst pipeline, SourceLocation loc)
        => new SubExpressionAst(new ScriptAst(new[] { pipeline }, loc), loc);

    private ExpressionAst ParseArrayElementExpression()
        => ParseCommandCapableValueExpression(allowCommaList: false, allowPipeline: true);

    private ExpressionAst ParseCommandCapableValueExpression(bool allowCommaList, bool allowPipeline)
    {
        var loc = Current.Location;
        if (IsStatementLikeValueKeywordStart())
        {
            var stmt = ParseStatement();
            return new SubExpressionAst(new ScriptAst(new[] { stmt }, loc), loc);
        }
        if (IsCommandPipelineStart())
            return WrapPipelineAsSubExpression(ParseCommandPipeline(loc), loc);

        var expr = ParseAssignmentExpression();
        if (allowCommaList && IsAt(TokenKind.Comma))
        {
            var elems = new List<ExpressionAst> { expr };
            while (IsAt(TokenKind.Comma))
            {
                Advance();
                SkipNewlines();
                elems.Add(ParseAssignmentExpression());
            }
            expr = new ArrayExpressionAst(elems, loc);
        }
        SkipNewlinesBeforePipelineOrRedirection();
        if (!allowPipeline || (!IsAt(TokenKind.Pipe) && !IsAt(TokenKind.Redirection) && !IsBackgroundSuffix())) return expr;

        return WrapPipelineAsSubExpression(ParseExpressionPipelineOrRedirection(expr, loc), loc);
    }

    private void SkipNewlinesBeforePipelineOrRedirection()
    {
        while (IsAt(TokenKind.NewLine) &&
               (Peek(1).Kind == TokenKind.Pipe || Peek(1).Kind == TokenKind.Redirection))
        {
            Advance();
        }
    }

    private PipelineAst ParseExpressionPipelineOrRedirection(ExpressionAst expr, SourceLocation loc)
    {
        SkipNewlinesBeforePipelineOrRedirection();
        AstNode firstStage = expr;
        if (IsAt(TokenKind.Redirection))
        {
            var elements = new List<CommandElementAst> { new CommandArgumentAst(expr, expr.Location) };
            while (IsAt(TokenKind.Redirection))
            {
                elements.Add(ParseCommandRedirection());
                SkipNewlinesBeforePipelineOrRedirection();
            }

            firstStage = new CommandAst("Write-Output", elements, loc);
        }

        var stages = new List<AstNode> { firstStage };
        SkipNewlinesBeforePipelineOrRedirection();
        while (IsAt(TokenKind.Pipe))
        {
            Advance();
            SkipNewlines();
            stages.Add(ParsePipelineStage());
            SkipNewlinesBeforePipelineOrRedirection();
        }

        var pipeline = new PipelineAst(stages, loc);
        TryConsumeBackgroundSuffix();
        return pipeline;
    }

    private CommandAst ParseCommandStage()
    {
        var loc = Current.Location;
        if (IsAt(TokenKind.Dot) && IsDotSourceCandidate())
            return ParseDotSourceCommand(loc);
        if (IsAt(TokenKind.Ampersand))
            return ParseCallOperatorCommand(loc);
        if ((IsAt(TokenKind.Dot) || IsAt(TokenKind.Slash) || IsAt(TokenKind.Backslash)) && IsPathLikeCommand())
            return ParsePathCommand(loc);
        return ParseCommand();
    }

    private CommandAst ParseCommand()
    {
        var nameLoc = Current.Location;
        var name = GetLiteralText(ParseBareWord());
        var elements = ParseCommandElements();
        return new CommandAst(name, elements, nameLoc);
    }

    private bool IsCommandElementStart()
    {
        if (IsAt(TokenKind.EndOfInput) || IsAt(TokenKind.NewLine) ||
            IsAt(TokenKind.Semicolon) || IsAt(TokenKind.Pipe) ||
            IsAt(TokenKind.RParen) || IsAt(TokenKind.RBrace) || IsAt(TokenKind.RBracket))
            return false;
        if (IsPipelineChainOperator() || IsBackgroundSuffix())
            return false;
        return true;
    }

    private ExpressionAst ParseCommandArgument()
    {
        var t = Current;
        ExpressionAst first;
        switch (t.Kind)
        {
            case TokenKind.Variable:
            case TokenKind.Number:
            case TokenKind.String:
            case TokenKind.DollarLParen:
                if (HasAdjacentCommandTextContinuation())
                {
                    first = ParseExpandableCommandTextArgument();
                    break;
                }
                first = ParsePostfix();
                break;

            case TokenKind.AtLParen:
            case TokenKind.AtLBrace:
            case TokenKind.LBrace:
                first = ParsePostfix();
                break;

            case TokenKind.LParen:
                first = ParsePostfixContinuation(ParseGroupedCommandArgument());
                break;

            case TokenKind.LBracket when LooksLikeTypeLiteral():
                first = ParsePostfix();
                break;

            default:
                first = ParseExpandableCommandTextArgument();
                break;
        }

        // Command-mode comma-array: `Write-Output 1,2,3` is a single argument whose value
        // is @(1,2,3). Collect adjacent comma-separated expressions into an ArrayExpression.
        if (IsAt(TokenKind.Comma))
        {
            var elems = new List<ExpressionAst> { first };
            while (IsAt(TokenKind.Comma))
            {
                Advance();
                SkipNewlines();
                elems.Add(Current.Kind switch
                {
                    TokenKind.Variable or TokenKind.Number or TokenKind.String or
                    TokenKind.AtLParen or TokenKind.AtLBrace or
                    TokenKind.LBrace => ParsePostfix(),
                    TokenKind.DollarLParen when !HasAdjacentCommandTextContinuation() => ParsePostfix(),
                    TokenKind.LParen => ParsePostfixContinuation(ParseGroupedCommandArgument()),
                    TokenKind.LBracket when LooksLikeTypeLiteral() => ParsePostfix(),
                    _ => ParseExpandableCommandTextArgument(),
                });
            }
            return new ArrayExpressionAst(elems, t.Location);
        }
        return first;
    }

    private ExpressionAst ParseGroupedCommandArgument()
    {
        var start = Consume(TokenKind.LParen).Location;
        SkipNewlines();
        if (IsAt(TokenKind.Identifier) || IsCommandPipelineStart())
        {
            var pipelineStmt = ParseCommandPipeline(start);
            SkipNewlines();
            if (!IsAt(TokenKind.RParen))
            {
                if (IsAt(TokenKind.EndOfInput))
                    throw new PwshIncompleteInputException("Unterminated '(' — expected ')'.", start);
                throw new PwshParseException($"Expected ')', got {Current.Kind} '{Current.Text}'.", Current.Location);
            }
            Advance();
            return new SubExpressionAst(new ScriptAst(new StatementAst[] { pipelineStmt }, start), start);
        }

        var inner = ParseAssignmentExpression();
        SkipNewlines();
        if (IsAt(TokenKind.Comma))
        {
            var elems = new List<ExpressionAst> { inner };
            while (IsAt(TokenKind.Comma))
            {
                Advance();
                SkipNewlines();
                elems.Add(ParseAssignmentExpression());
                SkipNewlines();
            }
            inner = new ArrayExpressionAst(elems, start);
        }
        if (IsAt(TokenKind.Pipe) || IsAt(TokenKind.Redirection) || IsBackgroundSuffix())
        {
            var pipeline = ParseExpressionPipelineOrRedirection(inner, start);
            SkipNewlines();
            if (!IsAt(TokenKind.RParen))
            {
                if (IsAt(TokenKind.EndOfInput))
                    throw new PwshIncompleteInputException("Unterminated '(' — expected ')'.", start);
                throw new PwshParseException($"Expected ')', got {Current.Kind} '{Current.Text}'.", Current.Location);
            }
            Advance();
            return new SubExpressionAst(new ScriptAst(new StatementAst[] { pipeline }, start), start);
        }
        if (!IsAt(TokenKind.RParen))
        {
            if (IsAt(TokenKind.EndOfInput))
                throw new PwshIncompleteInputException("Unterminated '(' — expected ')'.", start);
            throw new PwshParseException($"Expected ')', got {Current.Kind} '{Current.Text}'.", Current.Location);
        }
        Advance();
        return new ParenExpressionAst(inner, start);
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
               !IsAt(TokenKind.Comma) &&
               !IsAt(TokenKind.Pipe) &&
               !IsAt(TokenKind.Redirection) &&
               !IsAt(TokenKind.LParen) &&
               !IsAt(TokenKind.LBrace) &&
               !IsAt(TokenKind.RParen) &&
               !IsAt(TokenKind.RBrace))
        {
            // First token: always consume. Subsequent: only if adjacent (no whitespace).
            if (sb.Length > 0 && Current.Location.Offset != endOffset)
                break;
            // In bare-word position, accept an adjacent `[]` pair as a suffix — pwsh treats
            // `byte[]` as the type name `System.Byte[]` when passed as a command argument.
            // The same command-mode rule also accepts generic-ish suffixes like
            // `List[string]` or drive globs like `function:[e-z]:`.
            if (IsAt(TokenKind.RBracket)) break;
            if (IsAt(TokenKind.LBracket))
            {
                var startCursor = _cursor;
                int depth = 0;
                do
                {
                    if (IsAt(TokenKind.LBracket)) depth++;
                    else if (IsAt(TokenKind.RBracket)) depth--;
                    Advance();
                }
                while (depth > 0 && !IsAt(TokenKind.EndOfInput));

                if (depth > 0)
                    throw new PwshIncompleteInputException("Unterminated bracketed bare word.", start);

                sb.Append(ReconstructTokenSource(startCursor, _cursor));
                var endToken = _tokens[_cursor - 1];
                endOffset = endToken.Location.Offset + endToken.Location.Length;
                continue;
            }
            sb.Append(GetCommandTextTokenText(Current));
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

    private bool HasAdjacentCommandTextContinuation()
    {
        if (_cursor + 1 >= _tokens.Count)
            return false;

        var next = Peek(1);
        return next.Kind switch
        {
            TokenKind.EndOfInput or TokenKind.NewLine or TokenKind.Semicolon or TokenKind.Comma or TokenKind.Pipe or
            TokenKind.Redirection or TokenKind.RParen or TokenKind.RBrace or TokenKind.RBracket or
            TokenKind.Dot or TokenKind.ColonColon or TokenKind.LBracket => false,
            _ => Current.Location.Offset + Current.Location.Length == next.Location.Offset,
        };
    }

    private ExpressionAst ParseExpandableCommandTextArgument()
    {
        var start = Current.Location;
        var parts = new List<StringPart>();
        var endOffset = start.Offset;

        while (!IsAt(TokenKind.EndOfInput) &&
               !IsAt(TokenKind.NewLine) &&
               !IsAt(TokenKind.Semicolon) &&
               !IsAt(TokenKind.Comma) &&
               !IsAt(TokenKind.Pipe) &&
               !IsAt(TokenKind.Redirection) &&
               !IsAt(TokenKind.LParen) &&
               !IsAt(TokenKind.LBrace) &&
               !IsAt(TokenKind.RParen) &&
               !IsAt(TokenKind.RBrace))
        {
            if (parts.Count > 0 && Current.Location.Offset != endOffset)
                break;
            if (IsAt(TokenKind.RBracket))
                break;
            if (IsAt(TokenKind.LBracket) && Peek(1).Kind != TokenKind.RBracket)
            {
                var startCursor = _cursor;
                int depth = 0;
                do
                {
                    if (IsAt(TokenKind.LBracket)) depth++;
                    else if (IsAt(TokenKind.RBracket)) depth--;
                    Advance();
                }
                while (depth > 0 && !IsAt(TokenKind.EndOfInput));

                if (depth > 0)
                    throw new PwshIncompleteInputException("Unterminated bracketed command argument.", start);

                parts.Add(new LiteralPart(ReconstructTokenSource(startCursor, _cursor)));
                var endToken = _tokens[_cursor - 1];
                endOffset = endToken.Location.Offset + endToken.Location.Length;
                continue;
            }

            if (IsAt(TokenKind.LBracket) && Peek(1).Kind == TokenKind.RBracket)
            {
                parts.Add(new LiteralPart("[]"));
                endOffset = Peek(1).Location.Offset + Peek(1).Location.Length;
                Advance();
                Advance();
                continue;
            }

            switch (Current.Kind)
            {
                case TokenKind.String:
                {
                    var token = Advance();
                    foreach (var part in (IReadOnlyList<StringPart>)token.Value!)
                    {
                        parts.Add(part);
                    }
                    endOffset = token.Location.Offset + token.Location.Length;
                    continue;
                }
                case TokenKind.Variable:
                {
                    if (HasAdjacentPostfixTextContinuation())
                    {
                        var startCursor = _cursor;
                        var origin = Current.Location;
                        _ = ParsePostfix();
                        parts.Add(new ExpressionPart(ReconstructTokenSource(startCursor, _cursor), origin));
                        var endToken = _tokens[_cursor - 1];
                        endOffset = endToken.Location.Offset + endToken.Location.Length;
                        continue;
                    }

                    var token = Advance();
                    var (scope, name) = ((string? Scope, string Name))token.Value!;
                    parts.Add(new VariablePart(scope, name));
                    endOffset = token.Location.Offset + token.Location.Length;
                    continue;
                }
                case TokenKind.DollarLParen:
                {
                    var startCursor = _cursor;
                    var origin = Current.Location;
                    _ = ParsePrimary();
                    parts.Add(new ExpressionPart(ReconstructTokenSource(startCursor + 1, _cursor - 1), origin));
                    var endToken = _tokens[_cursor - 1];
                    endOffset = endToken.Location.Offset + endToken.Location.Length;
                    continue;
                }
                default:
                {
                    var token = Advance();
                    parts.Add(new LiteralPart(GetCommandTextTokenText(token)));
                    endOffset = token.Location.Offset + token.Location.Length;
                    continue;
                }
            }
        }

        if (parts.Count == 0)
            throw new PwshParseException($"Unexpected token {Current.Kind} '{Current.Text}' in command argument.", Current.Location);

        var loc = new SourceLocation(start.Line, start.Column, start.Offset, endOffset - start.Offset);
        var isLiteralOnly = parts.All(static part => part is LiteralPart);
        return new StringLiteralAst(parts, IsSingleQuoted: isLiteralOnly, loc);
    }

    private bool HasAdjacentPostfixTextContinuation()
    {
        if (_cursor + 1 >= _tokens.Count)
            return false;

        var next = Peek(1);
        if (Current.Location.Offset + Current.Location.Length != next.Location.Offset)
            return false;

        return next.Kind is TokenKind.Dot or TokenKind.ColonColon or TokenKind.LBracket
            || (next.Kind == TokenKind.Question &&
                Peek(2).Kind is TokenKind.Dot or TokenKind.LBracket &&
                next.Location.Offset + next.Location.Length == Peek(2).Location.Offset);
    }

    private string ReconstructTokenSource(int startInclusive, int endExclusive)
    {
        var sb = new System.Text.StringBuilder();
        Token? previous = null;
        for (int i = startInclusive; i < endExclusive; i++)
        {
            var token = _tokens[i];
            if (token.Kind == TokenKind.EndOfInput)
                break;
            if (token.Kind == TokenKind.NewLine)
            {
                sb.Append('\n');
                previous = token;
                continue;
            }

            if (previous is Token prev &&
                prev.Kind != TokenKind.NewLine &&
                prev.Location.Offset + prev.Location.Length != token.Location.Offset)
            {
                sb.Append(' ');
            }

            sb.Append(token.Text);
            previous = token;
        }

        return sb.ToString();
    }

    private static string GetCommandTextTokenText(Token token)
        => token.Value as string ?? token.Text;

    private List<CommandElementAst> ParseCommandElements(IEnumerable<CommandElementAst>? seed = null)
    {
        var elements = seed?.ToList() ?? new List<CommandElementAst>();

        while (true)
        {
            while (IsAt(TokenKind.NewLine) && IsCommandElementContinuationAfterNewline())
                Advance();

            if (!IsCommandElementStart())
                break;

            if (TryParseCommandParameter(out var parameter))
            {
                elements.Add(parameter);
                if (IsAt(TokenKind.Colon) && CurrentIsAdjacentToPreviousToken())
                {
                    Advance();
                    if (IsCommandElementStart())
                    {
                        var attachedLoc = Current.Location;
                        elements.Add(new CommandArgumentAst(ParseCommandArgument(), attachedLoc));
                    }
                }
                continue;
            }
            if (IsAt(TokenKind.SplatVariable))
            {
                var tok = Advance();
                var (scope, name) = ((string? Scope, string Name))tok.Value!;
                elements.Add(new CommandSplatAst(new VariableAst(scope, name, tok.Location), tok.Location));
                continue;
            }
            if (IsAt(TokenKind.Redirection))
            {
                elements.Add(ParseCommandRedirection());
                continue;
            }

            var argLoc = Current.Location;
            var expr = ParseCommandArgument();
            elements.Add(new CommandArgumentAst(expr, argLoc));
        }

        return elements;
    }

    private bool IsCommandElementContinuationAfterNewline()
    {
        var next = Peek(1);
        if (next.Kind == TokenKind.EndOfInput)
            return false;

        if (next.Kind == TokenKind.Redirection || next.Kind == TokenKind.SplatVariable)
            return true;

        return next.Kind == TokenKind.Minus &&
               Peek(2).Kind == TokenKind.Identifier &&
               next.Location.Offset + next.Location.Length == Peek(2).Location.Offset;
    }

    private bool TryParseCommandParameter(out CommandParameterAst parameter)
    {
        if (IsAt(TokenKind.Minus)
            && Peek(1).Kind == TokenKind.Identifier
            && Current.Location.Offset + Current.Location.Length == Peek(1).Location.Offset)
        {
            var minusLoc = Current.Location;
            Advance();
            var idToken = Advance();
            parameter = new CommandParameterAst(idToken.Text, minusLoc);
            return true;
        }

        parameter = null!;
        return false;
    }

    private CommandRedirectionAst ParseCommandRedirection()
    {
        var token = Consume(TokenKind.Redirection);
        var spec = (RedirectionTokenData)token.Value!;
        ExpressionAst? target = null;
        if (spec.MergeToStream is null)
        {
            if (!IsCommandElementStart())
                throw new PwshIncompleteInputException("Expected a redirection target.", token.Location);
            target = ParseCommandArgument();
        }
        return new CommandRedirectionAst(spec.FromStream, spec.Append, spec.MergeToStream, target, token.Location);
    }

    private CommandAst ParsePathCommand(SourceLocation loc)
    {
        var name = GetLiteralText(ParseBareWord());
        var elements = ParseCommandElements();
        return new CommandAst(name, elements, loc);
    }

    private CommandAst ParseCallOperatorCommand(SourceLocation loc)
    {
        Consume(TokenKind.Ampersand);
        var target = ParseCommandTargetArgument();
        var elements = ParseCommandElements(new[] { new CommandArgumentAst(target, loc) });
        return new CommandAst("&", elements, loc);
    }

    private CommandAst ParseDotSourceCommand(SourceLocation loc)
    {
        Consume(TokenKind.Dot);
        var elements = ParseCommandElements();
        return new CommandAst(".", elements, loc);
    }

    private ExpressionAst ParseCommandTargetArgument()
    {
        return Current.Kind switch
        {
            TokenKind.Variable or TokenKind.Number or TokenKind.String or
            TokenKind.LParen or TokenKind.AtLParen or TokenKind.AtLBrace or
            TokenKind.DollarLParen or TokenKind.LBrace => ParsePostfix(),
            TokenKind.LBracket when LooksLikeTypeLiteral() => ParsePostfix(),
            _ => ParseBareWord(),
        };
    }

    private static string GetLiteralText(ExpressionAst expression)
    {
        var literal = expression as StringLiteralAst
            ?? throw new InvalidOperationException("Expected a string-like literal.");
        return string.Concat(literal.Parts.Select(GetStringPartText));
    }

    private static string GetStringPartText(StringPart part) => part switch
    {
        LiteralPart lp => lp.Text,
        VariablePart vp => "$" + (vp.Scope is null ? vp.Name : vp.Scope + ":" + vp.Name),
        ExpressionPart ep => "$(" + ep.Source + ")",
        _ => throw new InvalidOperationException($"Unsupported string part {part.GetType().Name}."),
    };

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

    private bool IsIdentifierValue(string text)
        => IsAt(TokenKind.Identifier) && Current.Text.Equals(text, StringComparison.OrdinalIgnoreCase);

    private bool TryParseStatementLabel(out string label)
    {
        if (IsAt(TokenKind.Colon) &&
            Peek(1).Kind == TokenKind.Identifier &&
            Current.Location.Offset + Current.Location.Length == Peek(1).Location.Offset)
        {
            Advance();
            label = Advance().Text;
            SkipNewlines();
            return true;
        }

        label = null!;
        return false;
    }

    private bool IsNumericCommandStart()
    {
        if (!IsAt(TokenKind.Number))
            return false;

        var next = Peek(1);
        var end = Current.Location.Offset + Current.Location.Length;
        return next.Location.Offset == end && next.Kind == TokenKind.Identifier;
    }

    private static bool IsIntrinsicCollectionMethodWithoutParens(string name)
        => name.Equals("Where", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ForEach", StringComparison.OrdinalIgnoreCase)
        || name.Equals("PSWhere", StringComparison.OrdinalIgnoreCase)
        || name.Equals("PSForEach", StringComparison.OrdinalIgnoreCase);

    private Token ConsumeKeyword(string keyword)
    {
        if (!IsKeyword(keyword))
            throw new PwshParseException($"Expected keyword '{keyword}'.", Current.Location);
        return Advance();
    }

    private bool IsDotSourceCandidate()
    {
        if (!IsAt(TokenKind.Dot)) return false;
        var next = Peek(1);
        if (next.Kind == TokenKind.LBrace) return true;
        if (Current.Location.Offset + Current.Location.Length == next.Location.Offset) return false;
        return true;
    }

    /// <summary>
    /// Parse a condition inside `if (...)`, `while (...)`, `do-while (...)`, `for (;cond;)`.
    /// If the first token is an Identifier that isn't an expression keyword, treat the
    /// whole paren as a command-pipeline sub-expression (matches real pwsh: `if (Test-Path
    /// /foo)` is valid). Pipelines with `|` stitch through, same as the parenthesized-
    /// pipeline path in primary expressions.
    /// </summary>
    private ExpressionAst ParseConditionExpression()
    {
        return ParseCommandCapableValueExpression(allowCommaList: false, allowPipeline: true);
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
            var cond = ParseConditionExpression();
            SkipNewlines();
            Consume(TokenKind.RParen);
            SkipNewlines();
            var body = ParseBraceBlock();
            branches.Add((cond, body));
        }

        ParseBranch();
        while (true)
        {
            // Look past any newlines WITHOUT committing — pwsh allows `} elseif` on the
            // same line or after a newline, but a newline followed by an unrelated
            // statement ends the if. If the next meaningful keyword isn't `elseif` /
            // `else`, rewind so ParseScript sees the newline as a statement separator.
            var save = _cursor;
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
                break;
            }
            _cursor = save;
            break;
        }
        return new IfStatementAst(branches, elseBody, start);
    }

    private WhileStatementAst ParseWhileStatement(string? label = null)
    {
        var start = ConsumeKeyword("while").Location;
        Consume(TokenKind.LParen);
        SkipNewlines();
        var cond = ParseConditionExpression();
        SkipNewlines();
        Consume(TokenKind.RParen);
        SkipNewlines();
        var body = ParseBraceBlock();
        return new WhileStatementAst(label, cond, body, start);
    }

    private DoWhileStatementAst ParseDoStatement(string? label = null)
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
        var cond = ParseConditionExpression();
        SkipNewlines();
        Consume(TokenKind.RParen);
        return new DoWhileStatementAst(label, body, cond, isUntil, start);
    }

    private ForStatementAst ParseForStatement(string? label = null)
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
        if (IsAt(TokenKind.Semicolon))
        {
            Consume(TokenKind.Semicolon);
            SkipNewlines();
            if (!IsAt(TokenKind.RParen))
            {
                update = ParseStatement();
            }
        }
        SkipNewlines();
        Consume(TokenKind.RParen);
        SkipNewlines();
        var body = ParseBraceBlock();
        return new ForStatementAst(label, init, cond, update, body, start);
    }

    private ForEachStatementAst ParseForEachStatement(string? label = null)
    {
        var start = ConsumeKeyword("foreach").Location;
        Consume(TokenKind.LParen);
        SkipNewlines();
        var varToken = Consume(TokenKind.Variable);
        var (scope, name) = ((string? Scope, string Name))varToken.Value!;
        if (!IsKeyword("in"))
            throw new PwshParseException("Expected 'in' in foreach statement.", Current.Location);
        Advance();
        SkipNewlines();
        var collection = ParseCommandCapableValueExpression(allowCommaList: true, allowPipeline: true);
        SkipNewlines();
        Consume(TokenKind.RParen);
        SkipNewlines();
        var body = ParseBraceBlock();
        return new ForEachStatementAst(label, scope, name, collection, body, start);
    }

    private SwitchStatementAst ParseSwitchStatement(string? label = null)
    {
        var start = ConsumeKeyword("switch").Location;
        bool isWildcard = false;
        while (IsAt(TokenKind.Minus)
            && Peek(1).Kind == TokenKind.Identifier
            && Current.Location.Offset + Current.Location.Length == Peek(1).Location.Offset)
        {
            Advance();
            var option = Advance().Text;
            if (string.Equals(option, "wildcard", StringComparison.OrdinalIgnoreCase))
                isWildcard = true;
            SkipNewlines();
        }
        ExpressionAst cond;
        if (IsAt(TokenKind.LParen))
        {
            Advance();
            SkipNewlines();
            cond = ParseCommandCapableValueExpression(allowCommaList: true, allowPipeline: true);
            SkipNewlines();
            Consume(TokenKind.RParen);
        }
        else
        {
            cond = ParseCommandCapableValueExpression(allowCommaList: true, allowPipeline: true);
        }
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
            var pattern = ParseSwitchPattern();
            SkipNewlines();
            var body = ParseBraceBlock();
            cases.Add((pattern, body));
            SkipNewlinesAndSemicolons();
        }
        if (IsAt(TokenKind.EndOfInput))
            throw new PwshIncompleteInputException("Unterminated switch statement.", start);
        Consume(TokenKind.RBrace);
        return new SwitchStatementAst(label, isWildcard, cond, cases, defaultBody, start);
    }

    private ExpressionAst ParseSwitchPattern()
    {
        if (IsAt(TokenKind.Identifier) || IsNumericCommandStart())
            return ParseBareWord();

        return ParseLogicalOr();
    }

    private BreakStatementAst ParseBreakStatement()
    {
        var start = ConsumeKeyword("break").Location;
        string? label = null;
        if ((IsAt(TokenKind.Identifier) || IsAt(TokenKind.Variable)) && !IsStatementTerminator())
        {
            label = Current.Text;
            Advance();
        }
        return new BreakStatementAst(label, start);
    }

    private ContinueStatementAst ParseContinueStatement()
    {
        var start = ConsumeKeyword("continue").Location;
        string? label = null;
        if ((IsAt(TokenKind.Identifier) || IsAt(TokenKind.Variable)) && !IsStatementTerminator())
        {
            label = Current.Text;
            Advance();
        }
        return new ContinueStatementAst(label, start);
    }

    /// <summary>
    /// Parse the value after `return` or `throw`. Real pwsh lets the right-hand side be a
    /// full command pipeline (<c>return Get-ChildItem | Sort-Object</c>), not just an
    /// expression. If the first token is an Identifier that isn't an expression keyword,
    /// treat the tail as a command-pipeline sub-expression.
    /// </summary>
    private ExpressionAst ParseReturnOrThrowValue(Errors.SourceLocation loc)
    {
        return ParseCommandCapableValueExpression(allowCommaList: true, allowPipeline: true);
    }

    private ReturnStatementAst ParseReturnStatement()
    {
        var start = ConsumeKeyword("return").Location;
        ExpressionAst? value = null;
        if (!IsStatementTerminator() && !IsAt(TokenKind.RBrace))
        {
            value = ParseReturnOrThrowValue(start);
        }
        return new ReturnStatementAst(value, start);
    }

    private ThrowStatementAst ParseThrowStatement()
    {
        var start = ConsumeKeyword("throw").Location;
        ExpressionAst? value = null;
        if (!IsStatementTerminator() && !IsAt(TokenKind.RBrace))
        {
            value = ParseReturnOrThrowValue(start);
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

        var catches = new List<CatchClauseAst>();
        ScriptAst? finallyBody = null;

        while (true)
        {
            var catchLookahead = _cursor;
            SkipNewlines();
            if (!IsKeyword("catch"))
            {
                _cursor = catchLookahead;
                break;
            }

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
        }

        var finallyLookahead = _cursor;
        SkipNewlines();
        if (IsKeyword("finally"))
        {
            Advance();
            SkipNewlines();
            finallyBody = ParseBraceBlock();
        }
        else
        {
            _cursor = finallyLookahead;
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
        var name = ParseFunctionName();
        SkipNewlines();

        // Real pwsh supports `function name (a, b) { body }` as a shorthand for the
        // param-block form. If we see a `(` before the `{`, parse inline parameters.
        var parameters = new List<ParameterAst>();
        bool parenParams = false;
        if (IsAt(TokenKind.LParen))
        {
            parenParams = true;
            Advance();
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
            SkipNewlines();
        }

        Consume(TokenKind.LBrace);
        SkipNewlinesAndSemicolons();

        // Optional param(...) block right after { — only if the paren-parameter form wasn't
        // already used on the function declaration line. Using both is a syntax error in
        // real pwsh; we quietly ignore the inner param(...) when the outer form wins.
        if (!parenParams && IsKeyword("param"))
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
        ScriptAst? cleanBlock = null;
        ScriptAst? simpleBody = null;

        // Pipeline-participating form: one or more of begin/process/end/clean labels at top level.
        if (IsKeyword("begin") || IsKeyword("process") || IsKeyword("end") || IsKeyword("clean"))
        {
            while (!IsAt(TokenKind.RBrace) && !IsAt(TokenKind.EndOfInput))
            {
                if (IsKeyword("begin")) { Advance(); SkipNewlines(); beginBlock = ParseBraceBlock(); }
                else if (IsKeyword("process")) { Advance(); SkipNewlines(); processBlock = ParseBraceBlock(); }
                else if (IsKeyword("end")) { Advance(); SkipNewlines(); endBlock = ParseBraceBlock(); }
                else if (IsKeyword("clean")) { Advance(); SkipNewlines(); cleanBlock = ParseBraceBlock(); }
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

        return new FunctionDefinitionAst(name, parameters, beginBlock, processBlock, endBlock, cleanBlock, simpleBody, start);
    }

    /// <summary>
    /// Top-level <c>param(...)</c> block at script start. Each declared parameter becomes
    /// an assignment to its name in the current scope, using its default expression when
    /// one is supplied. Attributes on the parameter (<c>[Parameter(Mandatory)]</c>,
    /// <c>[ValidateSet(...)]</c>, etc.) are accepted but not enforced in Phase 1.
    /// </summary>
    private StatementAst ParseTopLevelParamBlock()
    {
        var start = ConsumeKeyword("param").Location;
        SkipNewlines();
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
        // Represent the param block as a sequence of assignment statements so the
        // interpreter doesn't need a new AST node. A parameter without a default evaluates
        // to $null; a [switch]$Flag without a default becomes $false (matching real pwsh).
        var assignments = new List<StatementAst>();
        foreach (var p in parameters)
        {
            ExpressionAst rhs = p.DefaultValue
                ?? (IsSwitchType(p.TypeConstraint) ? (ExpressionAst)new BooleanLiteralAst(false, start) : new NullLiteralAst(start));
            var lhs = new VariableAst(null, p.Name, start);
            assignments.Add(new AssignmentStatementAst(lhs, AssignmentOp.Assign, rhs, start));
        }
        // Always emit a BlockStatementAst (even for 0 or 1 assignments) so the brace-block
        // parser can reliably detect "we just finished a param() block" via a type check
        // and relax its separator requirement. Matches real pwsh's `{ param($x) body }`.
        return new BlockStatementAst(assignments, start);
    }

    private static bool IsSwitchType(TypeLiteralAst? type)
    {
        if (type is null) return false;
        return string.Equals(type.TypeName, "switch", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type.TypeName, "System.Management.Automation.SwitchParameter", StringComparison.OrdinalIgnoreCase);
    }

    private ParameterAst ParseParameter()
    {
        var loc = Current.Location;
        TypeLiteralAst? type = null;
        while (true)
        {
            bool consumed = false;
            while (IsAttributeSpec())
            {
                ConsumeAttributeSpec();
                SkipNewlines();
                consumed = true;
            }
                if (IsAt(TokenKind.LBracket) && LooksLikeTypeLiteral())
                {
                    type = ParseTypeLiteral();
                    SkipNewlines();
                    consumed = true;
                }
            if (!consumed)
                break;
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
        if (IsAt(TokenKind.Colon))
        {
            Advance();
            SkipNewlines();
            ConsumeBaseTypeClause();
            SkipNewlines();
        }
        Consume(TokenKind.LBrace);
        SkipNewlinesAndSemicolons();

        var properties = new List<ClassPropertyAst>();
        var methods = new List<ClassMethodAst>();

        while (!IsAt(TokenKind.RBrace) && !IsAt(TokenKind.EndOfInput))
        {
            var memberLoc = Current.Location;
            TypeLiteralAst? typeLit = null;
            bool isStatic = false;

            while (true)
            {
                bool consumed = false;
                while (IsAttributeSpec())
                {
                    ConsumeAttributeSpec();
                    SkipNewlines();
                    consumed = true;
                }
                if (typeLit == null && IsAt(TokenKind.LBracket) && LooksLikeTypeLiteral())
                {
                    typeLit = ParseTypeLiteral();
                    SkipNewlines();
                    consumed = true;
                    continue;
                }
                if (IsIdentifierValue("static"))
                {
                    isStatic = true;
                    Advance();
                    SkipNewlines();
                    consumed = true;
                    continue;
                }
                if (IsIdentifierValue("hidden"))
                {
                    Advance();
                    SkipNewlines();
                    consumed = true;
                    continue;
                }
                if (!consumed)
                    break;
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
                properties.Add(new ClassPropertyAst(propName, typeLit, defaultVal, isStatic, memberLoc));
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
                if (IsAt(TokenKind.Colon))
                {
                    SkipConstructorInitializer();
                    SkipNewlines();
                }
                var body = ParseBraceBlock();
                bool isConstructor = methodName.Equals(name, StringComparison.OrdinalIgnoreCase);
                methods.Add(new ClassMethodAst(methodName, parameters, typeLit, body, isStatic, isConstructor, memberLoc));
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

    private void ConsumeBaseTypeClause()
    {
        _ = ParseBareTypeLiteral();
        SkipNewlines();
        while (IsAt(TokenKind.Comma))
        {
            Advance();
            SkipNewlines();
            _ = ParseBareTypeLiteral();
            SkipNewlines();
        }
    }

    private void SkipConstructorInitializer()
    {
        Consume(TokenKind.Colon);
        SkipNewlines();
        if (!IsAt(TokenKind.Identifier))
            throw new PwshParseException("Expected constructor initializer target.", Current.Location);
        Advance();
        SkipNewlines();
        Consume(TokenKind.LParen);
        SkipNewlines();
        if (!IsAt(TokenKind.RParen))
        {
            _ = ParseAssignmentExpression();
            SkipNewlines();
            while (IsAt(TokenKind.Comma))
            {
                Advance();
                SkipNewlines();
                _ = ParseAssignmentExpression();
                SkipNewlines();
            }
        }
        Consume(TokenKind.RParen);
    }

    private TypeLiteralAst ParseBareTypeLiteral()
    {
        var start = Current.Location;
        var typeName = ParseTypeName();
        var genericArgs = new List<TypeLiteralAst>();
        int arrayRank = 0;
        while (IsAt(TokenKind.LBracket))
        {
            if (TryConsumeArrayTypeSuffix())
            {
                arrayRank++;
                continue;
            }

            Advance();
            SkipNewlines();
            genericArgs.Add(ParseTypeLiteralInner());
            SkipNewlines();
            while (IsAt(TokenKind.Comma))
            {
                Advance();
                SkipNewlines();
                genericArgs.Add(ParseTypeLiteralInner());
                SkipNewlines();
            }
            Consume(TokenKind.RBracket);
        }

        return new TypeLiteralAst(typeName, genericArgs, arrayRank, start);
    }

    private EnumDefinitionAst ParseEnumDefinition()
    {
        var start = ConsumeKeyword("enum").Location;
        if (!IsAt(TokenKind.Identifier))
            throw new PwshParseException("Expected enum name.", Current.Location);
        var name = Advance().Text;
        SkipNewlines();
        TypeLiteralAst? underlyingType = null;
        if (IsAt(TokenKind.Colon))
        {
            Advance();
            SkipNewlines();
            underlyingType = ParseBareTypeLiteral();
            SkipNewlines();
        }
        Consume(TokenKind.LBrace);
        SkipNewlinesAndSemicolons();

        var members = new List<EnumMemberAst>();
        while (!IsAt(TokenKind.RBrace) && !IsAt(TokenKind.EndOfInput))
        {
            if (!IsAt(TokenKind.Identifier))
                throw new PwshParseException("Expected enum member name.", Current.Location);
            var memberLoc = Current.Location;
            var memberName = Advance().Text;
            ExpressionAst? value = null;
            SkipNewlines();
            if (IsAt(TokenKind.Equal))
            {
                Advance();
                SkipNewlines();
                value = ParseAssignmentExpression();
            }
            members.Add(new EnumMemberAst(memberName, value, memberLoc));
            SkipNewlinesAndSemicolons();
            if (IsAt(TokenKind.Comma)) { Advance(); SkipNewlinesAndSemicolons(); }
        }
        if (IsAt(TokenKind.EndOfInput))
            throw new PwshIncompleteInputException("Unterminated enum definition.", start);
        Consume(TokenKind.RBrace);
        return new EnumDefinitionAst(name, underlyingType, members, start);
    }

    private ScriptAst ParseBraceBlock()
    {
        var start = Current.Location;
        Consume(TokenKind.LBrace);
        SkipNewlinesAndSemicolons();
        var statements = new List<StatementAst>();
        while (!IsAt(TokenKind.RBrace) && !IsAt(TokenKind.EndOfInput))
        {
            var stmt = ParseStatement();
            statements.Add(stmt);
            if (IsAt(TokenKind.RBrace) || IsAt(TokenKind.EndOfInput)) break;
            if (!AllowsImplicitStatementSeparator(stmt) && !IsAt(TokenKind.NewLine) && !IsAt(TokenKind.Semicolon))
                throw new PwshParseException(
                    $"Expected statement separator, got {Current.Kind} '{Current.Text}'.", Current.Location);
            SkipNewlinesAndSemicolons();
        }
        if (IsAt(TokenKind.EndOfInput))
            throw new PwshIncompleteInputException("Unterminated brace block.", start);
        Consume(TokenKind.RBrace);
        return new ScriptAst(statements, start);
    }

    /// <summary>
    /// Mark each BlockStatementAst emitted by a top-level param block with a flag so that
    /// ParseBraceBlock can relax its statement-separator requirement immediately after
    /// one. In Phase 1 we use a sentinel empty location to avoid threading a second field
    /// through the AST record.
    /// </summary>
    private static bool IsFromParamBlock(StatementAst stmt)
    {
        // BlockStatementAst is only emitted from ParseTopLevelParamBlock today, and param
        // blocks without defaults produce a single AssignmentStatementAst — we recognise
        // that shape too (assignment of a VariableAst to a null-or-boolean literal at a
        // param-block's own source location).
        return stmt is BlockStatementAst;
    }

    private static bool AllowsImplicitStatementSeparator(StatementAst stmt)
        => stmt is BlockStatementAst or IfStatementAst or WhileStatementAst or DoWhileStatementAst
            or ForStatementAst or ForEachStatementAst or SwitchStatementAst or TryStatementAst
            or FunctionDefinitionAst or ClassDefinitionAst or EnumDefinitionAst;

    private bool IsPathLikeCommand()
    {
        // First token is Dot, Slash, or Backslash; we expect it to be adjacent to something that makes it
        // a path (e.g. `./foo`, `/usr/...`). Not adjacent → leave for other paths.
        if (!(IsAt(TokenKind.Dot) || IsAt(TokenKind.Slash) || IsAt(TokenKind.Backslash))) return false;
        var next = Peek(1);
        return Current.Location.Offset + Current.Location.Length == next.Location.Offset;
    }

    private StatementAst ParsePathCommandStatement(SourceLocation loc)
    {
        var cmd = ParsePathCommand(loc);
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
        var cmd = ParseCallOperatorCommand(loc);
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

    private StatementAst ParseDotSourceStatement(SourceLocation loc)
    {
        var cmd = ParseDotSourceCommand(loc);
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

    private bool IsPipelineChainOperator()
        => (IsAt(TokenKind.OpAnd) || IsAt(TokenKind.OpOr)) &&
           (Current.Text == "&&" || Current.Text == "||");

    private bool IsBackgroundSuffix()
        => IsAt(TokenKind.Ampersand);

    private bool TryConsumeBackgroundSuffix()
    {
        if (!IsBackgroundSuffix())
            return false;

        Advance();
        return true;
    }

    private StatementAst ParseSimpleCommandPipeline(SourceLocation loc)
    {
        var first = ParseCommandStage();
        if (!IsAt(TokenKind.Pipe))
        {
            var single = new PipelineAst(new AstNode[] { first }, loc);
            TryConsumeBackgroundSuffix();
            return single;
        }

        var stages = new List<AstNode> { first };
        while (IsAt(TokenKind.Pipe))
        {
            Advance();
            SkipNewlines();
            stages.Add(ParsePipelineStage());
        }

        var pipeline = new PipelineAst(stages, loc);
        TryConsumeBackgroundSuffix();
        return pipeline;
    }

    private ExpressionAst WrapStatementAsSubExpression(StatementAst statement, SourceLocation loc)
        => new SubExpressionAst(new ScriptAst(new[] { statement }, loc), loc);

    private ExpressionAst ParsePipelineChainOperand()
    {
        var loc = Current.Location;
        if (IsCommandPipelineStart())
            return WrapStatementAsSubExpression(ParseSimpleCommandPipeline(loc), loc);

        return ParseLogicalOr();
    }

    private string ParseFunctionName()
    {
        var sb = new System.Text.StringBuilder();
        int endOffset = Current.Location.Offset;

        while (!IsAt(TokenKind.EndOfInput) &&
               !IsAt(TokenKind.NewLine) &&
               !IsAt(TokenKind.Semicolon) &&
               !IsAt(TokenKind.LParen) &&
               !IsAt(TokenKind.LBrace))
        {
            if (sb.Length > 0 && Current.Location.Offset != endOffset)
                break;

            sb.Append(GetCommandTextTokenText(Current));
            endOffset = Current.Location.Offset + Current.Location.Length;
            Advance();
        }

        if (sb.Length == 0)
            throw new PwshParseException("Expected function name.", Current.Location);

        return sb.ToString();
    }
}
