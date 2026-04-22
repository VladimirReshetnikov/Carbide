using CarbideCmd.Errors;
using CarbideCmd.Lexer;

namespace CarbideCmd.Parser;

/// <summary>
/// Line-oriented recursive-descent parser for the cmd subset. Each top-level line becomes a
/// <see cref="LineAst"/>; chains of commands within a line are captured as
/// <see cref="CommandChainAst"/>. Statement-shaped constructs (IF, GOTO, EXIT, SETLOCAL) are
/// recognized by matching their keyword as the first word of a simple command.
/// </summary>
public sealed class CmdParser
{
    private readonly List<Token> _tokens;
    private int _pos;

    public CmdParser(List<Token> tokens) { _tokens = tokens; }

    public static ScriptAst ParseString(string source)
    {
        var tokens = Lexer.CmdLexer.Tokenize(source);
        return new CmdParser(tokens).ParseScript();
    }

    public ScriptAst ParseScript()
    {
        var lines = new List<LineAst>();
        while (!IsAtEnd())
        {
            SkipBlankLines();
            if (IsAtEnd()) break;

            if (Peek().Kind == TokenKind.Label)
            {
                lines.Add(new LabelLineAst(Consume().Text));
                ExpectEndOfLine();
                continue;
            }

            bool echoSuppressed = false;
            if (Peek().Kind == TokenKind.At)
            {
                echoSuppressed = true;
                Consume();
            }

            if (Peek().Kind == TokenKind.Newline || IsAtEnd()) { ConsumeIfNewline(); continue; }

            var chain = ParseChain();
            lines.Add(new CommandLineAst(chain, echoSuppressed));
            ExpectEndOfLine();
        }
        return new ScriptAst(lines);
    }

    private CommandChainAst ParseChain()
    {
        var items = new List<ChainedStatementAst>();
        var first = ParseStatement();
        items.Add(new ChainedStatementAst(ChainOperator.None, first));

        while (!IsAtEnd() && IsChainOp(Peek().Kind))
        {
            var op = Consume().Kind switch
            {
                TokenKind.Amp => ChainOperator.Sequence,
                TokenKind.AmpAmp => ChainOperator.And,
                TokenKind.PipePipe => ChainOperator.Or,
                TokenKind.Pipe => ChainOperator.Pipe,
                _ => throw new CmdParseException("Unexpected chain operator.", Peek().Line, Peek().Column),
            };
            var next = ParseStatement();
            items.Add(new ChainedStatementAst(op, next));
        }
        return new CommandChainAst(items);
    }

    private StatementAst ParseStatement()
    {
        if (Peek().Kind != TokenKind.Word)
            throw new CmdParseException("Expected a command.", Peek().Line, Peek().Column);
        var verb = Peek().Text;

        if (verb.Equals("IF", StringComparison.OrdinalIgnoreCase)) return ParseIf();
        if (verb.Equals("GOTO", StringComparison.OrdinalIgnoreCase)) return ParseGoto();
        if (verb.Equals("EXIT", StringComparison.OrdinalIgnoreCase)) return ParseExit();
        if (verb.Equals("SETLOCAL", StringComparison.OrdinalIgnoreCase)) return ParseSetLocal();
        if (verb.Equals("ENDLOCAL", StringComparison.OrdinalIgnoreCase)) { Consume(); return new EndLocalStatementAst(); }
        if (verb.Equals("FOR", StringComparison.OrdinalIgnoreCase)) return ParseFor();
        if (verb.Equals("CALL", StringComparison.OrdinalIgnoreCase)) return ParseCall();

        return ParseSimpleCommand();
    }

    private StatementAst ParseSimpleCommand()
    {
        var name = Consume().Text;
        var args = new List<string>();
        var redirs = new List<RedirectionAst>();

        while (!IsAtEnd() && !IsTerminator(Peek().Kind))
        {
            switch (Peek().Kind)
            {
                case TokenKind.Word:
                    args.Add(Consume().Text);
                    break;
                case TokenKind.RedirOut:
                    Consume();
                    redirs.Add(new StdoutRedirection(ExpectWord("target file path"), Append: false));
                    break;
                case TokenKind.RedirAppend:
                    Consume();
                    redirs.Add(new StdoutRedirection(ExpectWord("target file path"), Append: true));
                    break;
                case TokenKind.RedirIn:
                    Consume();
                    redirs.Add(new StdinRedirection(ExpectWord("source file path")));
                    break;
                case TokenKind.RedirErr:
                    Consume();
                    redirs.Add(new StderrRedirection(ExpectWord("stderr target file path")));
                    break;
                case TokenKind.RedirMerge:
                    Consume();
                    redirs.Add(new StderrMergeRedirection());
                    break;
                default:
                    return new SimpleCommandAst(name, args, redirs);
            }
        }
        return new SimpleCommandAst(name, args, redirs);
    }

    private IfStatementAst ParseIf()
    {
        Consume(); // IF
        bool negated = false;
        bool caseInsensitive = false;

        while (Peek().Kind == TokenKind.Word)
        {
            var t = Peek().Text;
            if (t.Equals("/I", StringComparison.OrdinalIgnoreCase)) { caseInsensitive = true; Consume(); continue; }
            if (t.Equals("NOT", StringComparison.OrdinalIgnoreCase)) { negated = true; Consume(); continue; }
            break;
        }

        IfConditionAst condition;
        var nextText = Peek().Text;
        if (Peek().Kind == TokenKind.Word && nextText.Equals("EXIST", StringComparison.OrdinalIgnoreCase))
        {
            Consume();
            condition = new IfExistCondition(ExpectWord("path for EXIST"));
        }
        else if (Peek().Kind == TokenKind.Word && nextText.Equals("DEFINED", StringComparison.OrdinalIgnoreCase))
        {
            Consume();
            condition = new IfDefinedCondition(ExpectWord("variable name for DEFINED"));
        }
        else if (Peek().Kind == TokenKind.Word && nextText.Equals("ERRORLEVEL", StringComparison.OrdinalIgnoreCase))
        {
            Consume();
            var threshold = ExpectWord("ERRORLEVEL threshold");
            if (!int.TryParse(threshold, out var th))
                throw new CmdParseException($"ERRORLEVEL requires an integer, got '{threshold}'.", Peek().Line, Peek().Column);
            condition = new IfErrorLevelCondition(th);
        }
        else
        {
            var left = ExpectWord("left-hand side of IF comparison");
            if (Peek().Kind != TokenKind.Word || Peek().Text != "==")
                throw new CmdParseException("IF requires '==' after the left operand (only == is supported in Phase 1).", Peek().Line, Peek().Column);
            Consume();
            var right = ExpectWord("right-hand side of IF comparison");
            condition = new IfEqualsCondition(left, right);
        }

        var body = ParseIfBody();
        StatementAst? elseBody = null;
        if (Peek().Kind == TokenKind.Word && Peek().Text.Equals("ELSE", StringComparison.OrdinalIgnoreCase))
        {
            Consume();
            elseBody = ParseIfBody();
        }

        return new IfStatementAst(condition, negated, caseInsensitive, body, elseBody);
    }

    private StatementAst ParseIfBody()
    {
        if (Peek().Kind == TokenKind.LParen)
        {
            Consume();
            var chain = ParseChain();
            if (Peek().Kind != TokenKind.RParen)
                throw new CmdParseException("Expected ')' to close IF body.", Peek().Line, Peek().Column);
            Consume();
            if (chain.Items.Count == 1) return chain.Items[0].Statement;
            return new ChainStatementWrapperAst(chain);
        }
        return ParseStatement();
    }

    private GotoStatementAst ParseGoto()
    {
        Consume();
        var label = ExpectWord("label name after GOTO");
        if (label.StartsWith(':')) label = label[1..];
        return new GotoStatementAst(label);
    }

    private ExitStatementAst ParseExit()
    {
        Consume();
        bool branch = false;
        int code = 0;
        while (Peek().Kind == TokenKind.Word)
        {
            var t = Peek().Text;
            if (t.Equals("/B", StringComparison.OrdinalIgnoreCase)) { branch = true; Consume(); continue; }
            if (int.TryParse(t, out var c)) { code = c; Consume(); continue; }
            break;
        }
        return new ExitStatementAst(code, branch);
    }

    private SetLocalStatementAst ParseSetLocal()
    {
        Consume();
        var options = new List<string>();
        while (Peek().Kind == TokenKind.Word)
        {
            options.Add(Consume().Text);
        }
        return new SetLocalStatementAst(options);
    }

    private StatementAst ParseFor()
    {
        Consume(); // FOR
        bool numeric = false;
        while (Peek().Kind == TokenKind.Word)
        {
            var s = Peek().Text;
            if (s.Equals("/L", StringComparison.OrdinalIgnoreCase)) { numeric = true; Consume(); continue; }
            if (s.Equals("/F", StringComparison.OrdinalIgnoreCase))
            {
                // Phase-2 stretch; Phase-1b falls back to treating `/F` like `/ D` — parse-only
                // accept, reject at eval time if a real script tries to exercise it.
                Consume();
                continue;
            }
            break;
        }
        var varWord = ExpectWord("loop variable (e.g. %X)");
        if (!varWord.StartsWith("%") || varWord.Length < 2)
            throw new CmdParseException($"FOR loop variable must start with '%' (got '{varWord}').", Peek().Line, Peek().Column);
        var varName = varWord.Substring(1);

        var inWord = ExpectWord("'IN' keyword");
        if (!inWord.Equals("IN", StringComparison.OrdinalIgnoreCase))
            throw new CmdParseException($"Expected 'IN' in FOR, got '{inWord}'.", Peek().Line, Peek().Column);

        if (Peek().Kind != TokenKind.LParen)
            throw new CmdParseException("Expected '(' after IN.", Peek().Line, Peek().Column);
        Consume();

        var setItems = new List<string>();
        while (Peek().Kind == TokenKind.Word)
        {
            var w = Consume().Text;
            // Split on commas so `(1, 2, 3)` tokenizes as "1,", "2,", "3" and produces three items.
            foreach (var piece in w.Split(','))
            {
                var trimmed = piece.Trim();
                if (trimmed.Length > 0) setItems.Add(trimmed);
            }
        }

        if (Peek().Kind != TokenKind.RParen)
            throw new CmdParseException("Expected ')' to close FOR set.", Peek().Line, Peek().Column);
        Consume();

        var doWord = ExpectWord("'DO' keyword");
        if (!doWord.Equals("DO", StringComparison.OrdinalIgnoreCase))
            throw new CmdParseException($"Expected 'DO' in FOR, got '{doWord}'.", Peek().Line, Peek().Column);

        var body = ParseForBody();

        if (numeric)
        {
            if (setItems.Count != 3)
                throw new CmdParseException($"FOR /L needs exactly 3 items in (start, step, end); got {setItems.Count}.", Peek().Line, Peek().Column);
            return new ForLStatementAst(varName, setItems[0], setItems[1], setItems[2], body);
        }
        return new ForInStatementAst(varName, setItems, body);
    }

    private StatementAst ParseForBody()
    {
        if (Peek().Kind == TokenKind.LParen)
        {
            Consume();
            var chain = ParseChain();
            if (Peek().Kind != TokenKind.RParen)
                throw new CmdParseException("Expected ')' to close FOR body.", Peek().Line, Peek().Column);
            Consume();
            if (chain.Items.Count == 1) return chain.Items[0].Statement;
            return new ChainStatementWrapperAst(chain);
        }
        return ParseStatement();
    }

    private StatementAst ParseCall()
    {
        Consume(); // CALL
        if (Peek().Kind != TokenKind.Word)
            throw new CmdParseException("Expected target after CALL.", Peek().Line, Peek().Column);
        var target = Consume().Text;
        var args = new List<string>();
        while (Peek().Kind == TokenKind.Word) args.Add(Consume().Text);
        if (target.StartsWith(':'))
            return new CallLabelStatementAst(target.Substring(1), args);
        return new CallScriptStatementAst(target, args);
    }

    private string ExpectWord(string what)
    {
        if (Peek().Kind != TokenKind.Word)
            throw new CmdParseException($"Expected {what}.", Peek().Line, Peek().Column);
        return Consume().Text;
    }

    private void ExpectEndOfLine()
    {
        if (IsAtEnd()) return;
        if (Peek().Kind == TokenKind.Newline) { Consume(); return; }
        throw new CmdParseException($"Expected end of line, got {Peek().Kind}.", Peek().Line, Peek().Column);
    }

    private void SkipBlankLines()
    {
        while (Peek().Kind == TokenKind.Newline) Consume();
    }

    private void ConsumeIfNewline() { if (Peek().Kind == TokenKind.Newline) Consume(); }

    private static bool IsChainOp(TokenKind k) =>
        k == TokenKind.Amp || k == TokenKind.AmpAmp || k == TokenKind.PipePipe || k == TokenKind.Pipe;

    private static bool IsTerminator(TokenKind k) =>
        k == TokenKind.Newline || k == TokenKind.EndOfFile || k == TokenKind.Amp
        || k == TokenKind.AmpAmp || k == TokenKind.PipePipe || k == TokenKind.Pipe
        || k == TokenKind.RParen;

    private Token Peek(int offset = 0) => _pos + offset < _tokens.Count ? _tokens[_pos + offset] : _tokens[^1];
    private Token Consume() => _tokens[_pos++];
    private bool IsAtEnd() => Peek().Kind == TokenKind.EndOfFile;
}
