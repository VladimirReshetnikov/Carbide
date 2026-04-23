using CarbidePwsh.Host;
using Xunit;

namespace CarbidePwsh.Tests;

public class Phase3FunctionTests
{
    private static ShellHost NewShell() => new ShellHost();

    [Fact]
    public void SimpleFunction()
    {
        var host = NewShell();
        host.Submit("function f { param($x) $x * 2 }");
        Assert.Equal(10, host.Submit("f 5"));
    }

    [Fact]
    public void FunctionWithDefault()
    {
        var host = NewShell();
        host.Submit("function Sum { param($a, $b = 10) $a + $b }");
        Assert.Equal(15, host.Submit("Sum 5"));
        Assert.Equal(7, host.Submit("Sum 3 4"));
    }

    [Fact]
    public void FunctionWithTypedParameter()
    {
        var host = NewShell();
        host.Submit("function Greet { param([string] $name) \"hello $name\" }");
        Assert.Equal("hello V", host.Submit("Greet V"));
    }

    [Fact]
    public void ReturnEarlyExits()
    {
        var host = NewShell();
        host.Submit("function f { param($x) if ($x -lt 0) { return 'negative' }; 'positive' }");
        Assert.Equal("negative", host.Submit("f -5"));
        Assert.Equal("positive", host.Submit("f 5"));
    }

    [Fact]
    public void RecursiveFunction()
    {
        var host = NewShell();
        host.Submit("function Fact { param($n) if ($n -le 1) { 1 } else { $n * (Fact ($n - 1)) } }");
        Assert.Equal(120, host.Submit("Fact 5"));
    }

    [Fact]
    public void PipelineParticipatingFunction()
    {
        var host = NewShell();
        host.Submit("function Double { process { $_ * 2 } }");
        var r = host.Submit("1..3 | Double");
        Assert.Equal(new object?[] { 2, 4, 6 }, (object?[])r!);
    }

    [Fact]
    public void BeginProcessEndBlocks()
    {
        var host = NewShell();
        host.Submit("function Counter { begin { $s = 0 } process { $s += $_ } end { $s } }");
        Assert.Equal(6, host.Submit("1..3 | Counter"));
    }

    [Fact]
    public void CleanBlockRunsAfterNamedBlocks()
    {
        var host = NewShell();
        host.Submit("function Counter { begin { $s = 0 } process { $s += $_ } end { $s } clean { $script:cleanSeen = $true } }");
        Assert.Equal(6, host.Submit("1..3 | Counter"));
        Assert.Equal(true, host.Submit("$script:cleanSeen"));
    }

    [Fact]
    public void CleanBlockSkipsWhenNoNamedBlockExecuted()
    {
        var host = NewShell();
        host.Submit("$script:cleanSeen = $false");
        host.Submit("function Test-CleanOnly { clean { $script:cleanSeen = $true } }");
        Assert.Null(host.Submit("Test-CleanOnly"));
        Assert.Equal(false, host.Submit("$script:cleanSeen"));
    }

    [Fact]
    public void FunctionScopeIsolatesVariables()
    {
        var host = NewShell();
        host.Submit("$a = 1");
        host.Submit("function f { $a = 99 }");
        host.Submit("f");
        Assert.Equal(1, host.Submit("$a"));
    }

    [Fact]
    public void ScriptScopeReachesAcross()
    {
        var host = NewShell();
        host.Submit("$a = 1");
        host.Submit("function f { $script:a = 99 }");
        host.Submit("f");
        Assert.Equal(99, host.Submit("$a"));
    }

    [Fact]
    public void GlobalScope()
    {
        var host = NewShell();
        host.Submit("$global:x = 42");
        host.Submit("function f { $global:x }");
        Assert.Equal(42, host.Submit("f"));
    }
}
