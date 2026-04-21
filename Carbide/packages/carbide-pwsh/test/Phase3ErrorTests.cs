using CarbidePwsh.Host;
using CarbidePwsh.Errors;
using CarbidePwsh.Runtime;
using Xunit;

namespace CarbidePwsh.Tests;

public class Phase3ErrorTests
{
    private static ShellHost NewShell() => new ShellHost();

    [Fact]
    public void TryCatchCatchesThrow()
    {
        Assert.Equal("caught", NewShell().Submit("try { throw 'boom' } catch { 'caught' }"));
    }

    [Fact]
    public void CatchAccessErrorObject()
    {
        Assert.Equal("boom", NewShell().Submit("try { throw 'boom' } catch { $_.Exception.Message }"));
    }

    [Fact]
    public void FinallyRunsAlways()
    {
        var host = NewShell();
        host.Submit("$hit = 0");
        host.Submit("try { 'ok' } catch { 'no' } finally { $hit = 1 }");
        Assert.Equal(1, host.Submit("$hit"));
    }

    [Fact]
    public void FinallyRunsAfterException()
    {
        var host = NewShell();
        host.Submit("$hit = 0");
        try { host.Submit("try { throw 'x' } finally { $hit = 1 }"); } catch { }
        Assert.Equal(1, host.Submit("$hit"));
    }

    [Fact]
    public void TypedCatchFiltersByException()
    {
        var host = NewShell();
        // ArgumentException → typed catch hits; ordinary string throw → falls through.
        var r = host.Submit(
            "try { throw [System.ArgumentException]::new('bad') } " +
            "catch [System.ArgumentException] { 'arg' } " +
            "catch { 'other' }");
        Assert.Equal("arg", r);
    }

    [Fact]
    public void QuestionMarkTrueAfterSuccess()
    {
        var host = NewShell();
        host.Submit("Get-Location");
        Assert.Equal(true, host.Submit("$?"));
    }

    [Fact]
    public void QuestionMarkFalseAfterError()
    {
        var host = NewShell();
        try { host.Submit("Get-Content /does/not/exist"); } catch { }
        Assert.Equal(false, host.Submit("$?"));
    }

    [Fact]
    public void ThrowStringWrappedInErrorRecord()
    {
        var host = NewShell();
        var r = host.Submit("try { throw 'boom' } catch { $_ -is [CarbidePwsh.Runtime.ErrorRecord] }");
        Assert.Equal(true, r);
    }
}
