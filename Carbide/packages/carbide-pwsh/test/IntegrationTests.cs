using System.Text;
using CarbidePwsh.Host;
using CarbidePwsh.SharedMultishell;
using Xunit;

namespace CarbidePwsh.Tests;

public class IntegrationTests
{
    [Fact]
    public void ExitGateScriptProducesHelloVladimir()
    {
        var host = new ShellHost();

        var savedOut = Console.Out;
        var capturedOut = new StringBuilder();
        Console.SetOut(new StringWriter(capturedOut));
        try
        {
            host.Submit("Set-Location /tmp");
            host.Submit("@{ name = 'Vladimir'; langs = @('C#', 'PowerShell', 'TypeScript') } | ConvertTo-Json | Set-Content profile.json");
            var last = host.Submit("Get-Content profile.json | ConvertFrom-Json | ForEach-Object { \"Hello, $($_.name)!\" }");
            Assert.Equal("Hello, Vladimir!", last);
        }
        finally
        {
            Console.SetOut(savedOut);
        }
    }

    [Fact]
    public void PhaseOneRegressionExpressionEvaluation()
    {
        var host = new ShellHost();
        Assert.Equal(4, host.Submit("2 + 2"));
        Assert.Equal(Math.Sqrt(2), host.Submit("[System.Math]::Sqrt(2)"));
        Assert.Equal(43, host.Submit("[int]'42' + 1"));
    }

    [Fact]
    public void PwdPersistsAcrossSubmissions()
    {
        var host = new ShellHost();
        host.Submit("New-Item -ItemType Directory /zone");
        host.Submit("Set-Location /zone");
        Assert.Equal("/zone", host.Submit("$PWD"));
        // Variable assignment persists too.
        host.Submit("$flag = 7");
        Assert.Equal(7, host.Submit("$flag"));
    }

    [Fact]
    public void BareWordArgumentConsumedAsPath()
    {
        var host = new ShellHost();
        host.Submit("New-Item -ItemType Directory /dirname");
        host.Submit("Set-Location /dirname");
        // `foo.json` is a bare-word path.
        host.Submit("Set-Content foo.json -Value hi");
        Assert.Equal("hi", host.Submit("Get-Content foo.json"));
    }

    [Fact]
    public void PipelineExpressionStage()
    {
        var host = new ShellHost();
        var r = host.Submit("1..5 | Where-Object { $_ -gt 2 }");
        Assert.Equal(new object?[] { 3, 4, 5 }, (object?[])r!);
    }

    [Fact]
    public void NativeVirtualExecutablesReceiveSingleDashArguments()
    {
        var host = new MultishellSession().Pwsh;

        Assert.Equal("Python 3-compatible Carbide subset", host.Submit("python -V"));
        Assert.Equal("value", host.Submit("python -c \"import sys; print(sys.argv[1])\" value"));
    }
}
