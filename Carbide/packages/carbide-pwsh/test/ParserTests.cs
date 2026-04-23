using CarbidePwsh.Errors;
using CarbidePwsh.Lexer;
using CarbidePwsh.Parser.Ast;
using Xunit;
using PwshParser = CarbidePwsh.Parser.Parser;

namespace CarbidePwsh.Tests;

public class ParserTests
{
    private static ScriptAst Parse(string s) => PwshParser.ParseString(s);

    [Fact]
    public void ParsesNumberLiteral()
    {
        var script = Parse("42");
        var stmt = Assert.IsType<ExpressionStatementAst>(script.Statements.Single());
        var n = Assert.IsType<NumberLiteralAst>(stmt.Expression);
        Assert.Equal(42, n.Value);
    }

    [Fact]
    public void ParsesArithmeticWithPrecedence()
    {
        var script = Parse("2 + 3 * 4");
        var stmt = Assert.IsType<ExpressionStatementAst>(script.Statements.Single());
        var b = Assert.IsType<BinaryExpressionAst>(stmt.Expression);
        Assert.Equal(BinaryOp.Add, b.Op);
        Assert.IsType<NumberLiteralAst>(b.Left);
        var r = Assert.IsType<BinaryExpressionAst>(b.Right);
        Assert.Equal(BinaryOp.Multiply, r.Op);
    }

    [Fact]
    public void ParensOverridePrecedence()
    {
        var script = Parse("(2 + 3) * 4");
        var b = Assert.IsType<BinaryExpressionAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.Equal(BinaryOp.Multiply, b.Op);
        Assert.IsType<ParenExpressionAst>(b.Left);
    }

    [Fact]
    public void ParsesAssignment()
    {
        var script = Parse("$x = 5");
        var a = Assert.IsType<AssignmentStatementAst>(script.Statements.Single());
        Assert.IsType<VariableAst>(a.Target);
        Assert.Equal(AssignmentOp.Assign, a.Op);
        Assert.IsType<NumberLiteralAst>(a.Value);
    }

    [Fact]
    public void ParsesCompoundAssignment()
    {
        var script = Parse("$x += 1");
        var a = Assert.IsType<AssignmentStatementAst>(script.Statements.Single());
        Assert.Equal(AssignmentOp.AddAssign, a.Op);
    }

    [Fact]
    public void ParsesStaticMemberInvocation()
    {
        var script = Parse("[System.Math]::Sqrt(2)");
        var m = Assert.IsType<MemberAccessAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.True(m.IsStatic);
        Assert.True(m.IsInvocation);
        Assert.Equal("Sqrt", m.MemberName);
        Assert.IsType<TypeLiteralAst>(m.Target);
    }

    [Fact]
    public void ParsesStaticMemberPropertyGet()
    {
        var script = Parse("[System.Math]::PI");
        var m = Assert.IsType<MemberAccessAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.True(m.IsStatic);
        Assert.False(m.IsInvocation);
        Assert.Equal("PI", m.MemberName);
    }

    [Fact]
    public void ParsesStaticMemberPropertySet()
    {
        var script = Parse("[System.Console]::BackgroundColor = 'Red'");
        var a = Assert.IsType<AssignmentStatementAst>(script.Statements.Single());
        Assert.IsType<MemberAccessAst>(a.Target);
    }

    [Fact]
    public void ParsesTypeCast()
    {
        var script = Parse("[int]'42'");
        var c = Assert.IsType<CastExpressionAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.Equal("int", c.TargetType.TypeName);
    }

    [Fact]
    public void ParsesRange()
    {
        var script = Parse("1..5");
        var r = Assert.IsType<RangeExpressionAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.IsType<NumberLiteralAst>(r.Start);
        Assert.IsType<NumberLiteralAst>(r.End);
    }

    [Fact]
    public void ParsesArrayLiteral()
    {
        var script = Parse("@(1, 2, 3)");
        var arr = Assert.IsType<ArrayExpressionAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.Equal(3, arr.Elements.Count);
    }

    [Fact]
    public void ParsesHashtableLiteral()
    {
        var script = Parse("@{ a = 1; b = 2 }");
        var h = Assert.IsType<HashtableExpressionAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.Equal(2, h.Entries.Count);
    }

    [Fact]
    public void ParsesComparison()
    {
        var script = Parse("1 -eq 1");
        var b = Assert.IsType<BinaryExpressionAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.Equal(BinaryOp.Equal, b.Op);
    }

    [Fact]
    public void ParsesLogicalAnd()
    {
        var script = Parse("$true -and $false");
        var b = Assert.IsType<BinaryExpressionAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.Equal(BinaryOp.And, b.Op);
    }

    [Fact]
    public void ParsesUnaryNegation()
    {
        var script = Parse("-5");
        var u = Assert.IsType<UnaryExpressionAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.Equal(UnaryOp.Negate, u.Op);
    }

    [Fact]
    public void ParsesLogicalNot()
    {
        var script = Parse("!$true");
        var u = Assert.IsType<UnaryExpressionAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.Equal(UnaryOp.Not, u.Op);
    }

    [Fact]
    public void ParsesMultipleStatements()
    {
        var script = Parse("$x = 5; $x + 1");
        Assert.Equal(2, script.Statements.Count);
    }

    [Fact]
    public void ParsesIndexer()
    {
        var script = Parse("$a[0]");
        var ix = Assert.IsType<IndexerAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.IsType<VariableAst>(ix.Target);
    }

    [Fact]
    public void ParsesInstanceMethodCall()
    {
        var script = Parse("'hello'.ToUpper()");
        var m = Assert.IsType<MemberAccessAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.False(m.IsStatic);
        Assert.True(m.IsInvocation);
        Assert.Equal("ToUpper", m.MemberName);
    }

    [Fact]
    public void ParsesStringInterpolation()
    {
        var script = Parse("\"hello $name\"");
        Assert.IsType<StringLiteralAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
    }

    [Fact]
    public void ParsesSubexpressionInString()
    {
        var script = Parse("\"sum = $(1 + 2)\"");
        var s = Assert.IsType<StringLiteralAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.Equal(2, s.Parts.Count);
    }

    [Fact]
    public void ParsesConditionalExpression()
    {
        var script = Parse("$true ? 'yes' : 'no'");
        var expr = Assert.IsType<ConditionalExpressionAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.IsType<BooleanLiteralAst>(expr.Condition);
        Assert.IsType<StringLiteralAst>(expr.WhenTrue);
        Assert.IsType<StringLiteralAst>(expr.WhenFalse);
    }

    [Fact]
    public void ParsesAssignmentExpressionInsideParens()
    {
        var script = Parse("($line = $reader.ReadLine())");
        var paren = Assert.IsType<ParenExpressionAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        var assign = Assert.IsType<AssignmentExpressionAst>(paren.Inner);
        Assert.Equal(AssignmentOp.Assign, assign.Op);
    }

    [Fact]
    public void ParsesNullCoalescingExpression()
    {
        var script = Parse("$left ?? 'fallback'");
        var expr = Assert.IsType<BinaryExpressionAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.Equal(BinaryOp.Coalesce, expr.Op);
    }

    [Fact]
    public void ParsesChainedAssignment()
    {
        var script = Parse("$a = $b = 42");
        var assign = Assert.IsType<AssignmentStatementAst>(script.Statements.Single());
        var nested = Assert.IsType<AssignmentExpressionAst>(assign.Value);
        Assert.Equal(AssignmentOp.Assign, nested.Op);
    }

    [Fact]
    public void ParsesGroupedCommandArgumentWithIndexerAndMemberAccess()
    {
        var script = Parse("pushd (Get-ChildItem -Path 'C:\\Program Files*')[0].FullName");
        var pipeline = Assert.IsType<PipelineAst>(script.Statements.Single());
        var command = Assert.IsType<CommandAst>(pipeline.Stages.Single());
        var argument = Assert.IsType<CommandArgumentAst>(command.Elements.Single());
        var member = Assert.IsType<MemberAccessAst>(argument.Expression);
        Assert.Equal("FullName", member.MemberName);
        var indexer = Assert.IsType<IndexerAst>(member.Target);
        Assert.IsType<SubExpressionAst>(indexer.Target);
    }

    [Fact]
    public void ParsesGroupedCommandArgumentWithMemberInvocationChain()
    {
        var script = Parse("Assert-Equal -Expected (Get-FileHash -LiteralPath $strongDll -Algorithm SHA256).Hash.ToUpperInvariant()");
        var pipeline = Assert.IsType<PipelineAst>(script.Statements.Single());
        var command = Assert.IsType<CommandAst>(pipeline.Stages.Single());
        var expected = Assert.IsType<CommandArgumentAst>(command.Elements[1]);
        var toUpper = Assert.IsType<MemberAccessAst>(expected.Expression);
        Assert.True(toUpper.IsInvocation);
        Assert.Equal("ToUpperInvariant", toUpper.MemberName);
        var hash = Assert.IsType<MemberAccessAst>(toUpper.Target);
        Assert.Equal("Hash", hash.MemberName);
        Assert.IsType<SubExpressionAst>(hash.Target);
    }

    [Fact]
    public void RejectsWhitespaceSeparatedMethodInvocation()
    {
        Assert.Throws<PwshParseException>(() => Parse("$s.ToUpper ()"));
    }

    [Fact]
    public void ParsesQuestionAliasCommandInPipeline()
    {
        var script = Parse("1..5 | ? { $_ -gt 3 }");
        var pipeline = Assert.IsType<PipelineAst>(script.Statements.Single());
        var command = Assert.IsType<CommandAst>(pipeline.Stages[1]);
        Assert.Equal("?", command.Name);
    }

    [Fact]
    public void ParsesForEachAliasCommandInPipeline()
    {
        var script = Parse("1..5 | ForEach { $_ * 2 }");
        var pipeline = Assert.IsType<PipelineAst>(script.Statements.Single());
        var command = Assert.IsType<CommandAst>(pipeline.Stages[1]);
        Assert.Equal("ForEach", command.Name);
    }

    [Fact]
    public void ParsesNumericLeadingCommandName()
    {
        var script = Parse("7z l archive.7z");
        var pipeline = Assert.IsType<PipelineAst>(script.Statements.Single());
        var command = Assert.IsType<CommandAst>(pipeline.Stages.Single());
        Assert.Equal("7z", command.Name);
    }

    [Fact]
    public void ParsesExpressionRedirectionByLoweringToWriteOutput()
    {
        var script = Parse("'hello' > out.txt");
        var pipeline = Assert.IsType<PipelineAst>(script.Statements.Single());
        var command = Assert.IsType<CommandAst>(pipeline.Stages.Single());
        Assert.Equal("Write-Output", command.Name);
        Assert.Contains(command.Elements, element => element is CommandRedirectionAst);
    }

    [Fact]
    public void ParsesStatementStartNumericExpressionRedirection()
    {
        var script = Parse("1>>variable:a");
        var pipeline = Assert.IsType<PipelineAst>(script.Statements.Single());
        var command = Assert.IsType<CommandAst>(pipeline.Stages.Single());
        Assert.Equal("Write-Output", command.Name);
        var value = Assert.IsType<CommandArgumentAst>(command.Elements[0]);
        Assert.Equal(1, Assert.IsType<NumberLiteralAst>(value.Expression).Value);
        var redirection = Assert.IsType<CommandRedirectionAst>(command.Elements[1]);
        Assert.True(redirection.Append);
    }

    [Fact]
    public void ParsesBracedVariableWithUnicodeEscapeAssignment()
    {
        var script = Parse("${fooxyzzy`u{2195}} = 42");
        var assignment = Assert.IsType<AssignmentStatementAst>(script.Statements.Single());
        var variable = Assert.IsType<VariableAst>(assignment.Target);
        Assert.Equal("fooxyzzy↕", variable.Name);
        Assert.Equal(42, Assert.IsType<NumberLiteralAst>(assignment.Value).Value);
    }

    [Fact]
    public void ParsesClassWithModifiersAttributesAndBaseType()
    {
        var script = Parse("class Child : Base { [ValidateNotNullOrEmpty()] [string] hidden $Name; static [string] Make() { return 'ok' } }");
        var cls = Assert.IsType<ClassDefinitionAst>(script.Statements.Single());
        Assert.Single(cls.Properties);
        Assert.Single(cls.Methods);
        Assert.True(cls.Methods[0].IsStatic);
    }

    [Fact]
    public void ParsesComparisonWithCommaListLeftOperand()
    {
        var script = Parse("'master', 'tempdb' -contains $Database");
        var expr = Assert.IsType<BinaryExpressionAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        var left = Assert.IsType<ArrayExpressionAst>(expr.Left);
        Assert.Equal(2, left.Elements.Count);
        Assert.Equal(BinaryOp.Contains, expr.Op);
    }

    [Fact]
    public void ParsesComparisonWithCommaListRightOperand()
    {
        var script = Parse("1 -eq 1, 2 -eq 2");
        var expr = Assert.IsType<BinaryExpressionAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        var right = Assert.IsType<ArrayExpressionAst>(expr.Right);
        Assert.Equal(2, right.Elements.Count);
    }

    [Fact]
    public void ParsesBracketedGenericTypeArguments()
    {
        var script = Parse("[dictionary[[string], [int]]]::new()");
        var member = Assert.IsType<MemberAccessAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        var type = Assert.IsType<TypeLiteralAst>(member.Target);
        Assert.Equal("dictionary", type.TypeName);
        Assert.Equal(2, type.GenericArguments.Count);
        Assert.Equal("string", type.GenericArguments[0].TypeName);
        Assert.Equal("int", type.GenericArguments[1].TypeName);
    }

    [Fact]
    public void ParsesAritySuffixedGenericTypeName()
    {
        var script = Parse("[Func`3[[int], [string], [object]]]");
        var type = Assert.IsType<TypeLiteralAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.Equal("Func`3", type.TypeName);
        Assert.Equal(3, type.GenericArguments.Count);
    }

    [Fact]
    public void ParsesSwitchWithBareWordPattern()
    {
        var script = Parse("switch ($result) { Passed { 'ok' } default { 'no' } }");
        var sw = Assert.IsType<SwitchStatementAst>(script.Statements.Single());
        var pattern = Assert.IsType<StringLiteralAst>(sw.Cases[0].Pattern);
        Assert.Equal("Passed", string.Concat(pattern.Parts.OfType<LiteralPart>().Select(static p => p.Text)));
    }

    [Fact]
    public void ParsesClassWithMultipleBaseTypes()
    {
        var script = Parse("class DatabasePermission : IComparable, System.IEquatable[Object] { }");
        var cls = Assert.IsType<ClassDefinitionAst>(script.Statements.Single());
        Assert.Empty(cls.Properties);
        Assert.Empty(cls.Methods);
    }

    [Fact]
    public void ParsesSwitchFileSyntaxWithoutParentheses()
    {
        var script = Parse("switch -regex -file $Path { '^x$' { 'ok' } }");
        var sw = Assert.IsType<SwitchStatementAst>(script.Statements.Single());
        Assert.True(sw.Condition is VariableAst or SubExpressionAst);
    }

    [Fact]
    public void ParsesPipelineContinuationAcrossNewlineInAssignmentRhs()
    {
        var script = Parse("$ownersLine = $codeowners\n| Where-Object { $_ -like '*' }");
        var assign = Assert.IsType<AssignmentStatementAst>(script.Statements.Single());
        Assert.IsType<SubExpressionAst>(assign.Value);
    }

    [Fact]
    public void ParsesExpressionRedirectionFollowedByPipe()
    {
        var script = Parse("\"some text\" > out.txt | Out-Null");
        var pipeline = Assert.IsType<PipelineAst>(script.Statements.Single());
        Assert.Equal(2, pipeline.Stages.Count);
    }

    [Fact]
    public void ParsesBackgroundSuffixAfterExpressionAssignmentRhs()
    {
        var script = Parse("$j = (Get-Variable -Value ExecutionContext).SessionState.PSVariable.Get(\"MyInvocation\").Value.MyCommand.ScriptBlock &\n(Receive-Job -Wait $j).ToString()");
        Assert.Equal(2, script.Statements.Count);
        var assignment = Assert.IsType<AssignmentStatementAst>(script.Statements[0]);
        Assert.IsType<SubExpressionAst>(assignment.Value);
        Assert.IsType<ExpressionStatementAst>(script.Statements[1]);
    }

    [Fact]
    public void ParsesNestedPesterBlocksWithUnicodeBracedVariable()
    {
        var script = Parse(
            """
            Describe "outer" {
                Context "unicode variables" {
                    It "first" {
                        ${fooxyzzy`u{2195}} = 42
                    }

                    It "second" {
                        ${fooxyzzy`u{2195}}
                    }
                }
            }
            """);

        var describe = Assert.IsType<PipelineAst>(script.Statements.Single());
        var describeCommand = Assert.IsType<CommandAst>(describe.Stages.Single());
        var describeBody = Assert.IsType<ScriptBlockAst>(Assert.IsType<CommandArgumentAst>(describeCommand.Elements.Last()).Expression).Body;
        var context = Assert.IsType<PipelineAst>(describeBody.Statements.Single());
        var contextCommand = Assert.IsType<CommandAst>(context.Stages.Single());
        var contextBody = Assert.IsType<ScriptBlockAst>(Assert.IsType<CommandArgumentAst>(contextCommand.Elements.Last()).Expression).Body;
        Assert.Equal(2, contextBody.Statements.Count);
    }

    [Fact]
    public void ParsesNumericMemberAccess()
    {
        var script = Parse("$Matches.1");
        var member = Assert.IsType<MemberAccessAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.Equal("1", member.MemberName);
    }

    [Fact]
    public void ParsesForStatementWithoutUpdateClauseSemicolon()
    {
        var script = Parse("for ($i = 0; $i -lt 3) { $i }");
        var statement = Assert.IsType<ForStatementAst>(script.Statements.Single());
        Assert.Null(statement.Update);
    }

    [Fact]
    public void ParsesIntrinsicWhereMethodWithoutParentheses()
    {
        var script = Parse("$items.Where{ $_.Ready }");
        var expr = Assert.IsType<MemberAccessAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.True(expr.IsInvocation);
        Assert.Equal("Where", expr.MemberName);
        Assert.Single(expr.Arguments!);
    }

    [Fact]
    public void ParsesLabeledForEachStatement()
    {
        var script = Parse(":main foreach ($item in 1..3) { if ($item -eq 2) { break main } }");
        var loop = Assert.IsType<ForEachStatementAst>(script.Statements.Single());
        Assert.Equal("main", loop.Label);
    }

    [Fact]
    public void ParsesScopedForEachVariable()
    {
        var script = Parse("foreach ($script:item in 1..3) { $script:item }");
        var loop = Assert.IsType<ForEachStatementAst>(script.Statements.Single());
        Assert.Equal("script", loop.VariableScope);
        Assert.Equal("item", loop.VariableName);
    }

    [Fact]
    public void ParsesConstructorBaseInitializer()
    {
        var script = Parse("class Child : Base { Child([int] $x) : base($x) { } }");
        var cls = Assert.IsType<ClassDefinitionAst>(script.Statements.Single());
        Assert.Single(cls.Methods);
        Assert.True(cls.Methods[0].IsConstructor);
    }

    [Fact]
    public void ParsesDelegateScriptBlockCast()
    {
        var script = Parse("[System.Action] { 'hi' }");
        var expr = Assert.IsType<CastExpressionAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.IsType<ScriptBlockAst>(expr.Value);
    }

    [Fact]
    public void ParsesMultidimensionalIndexer()
    {
        var script = Parse("$board[0,7]");
        var expr = Assert.IsType<IndexerAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        var indices = Assert.IsType<ArrayExpressionAst>(expr.Index);
        Assert.Equal(2, indices.Elements.Count);
    }

    [Fact]
    public void ParsesMemberChainSplitAcrossLines()
    {
        var script = Parse("$ps.AddCommand('x').\n    AddParameter('y')");
        var expr = Assert.IsType<MemberAccessAst>(((ExpressionStatementAst)script.Statements.Single()).Expression);
        Assert.True(expr.IsInvocation);
        Assert.Equal("AddParameter", expr.MemberName);
    }

    [Fact]
    public void ParsesInlineCommandParameterValue()
    {
        var script = Parse("It 'name' -Skip:$(Should-Skip) { 'ok' }");
        var pipeline = Assert.IsType<PipelineAst>(script.Statements.Single());
        var command = Assert.IsType<CommandAst>(pipeline.Stages.Single());
        Assert.Contains(command.Elements, static element => element is CommandParameterAst parameter && parameter.Name == "Skip");
        Assert.Contains(command.Elements, static element => element is CommandArgumentAst);
    }

    [Fact]
    public void ParsesExpandableCommandTextArgument()
    {
        var script = Parse("Write-Host $HOME/Library/Devices/$($device.Id)");
        var pipeline = Assert.IsType<PipelineAst>(script.Statements.Single());
        var command = Assert.IsType<CommandAst>(pipeline.Stages.Single());
        var argument = Assert.IsType<CommandArgumentAst>(command.Elements.Single());
        var literal = Assert.IsType<StringLiteralAst>(argument.Expression);
        Assert.Contains(literal.Parts, static part => part is VariablePart);
        Assert.Contains(literal.Parts, static part => part is ExpressionPart);
    }
}
