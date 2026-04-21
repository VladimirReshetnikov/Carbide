using CarbidePwsh.Host;
using CarbidePwsh.Runtime;
using Xunit;

namespace CarbidePwsh.Tests;

public class Phase3ClassEnumTests
{
    private static ShellHost NewShell() => new ShellHost();

    [Fact]
    public void ClassWithField()
    {
        var host = NewShell();
        host.Submit("class P { [int] $X; [int] $Y }");
        host.Submit("$p = [P]::new()");
        host.Submit("$p.X = 3");
        Assert.Equal(3, host.Submit("$p.X"));
    }

    [Fact]
    public void ClassFieldDefaults()
    {
        var host = NewShell();
        host.Submit("class Q { [int] $V = 5 }");
        host.Submit("$q = [Q]::new()");
        Assert.Equal(5, host.Submit("$q.V"));
    }

    [Fact]
    public void ClassConstructor()
    {
        var host = NewShell();
        host.Submit("class P { [int] $V; P([int] $v) { $this.V = $v } }");
        Assert.Equal(7, host.Submit("[P]::new(7).V"));
    }

    [Fact]
    public void ClassMethod()
    {
        var host = NewShell();
        host.Submit("class P { [int] $V = 5; [int] Double() { return $this.V * 2 } }");
        Assert.Equal(10, host.Submit("[P]::new().Double()"));
    }

    [Fact]
    public void ClassMethodWithParameters()
    {
        var host = NewShell();
        host.Submit("class Calc { [int] Add([int] $a, [int] $b) { return $a + $b } }");
        Assert.Equal(7, host.Submit("[Calc]::new().Add(3, 4)"));
    }

    [Fact]
    public void ClassIsTypeCheck()
    {
        var host = NewShell();
        host.Submit("class Foo { [int] $N = 1 }");
        host.Submit("$f = [Foo]::new()");
        Assert.Equal(true, host.Submit("$f -is [Foo]"));
    }

    [Fact]
    public void EnumMemberAccess()
    {
        var host = NewShell();
        host.Submit("enum Color { Red; Green; Blue }");
        var r = host.Submit("[Color]::Green");
        var ev = Assert.IsType<EnumValue>(r);
        Assert.Equal("Green", ev.MemberName);
        Assert.Equal(1, ev.Value);
    }

    [Fact]
    public void EnumEquality()
    {
        var host = NewShell();
        host.Submit("enum Color { Red; Green; Blue }");
        Assert.Equal(true, host.Submit("[Color]::Red -eq [Color]::Red"));
        Assert.Equal(false, host.Submit("[Color]::Red -eq [Color]::Green"));
    }

    [Fact]
    public void EnumCastFromInt()
    {
        var host = NewShell();
        host.Submit("enum Color { Red; Green; Blue }");
        var r = host.Submit("[Color] 2");
        var ev = Assert.IsType<EnumValue>(r);
        Assert.Equal("Blue", ev.MemberName);
    }

    [Fact]
    public void EnumExplicitValue()
    {
        var host = NewShell();
        host.Submit("enum Status { Active = 1; Inactive = 2; Pending = 5 }");
        var r = host.Submit("[Status]::Pending");
        Assert.Equal(5, ((EnumValue)r!).Value);
    }
}
