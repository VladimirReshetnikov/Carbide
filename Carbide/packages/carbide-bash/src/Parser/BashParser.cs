using CarbideBash.Errors;
using CarbideBash.Lexer;

namespace CarbideBash.Parser;

/// <summary>
/// Recursive-descent bash parser for the Phase 1 subset. Understands: simple commands with
/// redirections and leading var=value assignments; pipelines; <c>;</c>/<c>&amp;&amp;</c>/<c>||</c>
/// statement lists; <c>if / elif / else / fi</c>; <c>while / until / do / done</c>;
/// <c>for x in ...; do ...; done</c>; <c>case ... esac</c>; function definitions with either
/// the <c>name () { ... }</c> or <c>function name { ... }</c> form; <c>{ ... }</c> groups
/// and <c>( ... )</c> subshells.
/// </summary>
public sealed class BashParser
{
    private readonly List<Token> _tokens;
    private int _pos;

    public BashParser(List<Token> tokens) { _tokens = tokens; }

    public static ScriptAst ParseString(string source)
    {
        var tokens = BashLexer.Tokenize(source);
        return new BashParser(tokens).ParseScript();
    }

    public ScriptAst ParseScript()
    {
        var stmts = new List<StatementAst>();
        SkipSeparators();
        while (!IsAtEnd())
        {
            var s = ParseList();
            if (s is not null) stmts.Add(s);
            SkipSeparators();
        }
        return new ScriptAst(stmts);
    }

    private StatementAst? ParseList()
    {
        var first = ParsePipeline();
        if (first is null) return null;
        var items = new List<ListItemAst> { new(ListOperator.None, first) };
        while (true)
        {
            if (Peek().Kind == TokenKind.AndIf) { Consume(); var next = ParsePipeline(); if (next is null) break; items.Add(new(ListOperator.And, next)); continue; }
            if (Peek().Kind == TokenKind.OrIf)  { Consume(); var next = ParsePipeline(); if (next is null) break; items.Add(new(ListOperator.Or, next));  continue; }
            break;
        }
        if (items.Count == 1) return items[0].Pipeline;
        return new ListAst(items);
    }

    private StatementAst? ParsePipeline()
    {
        var first = ParseStatement();
        if (first is null) return null;
        if (Peek().Kind != TokenKind.Pipe) return first;
        var stages = new List<StatementAst> { first };
        while (Peek().Kind == TokenKind.Pipe)
        {
            Consume();
            SkipNewlinesInline();
            var next = ParseStatement() ?? throw new BashParseException("Expected command after '|'.", Peek().Line, Peek().Column);
            stages.Add(next);
        }
        return new PipelineAst(stages);
    }

    private StatementAst? ParseStatement()
    {
        if (Peek().Kind == TokenKind.LBrace) return ParseBraceBlock();
        if (Peek().Kind == TokenKind.LParen) return ParseSubshell();
        if (Peek().Kind == TokenKind.Word)
        {
            var w = Peek().Text;
            if (w == "if") return ParseIf();
            if (w == "while") return ParseWhileOrUntil(until: false);
            if (w == "until") return ParseWhileOrUntil(until: true);
            if (w == "for") return ParseFor();
            if (w == "case") return ParseCase();
            if (w == "function") return ParseFunctionWithKeyword();
            // function-def: `name () { body }`
            if (_pos + 1 < _tokens.Count && _tokens[_pos + 1].Kind == TokenKind.LParen
                && _pos + 2 < _tokens.Count && _tokens[_pos + 2].Kind == TokenKind.RParen)
            {
                return ParseFunctionPosixForm();
            }
            return ParseSimpleCommand();
        }
        return null;
    }

    private SimpleCommandAst ParseSimpleCommand()
    {
        var words = new List<string>();
        var redirs = new List<RedirectionAst>();
        var assigns = new List<AssignmentAst>();

        while (true)
        {
            var k = Peek().Kind;
            if (k == TokenKind.Word)
            {
                var text = Peek().Text;
                if (words.Count == 0 && IsAssignment(text, out var name, out var value))
                {
                    assigns.Add(new AssignmentAst(name, value));
                    Consume();
                    continue;
                }
                words.Add(text);
                Consume();
                continue;
            }
            if (k == TokenKind.RedirOut) { Consume(); redirs.Add(new StdoutRedirection(ExpectWord("redirect target"), false)); continue; }
            if (k == TokenKind.RedirAppend) { Consume(); redirs.Add(new StdoutRedirection(ExpectWord("redirect target"), true)); continue; }
            if (k == TokenKind.RedirIn) { Consume(); redirs.Add(new StdinRedirection(ExpectWord("redirect source"))); continue; }
            if (k == TokenKind.RedirErr) { Consume(); redirs.Add(new StderrRedirection(ExpectWord("stderr redirect target"))); continue; }
            if (k == TokenKind.Heredoc || k == TokenKind.HeredocDash)
            {
                Consume();
                var delim = ExpectWord("heredoc delimiter");
                // Quoted delimiter disables body expansion; match real bash.
                bool expandable = !(delim.StartsWith('"') || delim.StartsWith('\''));
                // Skip through the terminating newline that the lexer emitted.
                if (Peek().Kind == TokenKind.Newline) Consume();
                // Collect body text.
                var body = "";
                if (Peek().Kind == TokenKind.HeredocBody) body = Consume().Text;
                redirs.Add(new HeredocRedirection(body, expandable));
                continue;
            }
            if (k == TokenKind.HereString)
            {
                Consume();
                var content = ExpectWord("here-string content");
                redirs.Add(new HereStringRedirection(content));
                continue;
            }
            break;
        }
        return new SimpleCommandAst(words, redirs, assigns);
    }

    private static bool IsAssignment(string text, out string name, out string value)
    {
        name = ""; value = "";
        var eq = text.IndexOf('=');
        if (eq <= 0) return false;
        var head = text.Substring(0, eq);
        foreach (var ch in head)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '_') return false;
        }
        if (char.IsDigit(head[0])) return false;
        name = head;
        value = text.Substring(eq + 1);
        return true;
    }

    private IfStatementAst ParseIf()
    {
        Expect("if");
        var condition = ParseCompoundList(new[] { "then" });
        Expect("then");
        var thenBody = ParseCompoundList(new[] { "fi", "else", "elif" });
        var elifs = new List<ElifClauseAst>();
        while (Peek().Kind == TokenKind.Word && Peek().Text == "elif")
        {
            Consume();
            var elifCond = ParseCompoundList(new[] { "then" });
            Expect("then");
            var elifBody = ParseCompoundList(new[] { "fi", "else", "elif" });
            elifs.Add(new ElifClauseAst(elifCond, elifBody));
        }
        StatementAst? elseBody = null;
        if (Peek().Kind == TokenKind.Word && Peek().Text == "else")
        {
            Consume();
            elseBody = ParseCompoundList(new[] { "fi" });
        }
        Expect("fi");
        return new IfStatementAst(condition, thenBody, elifs, elseBody);
    }

    private WhileStatementAst ParseWhileOrUntil(bool until)
    {
        Expect(until ? "until" : "while");
        var cond = ParseCompoundList(new[] { "do" });
        Expect("do");
        var body = ParseCompoundList(new[] { "done" });
        Expect("done");
        return new WhileStatementAst(cond, body, until);
    }

    private ForStatementAst ParseFor()
    {
        Expect("for");
        var name = ExpectWord("loop variable name");
        if (Peek().Kind == TokenKind.Word && Peek().Text == "in")
        {
            Consume();
            var words = new List<string>();
            while (Peek().Kind == TokenKind.Word && Peek().Text != "do") words.Add(Consume().Text);
            SkipSeparators();
            Expect("do");
            var body = ParseCompoundList(new[] { "done" });
            Expect("done");
            return new ForStatementAst(name, words, body);
        }
        SkipSeparators();
        Expect("do");
        var bodyOnly = ParseCompoundList(new[] { "done" });
        Expect("done");
        return new ForStatementAst(name, Array.Empty<string>(), bodyOnly);
    }

    private CaseStatementAst ParseCase()
    {
        Expect("case");
        var word = ExpectWord("case word");
        if (!(Peek().Kind == TokenKind.Word && Peek().Text == "in"))
            throw new BashParseException("Expected 'in' in case.", Peek().Line, Peek().Column);
        Consume();
        SkipSeparators();
        var clauses = new List<CaseClauseAst>();
        while (!(Peek().Kind == TokenKind.Word && Peek().Text == "esac"))
        {
            var patterns = new List<string> { ExpectWord("case pattern") };
            while (Peek().Kind == TokenKind.Pipe) { Consume(); patterns.Add(ExpectWord("case pattern")); }
            if (Peek().Kind != TokenKind.RParen)
                throw new BashParseException("Expected ')' after case pattern.", Peek().Line, Peek().Column);
            Consume();
            var body = ParseCompoundList(new[] { ";;" }, caseStop: true);
            // consume the ;;
            if (Peek().Kind == TokenKind.Semicolon) { Consume(); if (Peek().Kind == TokenKind.Semicolon) Consume(); }
            SkipSeparators();
            clauses.Add(new CaseClauseAst(patterns, body));
        }
        Expect("esac");
        return new CaseStatementAst(word, clauses);
    }

    private FunctionDefAst ParseFunctionWithKeyword()
    {
        Expect("function");
        var name = ExpectWord("function name");
        if (Peek().Kind == TokenKind.LParen) { Consume(); if (Peek().Kind == TokenKind.RParen) Consume(); }
        SkipSeparators();
        StatementAst body;
        if (Peek().Kind == TokenKind.LBrace) body = ParseBraceBlock();
        else body = ParseStatement() ?? throw new BashParseException("Expected function body.", Peek().Line, Peek().Column);
        return new FunctionDefAst(name, body);
    }

    private FunctionDefAst ParseFunctionPosixForm()
    {
        var name = Consume().Text;
        Consume(); // (
        Consume(); // )
        SkipSeparators();
        StatementAst body;
        if (Peek().Kind == TokenKind.LBrace) body = ParseBraceBlock();
        else body = ParseStatement() ?? throw new BashParseException("Expected function body.", Peek().Line, Peek().Column);
        return new FunctionDefAst(name, body);
    }

    private BlockAst ParseBraceBlock()
    {
        Consume(); // {
        SkipSeparators();
        var stmts = new List<StatementAst>();
        while (Peek().Kind != TokenKind.RBrace && !IsAtEnd())
        {
            var s = ParseList();
            if (s is not null) stmts.Add(s);
            SkipSeparators();
        }
        if (Peek().Kind != TokenKind.RBrace)
            throw new BashParseException("Expected '}' to close block.", Peek().Line, Peek().Column);
        Consume();
        return new BlockAst(stmts, Subshell: false);
    }

    private BlockAst ParseSubshell()
    {
        Consume(); // (
        SkipSeparators();
        var stmts = new List<StatementAst>();
        while (Peek().Kind != TokenKind.RParen && !IsAtEnd())
        {
            var s = ParseList();
            if (s is not null) stmts.Add(s);
            SkipSeparators();
        }
        if (Peek().Kind != TokenKind.RParen)
            throw new BashParseException("Expected ')' to close subshell.", Peek().Line, Peek().Column);
        Consume();
        return new BlockAst(stmts, Subshell: true);
    }

    private StatementAst ParseCompoundList(IReadOnlyCollection<string> stopKeywords, bool caseStop = false)
    {
        var stmts = new List<StatementAst>();
        SkipSeparatorsRespectingCase(caseStop);
        while (!IsAtEnd() && !IsStopKeyword(stopKeywords) && !IsCaseTerminator(caseStop))
        {
            var s = ParseList();
            if (s is not null) stmts.Add(s);
            SkipSeparatorsRespectingCase(caseStop);
        }
        if (stmts.Count == 1) return stmts[0];
        return new BlockAst(stmts, Subshell: false);
    }

    private bool IsCaseTerminator(bool caseStop)
        => caseStop && Peek().Kind == TokenKind.Semicolon && Peek(1).Kind == TokenKind.Semicolon;

    private void SkipSeparatorsRespectingCase(bool caseStop)
    {
        while (Peek().Kind == TokenKind.Newline
               || (Peek().Kind == TokenKind.Semicolon && !(caseStop && Peek(1).Kind == TokenKind.Semicolon)))
        {
            Consume();
        }
    }

    private bool IsStopKeyword(IReadOnlyCollection<string> stopKeywords)
    {
        if (Peek().Kind != TokenKind.Word) return false;
        foreach (var k in stopKeywords) if (Peek().Text == k) return true;
        return false;
    }

    private string ExpectWord(string what)
    {
        if (Peek().Kind != TokenKind.Word)
            throw new BashParseException($"Expected {what}.", Peek().Line, Peek().Column);
        return Consume().Text;
    }

    private void Expect(string word)
    {
        if (Peek().Kind != TokenKind.Word || Peek().Text != word)
            throw new BashParseException($"Expected '{word}'.", Peek().Line, Peek().Column);
        Consume();
    }

    private void SkipSeparators()
    {
        while (Peek().Kind == TokenKind.Newline || Peek().Kind == TokenKind.Semicolon) Consume();
    }

    private void SkipNewlinesInline()
    {
        while (Peek().Kind == TokenKind.Newline) Consume();
    }

    private Token Peek(int offset = 0) => _pos + offset < _tokens.Count ? _tokens[_pos + offset] : _tokens[^1];
    private Token Consume() => _tokens[_pos++];
    private bool IsAtEnd() => Peek().Kind == TokenKind.EndOfFile;
}
