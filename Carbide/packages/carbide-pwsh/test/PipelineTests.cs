using System.Collections.Specialized;
using CarbidePwsh.Host;
using Xunit;

namespace CarbidePwsh.Tests;

public class PipelineTests
{
    private static object? Run(ShellHost host, string source) => host.Submit(source);

    private static ShellHost NewShell() => new ShellHost();

    private static object?[] AsArray(object? value)
        => value is object?[] a ? a : new[] { value };

    [Fact]
    public void WhereObjectFilters()
    {
        var r = Run(NewShell(), "@(1,2,3,4,5) | Where-Object { $_ -gt 2 }");
        Assert.Equal(new object?[] { 3, 4, 5 }, AsArray(r));
    }

    [Fact]
    public void ForEachObjectTransforms()
    {
        var r = Run(NewShell(), "@(1,2,3) | ForEach-Object { $_ * $_ }");
        Assert.Equal(new object?[] { 1, 4, 9 }, AsArray(r));
    }

    [Fact]
    public void SelectFirstN()
    {
        var r = Run(NewShell(), "@(1..10) | Select-Object -First 3");
        Assert.Equal(new object?[] { 1, 2, 3 }, AsArray(r));
    }

    [Fact]
    public void SortAscending()
    {
        var r = Run(NewShell(), "@(5,3,1,4,2) | Sort-Object");
        Assert.Equal(new object?[] { 1, 2, 3, 4, 5 }, AsArray(r));
    }

    [Fact]
    public void SortDescending()
    {
        var r = Run(NewShell(), "@(5,3,1,4,2) | Sort-Object -Descending");
        Assert.Equal(new object?[] { 5, 4, 3, 2, 1 }, AsArray(r));
    }

    [Fact]
    public void MeasureSumAndCount()
    {
        var r = Run(NewShell(), "@(1,2,3,4) | Measure-Object -Sum");
        var dict = Assert.IsType<OrderedDictionary>(r);
        Assert.Equal(4, dict["Count"]);
        Assert.Equal(10.0, dict["Sum"]);
    }

    [Fact]
    public void GroupByProperty()
    {
        var r = Run(NewShell(), "@('foo','bar','foo') | Group-Object");
        var groups = AsArray(r);
        Assert.Equal(2, groups.Length);
        Assert.Equal("foo", ((OrderedDictionary)groups[0]!)["Name"]);
        Assert.Equal(2, ((OrderedDictionary)groups[0]!)["Count"]);
    }

    [Fact]
    public void MultiStagePipeline()
    {
        var r = Run(NewShell(),
            "@(1,2,3,4,5) | Where-Object { $_ -gt 1 } | ForEach-Object { $_ * 10 } | Sort-Object -Descending");
        Assert.Equal(new object?[] { 50, 40, 30, 20 }, AsArray(r));
    }

    [Fact]
    public void PipelineInsideSubExpression()
    {
        var r = Run(NewShell(), "$x = $(@(1,2,3) | ForEach-Object { $_ + 10 }); $x");
        Assert.Equal(new object?[] { 11, 12, 13 }, AsArray(r));
    }

    [Fact]
    public void CommandArgumentCanIndexGroupedExpression()
    {
        var r = Run(NewShell(), "Write-Output (@('zero','one'))[1]");
        Assert.Equal("one", r);
    }

    [Fact]
    public void CommandArgumentCanAccessMemberOnGroupedCommandResult()
    {
        var r = Run(NewShell(), "Write-Output (Get-Date -Date '2020-01-02').Year");
        Assert.Equal(2020, r);
    }
}
