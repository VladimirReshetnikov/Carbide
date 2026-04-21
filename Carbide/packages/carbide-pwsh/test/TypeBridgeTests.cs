using CarbidePwsh.Errors;
using CarbidePwsh.Runtime;
using Xunit;

namespace CarbidePwsh.Tests;

public class TypeBridgeTests
{
    private readonly TypeBridge _bridge = new();

    [Fact]
    public void ResolvesAlias()
    {
        Assert.Equal(typeof(int), _bridge.ResolveType("int", SourceLocation.None));
    }

    [Fact]
    public void ResolvesFullName()
    {
        Assert.Equal(typeof(DateTime), _bridge.ResolveType("System.DateTime", SourceLocation.None));
    }

    [Fact]
    public void ResolvesBareNameWithSystemPrefix()
    {
        Assert.Equal(typeof(DateTime), _bridge.ResolveType("DateTime", SourceLocation.None));
    }

    [Fact]
    public void UnknownTypeThrows()
    {
        Assert.Throws<PwshTypeNotFoundException>(
            () => _bridge.ResolveType("Nope.NotReal", SourceLocation.None));
    }

    [Fact]
    public void StaticPropertyGet()
    {
        var v = _bridge.GetStaticMember(typeof(Math), "PI", SourceLocation.None);
        Assert.Equal(Math.PI, v);
    }

    [Fact]
    public void StaticMethodInvocationPicksBestOverload()
    {
        var v = _bridge.InvokeStaticMethod(typeof(Math), "Max", new object?[] { 3, 5 }, SourceLocation.None);
        Assert.Equal(5, v);
    }

    [Fact]
    public void StaticMethodCoercesArguments()
    {
        // Sqrt takes double; we pass int.
        var v = _bridge.InvokeStaticMethod(typeof(Math), "Sqrt", new object?[] { 16 }, SourceLocation.None);
        Assert.Equal(4.0, v);
    }

    [Fact]
    public void UnknownMemberThrowsWithNearestMatches()
    {
        var ex = Assert.Throws<PwshMemberNotFoundException>(
            () => _bridge.GetStaticMember(typeof(Math), "Sqrty", SourceLocation.None));
        Assert.Contains("Sqrt", ex.NearestMatches);
    }

    [Fact]
    public void ConstructorViaNew()
    {
        var ctor = _bridge.GetStaticMember(typeof(System.Text.StringBuilder), "new", SourceLocation.None);
        Assert.IsType<ConstructorInvoker>(ctor);
        var instance = _bridge.InvokeStaticMethod(typeof(System.Text.StringBuilder), "new", Array.Empty<object?>(), SourceLocation.None);
        Assert.IsType<System.Text.StringBuilder>(instance);
    }

    [Fact]
    public void InstanceMethodOnString()
    {
        var v = _bridge.InvokeInstanceMethod("hello", "ToUpper", Array.Empty<object?>(), SourceLocation.None);
        Assert.Equal("HELLO", v);
    }

    [Fact]
    public void InstancePropertyOnArray()
    {
        var v = _bridge.GetInstanceMember(new[] { 1, 2, 3 }, "Length", SourceLocation.None);
        Assert.Equal(3, v);
    }

    [Fact]
    public void IndexArray()
    {
        var v = _bridge.GetIndex(new object?[] { 10, 20, 30 }, 1, SourceLocation.None);
        Assert.Equal(20, v);
    }

    [Fact]
    public void IndexArrayNegative()
    {
        var v = _bridge.GetIndex(new object?[] { 10, 20, 30 }, -1, SourceLocation.None);
        Assert.Equal(30, v);
    }

    [Fact]
    public void IndexStringReturnsChar()
    {
        var v = _bridge.GetIndex("abc", 1, SourceLocation.None);
        Assert.Equal('b', v);
    }

    [Fact]
    public void RuntimeExceptionIsUnwrapped()
    {
        var ex = Assert.Throws<PwshRuntimeException>(
            () => _bridge.InvokeStaticMethod(typeof(int), "Parse", new object?[] { "not a number" }, SourceLocation.None));
        Assert.Contains("not in a correct format", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
