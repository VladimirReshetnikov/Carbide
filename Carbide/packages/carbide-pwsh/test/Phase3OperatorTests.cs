using CarbidePwsh.Host;
using Xunit;

namespace CarbidePwsh.Tests;

public class Phase3OperatorTests
{
    private static object? Eval(string src) => new ShellHost().Submit(src);

    [Fact] public void Match() => Assert.Equal(true, Eval("'hello world' -match 'hello'"));
    [Fact] public void NotMatch() => Assert.Equal(true, Eval("'abc' -notmatch '^\\d+$'"));
    [Fact] public void Like() => Assert.Equal(true, Eval("'foo.bar' -like '*.bar'"));
    [Fact] public void NotLike() => Assert.Equal(true, Eval("'abc' -notlike 'x*'"));
    [Fact] public void Replace() => Assert.Equal("hello universe", Eval("'hello world' -replace 'world', 'universe'"));
    [Fact] public void FormatInt() => Assert.Equal("0xFF", Eval("'0x{0:X}' -f 255"));
    [Fact] public void FormatMultiple() => Assert.Equal("1-2-3", Eval("'{0}-{1}-{2}' -f 1, 2, 3"));
    [Fact] public void Join() => Assert.Equal("a,b,c", Eval("@('a','b','c') -join ','"));
    [Fact] public void Split()
    {
        var arr = (string[])Eval("'a,b,c' -split ','")!;
        Assert.Equal(new[] { "a", "b", "c" }, arr);
    }
    [Fact] public void Contains() => Assert.Equal(true, Eval("@(1,2,3) -contains 2"));
    [Fact] public void NotContains() => Assert.Equal(true, Eval("@(1,2,3) -notcontains 99"));
    [Fact] public void In() => Assert.Equal(true, Eval("2 -in @(1,2,3)"));
    [Fact] public void NotIn() => Assert.Equal(true, Eval("99 -notin @(1,2,3)"));

    [Fact]
    public void MatchPopulatesMatches()
    {
        var host = new ShellHost();
        host.Submit("'hello world' -match '(\\w+) (\\w+)'");
        Assert.Equal("hello", host.Submit("$Matches[1]"));
        Assert.Equal("world", host.Submit("$Matches[2]"));
    }

    [Fact]
    public void MatchOnCollection()
    {
        var arr = (object?[])Eval("@('apple', 'banana', 'cherry') -match 'an'")!;
        Assert.Equal(new object?[] { "banana" }, arr);
    }

    [Fact]
    public void ReplaceOnCollection()
    {
        var arr = (object?[])Eval("@('a1', 'b2') -replace '\\d', 'x'")!;
        Assert.Equal(new object?[] { "ax", "bx" }, arr);
    }
}
