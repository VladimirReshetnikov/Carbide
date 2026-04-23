using CarbidePwsh.Host;
using Xunit;

namespace CarbidePwsh.Tests;

public class Phase3ControlFlowTests
{
    private static ShellHost NewShell() => new ShellHost();

    [Fact]
    public void IfTrueBranch()
    {
        Assert.Equal("yes", NewShell().Submit("if (1 -eq 1) { 'yes' } else { 'no' }"));
    }

    [Fact]
    public void IfElseIfBranch()
    {
        Assert.Equal("b", NewShell().Submit("if ($false) { 'a' } elseif ($true) { 'b' } else { 'c' }"));
    }

    [Fact]
    public void IfElseBranch()
    {
        Assert.Equal("c", NewShell().Submit("if ($false) { 'a' } elseif ($false) { 'b' } else { 'c' }"));
    }

    [Fact]
    public void WhileLoop()
    {
        var host = NewShell();
        host.Submit("$i = 0");
        var r = host.Submit("while ($i -lt 3) { $i; $i++ }");
        Assert.Equal(new object?[] { 0, 1, 2 }, (object?[])r!);
    }

    [Fact]
    public void DoWhileLoop()
    {
        var host = NewShell();
        host.Submit("$i = 3");
        var r = host.Submit("do { $i; $i-- } while ($i -gt 0)");
        Assert.Equal(new object?[] { 3, 2, 1 }, (object?[])r!);
    }

    [Fact]
    public void DoUntilLoop()
    {
        var host = NewShell();
        host.Submit("$i = 0");
        var r = host.Submit("do { $i; $i++ } until ($i -eq 3)");
        Assert.Equal(new object?[] { 0, 1, 2 }, (object?[])r!);
    }

    [Fact]
    public void ForLoop()
    {
        var r = NewShell().Submit("for ($i = 0; $i -lt 3; $i++) { $i }");
        Assert.Equal(new object?[] { 0, 1, 2 }, (object?[])r!);
    }

    [Fact]
    public void ForEachLoop()
    {
        var r = NewShell().Submit("foreach ($x in 1..3) { $x * $x }");
        Assert.Equal(new object?[] { 1, 4, 9 }, (object?[])r!);
    }

    [Fact]
    public void BreakExitsLoop()
    {
        var r = NewShell().Submit("foreach ($x in 1..5) { if ($x -eq 3) { break }; $x }");
        Assert.Equal(new object?[] { 1, 2 }, (object?[])r!);
    }

    [Fact]
    public void ContinueSkipsIteration()
    {
        var r = NewShell().Submit("foreach ($x in 1..5) { if ($x % 2 -eq 0) { continue }; $x }");
        Assert.Equal(new object?[] { 1, 3, 5 }, (object?[])r!);
    }

    [Fact]
    public void SwitchLiteralMatch()
    {
        Assert.Equal("two", NewShell().Submit("switch (2) { 1 { 'one' } 2 { 'two' } default { '?' } }"));
    }

    [Fact]
    public void SwitchDefault()
    {
        Assert.Equal("?", NewShell().Submit("switch (99) { 1 { 'one' } 2 { 'two' } default { '?' } }"));
    }

    [Fact]
    public void SwitchWildcardMatchesPattern()
    {
        Assert.Equal("hit", NewShell().Submit("switch -Wildcard ('alpha') { 'a*' { 'hit' } default { 'miss' } }"));
    }

    [Fact]
    public void NestedLoopsBreakOuter()
    {
        var host = NewShell();
        var r = host.Submit("$sum = 0; foreach ($i in 1..3) { foreach ($j in 1..3) { $sum++; if ($j -eq 2) { break } } }; $sum");
        Assert.Equal(6, r);
    }
}
