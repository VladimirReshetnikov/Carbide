using System.Collections.Specialized;
using CarbidePwsh.Host;
using Xunit;

namespace CarbidePwsh.Tests;

public class CmdletTests
{
    private static ShellHost NewShell() => new ShellHost();

    [Fact]
    public void WriteOutputForwardsInput()
    {
        var r = NewShell().Submit("@(1,2,3) | Write-Output");
        Assert.Equal(new object?[] { 1, 2, 3 }, (object?[])r!);
    }

    [Fact]
    public void GetContentReadsLines()
    {
        var host = NewShell();
        host.Submit("Set-Content /tmp/file.txt -Value 'line1'");
        var r = host.Submit("Get-Content /tmp/file.txt");
        Assert.Equal("line1", r);
    }

    [Fact]
    public void SetContentThenGetContentMultipleLines()
    {
        var host = NewShell();
        host.Submit("@('a','b','c') | Set-Content /tmp/m.txt");
        var r = host.Submit("Get-Content /tmp/m.txt");
        Assert.Equal(new object?[] { "a", "b", "c" }, (object?[])r!);
    }

    [Fact]
    public void NewItemDirectoryCreates()
    {
        var host = NewShell();
        host.Submit("New-Item -ItemType Directory /new/sub");
        host.Submit("Set-Location /new/sub");
        Assert.Equal("/new/sub", host.Submit("Get-Location"));
    }

    [Fact]
    public void TestPathContainerVsLeaf()
    {
        var host = NewShell();
        host.Submit("New-Item -ItemType Directory /a");
        host.Submit("Set-Content /b.txt -Value x");
        Assert.Equal(true, host.Submit("Test-Path /a -PathType Container"));
        Assert.Equal(false, host.Submit("Test-Path /a -PathType Leaf"));
        Assert.Equal(true, host.Submit("Test-Path /b.txt -PathType Leaf"));
    }

    [Fact]
    public void RemoveItemDeletes()
    {
        var host = NewShell();
        host.Submit("Set-Content /f.txt -Value x");
        host.Submit("Remove-Item /f.txt");
        Assert.Equal(false, host.Submit("Test-Path /f.txt"));
    }

    [Fact]
    public void CopyItemPreservesContent()
    {
        var host = NewShell();
        host.Submit("Set-Content /a.txt -Value hello");
        host.Submit("Copy-Item /a.txt /b.txt");
        Assert.Equal("hello", host.Submit("Get-Content /b.txt"));
    }

    [Fact]
    public void MoveItemRenames()
    {
        var host = NewShell();
        host.Submit("Set-Content /a.txt -Value x");
        host.Submit("Move-Item /a.txt /b.txt");
        Assert.Equal(false, host.Submit("Test-Path /a.txt"));
        Assert.Equal("x", host.Submit("Get-Content /b.txt"));
    }

    [Fact]
    public void JoinPathJoins()
    {
        var host = NewShell();
        Assert.Equal("/a/b", host.Submit("Join-Path /a b"));
    }

    [Fact]
    public void SetLocationCdAlias()
    {
        var host = NewShell();
        host.Submit("New-Item -ItemType Directory /work");
        host.Submit("cd /work");
        Assert.Equal("/work", host.Submit("pwd"));
    }

    [Fact]
    public void ConvertToJsonCompact()
    {
        var host = NewShell();
        var r = host.Submit("@{ a = 1; b = 'two' } | ConvertTo-Json -Compress");
        var s = Assert.IsType<string>(r);
        Assert.Contains("\"a\":1", s);
        Assert.Contains("\"b\":\"two\"", s);
    }

    [Fact]
    public void ConvertFromJsonRoundTrip()
    {
        var host = NewShell();
        host.Submit("$j = @{ name = 'V'; x = 42 } | ConvertTo-Json -Compress");
        var r = host.Submit("$j | ConvertFrom-Json");
        var dict = Assert.IsType<OrderedDictionary>(r);
        Assert.Equal("V", dict["name"]);
        Assert.Equal(42, dict["x"]);
    }

    [Fact]
    public void PwdVariableReflectsCurrentLocation()
    {
        var host = NewShell();
        host.Submit("New-Item -ItemType Directory /area");
        host.Submit("cd /area");
        Assert.Equal("/area", host.Submit("$PWD"));
    }

    [Fact]
    public void GetChildItemEnumeratesVfs()
    {
        var host = NewShell();
        host.Submit("New-Item -ItemType File /f1.txt -Value x");
        host.Submit("New-Item -ItemType File /f2.txt -Value y");
        var r = host.Submit("Get-ChildItem /");
        var arr = (object?[])r!;
        Assert.True(arr.Length >= 2);
    }
}
