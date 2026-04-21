using System.IO;
using System.Text;
using CarbidePwsh.Host;
using Xunit;

namespace CarbidePwsh.Tests;

public class Phase3ScriptAppTests
{
    private static ShellHost NewShell() => new ShellHost();

    [Fact]
    public void ScriptExecutesFromVfs()
    {
        var host = NewShell();
        host.Submit("Set-Content /tmp/hello.ps1 -Value \"'hi from script'\"");
        var capturedOut = new StringBuilder();
        var saved = Console.Out;
        Console.SetOut(new StringWriter(capturedOut));
        try
        {
            var r = host.Submit("/tmp/hello.ps1");
            Assert.Equal("hi from script", r);
        }
        finally
        {
            Console.SetOut(saved);
        }
    }

    [Fact]
    public void DotSourceInjectsVariables()
    {
        var host = NewShell();
        host.Submit("Set-Content /tmp/init.ps1 -Value '$shared = 42'");
        host.Submit(". /tmp/init.ps1");
        Assert.Equal(42, host.Submit("$shared"));
    }

    [Fact]
    public void ScriptReceivesArgsVariable()
    {
        var host = NewShell();
        host.Submit("Set-Content /tmp/args.ps1 -Value '$args[0] + \":\" + $args[1]'");
        Assert.Equal("hello:world", host.Submit("/tmp/args.ps1 hello world"));
    }

    [Fact]
    public void StartSleepDoesNotThrow()
    {
        NewShell().Submit("Start-Sleep -Milliseconds 1");
    }

    [Fact]
    public void GetDateReturnsDateTime()
    {
        var r = NewShell().Submit("Get-Date");
        Assert.IsType<DateTime>(r);
    }

    [Fact]
    public void NewGuidReturnsGuid()
    {
        var r = NewShell().Submit("New-Guid");
        Assert.IsType<Guid>(r);
    }

    [Fact]
    public void GetRandomInRange()
    {
        var host = NewShell();
        for (int i = 0; i < 10; i++)
        {
            var r = (int)host.Submit("Get-Random -Minimum 0 -Maximum 10")!;
            Assert.InRange(r, 0, 9);
        }
    }

    [Fact]
    public void InvokeExpressionRuns()
    {
        Assert.Equal(4, NewShell().Submit("Invoke-Expression '2 + 2'"));
    }
}
