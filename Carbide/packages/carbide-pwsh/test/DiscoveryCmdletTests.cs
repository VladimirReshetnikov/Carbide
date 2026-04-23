using CarbidePwsh.Cmdlets.Discovery;
using CarbidePwsh.Errors;
using CarbidePwsh.Host;
using CarbidePwsh.Runtime;
using Xunit;

namespace CarbidePwsh.Tests;

public class DiscoveryCmdletTests
{
    private static ShellHost NewShell() => new ShellHost();

    [Fact]
    public void GetCommandFindsImplementedBuiltin()
    {
        var host = NewShell();

        var result = Assert.IsType<PwshCommandInfo>(host.Submit("Get-Command Get-Command"));
        Assert.Equal("Cmdlet", result.CommandType);
        Assert.Equal("Get-Command", result.Name);
        Assert.True(result.IsImplemented);
        Assert.Equal("Microsoft.PowerShell.Core", result.Source);
    }

    [Fact]
    public void GetCommandFindsRecognizedButUnimplementedBuiltin()
    {
        var host = NewShell();

        var result = Assert.IsType<PwshCommandInfo>(host.Submit("Get-Command Start-Process"));
        Assert.Equal("Cmdlet", result.CommandType);
        Assert.Equal("Start-Process", result.Name);
        Assert.False(result.IsImplemented);
        Assert.Equal("Microsoft.PowerShell.Management", result.Source);
    }

    [Fact]
    public void RecognizedButUnimplementedBuiltinThrowsHelpfulMessage()
    {
        var host = NewShell();

        var ex = Assert.Throws<PwshRuntimeException>(() => host.Submit("Start-Process foo"));
        Assert.Contains("recognized but not implemented", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Start-Process", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UserFunctionStillWinsOverRecognizedPlaceholderBuiltin()
    {
        var host = NewShell();

        var result = host.Submit("function Start-Process { 'mine' }; Start-Process");
        Assert.Equal("mine", result);
    }

    [Fact]
    public void GetAliasIncludesBuiltinAliasCatalog()
    {
        var host = NewShell();

        var result = Assert.IsType<PwshCommandInfo>(host.Submit("Get-Alias gcm"));
        Assert.Equal("Alias", result.CommandType);
        Assert.Equal("gcm", result.Name);
        Assert.Equal("Get-Command", result.Definition);
    }

    [Fact]
    public void BuiltinCatalogMateriallyExpandsRecognizedCounts()
    {
        var host = NewShell();

        var aliases = Assert.IsType<object?[]>(host.Submit("Get-Alias"));
        var cmdlets = Assert.IsType<object?[]>(host.Submit("Get-Command -CommandType Cmdlet"));
        Assert.True(aliases.Length >= 140);
        Assert.True(cmdlets.Length >= 324);
    }

    [Fact]
    public void SetAliasOverridesBuiltinAlias()
    {
        var host = NewShell();

        host.Submit("Set-Alias ls Get-Location");
        var result = host.Submit("ls");
        Assert.Equal("/home/user", result);
    }

    [Fact]
    public void MdAliasResolvesThroughBuiltinAliasChain()
    {
        var host = NewShell();

        host.Submit("md /scratch");
        Assert.Equal(true, host.Submit("Test-Path /scratch -PathType Container"));
    }

    [Fact]
    public void RmdirAliasResolvesToRemoveItem()
    {
        var host = NewShell();

        host.Submit("mkdir /gone");
        host.Submit("rmdir /gone");
        Assert.Equal(false, host.Submit("Test-Path /gone"));
    }

    [Fact]
    public void VariableCmdletsRoundTrip()
    {
        var host = NewShell();

        host.Submit("Set-Variable answer 42");
        var item = Assert.IsType<PwshProviderItem>(host.Submit("Get-Variable answer"));
        Assert.Equal("answer", item.Name);
        Assert.Equal(42, item.Value);
    }

    [Fact]
    public void PushAndPopLocationRoundTrip()
    {
        var host = NewShell();

        host.Submit("mkdir /stack-a");
        host.Submit("mkdir /stack-b");
        host.Submit("Set-Location /stack-a");
        host.Submit("Push-Location /stack-b");
        Assert.Equal("/stack-b", host.Submit("Get-Location"));
        host.Submit("Pop-Location");
        Assert.Equal("/stack-a", host.Submit("Get-Location"));
    }

    [Fact]
    public void GetPsDriveReturnsBuiltinProviders()
    {
        var host = NewShell();

        var result = Assert.IsType<object?[]>(host.Submit("Get-PSDrive"));
        var drives = result.Cast<PwshDriveInfo>().ToArray();
        Assert.Contains(drives, static drive => drive.Name == "Alias");
        Assert.Contains(drives, static drive => drive.Name == "Env");
        Assert.Contains(drives, static drive => drive.Name == "/");
    }

    [Fact]
    public void GetModuleFindsBuiltinModule()
    {
        var host = NewShell();

        var result = Assert.IsType<PwshModuleInfo>(host.Submit("Get-Module Microsoft.PowerShell.Utility"));
        Assert.Equal("Microsoft.PowerShell.Utility", result.Name);
        Assert.True(result.IsImported);
    }

    [Fact]
    public void GetHelpSummarizesBuiltinStatus()
    {
        var host = NewShell();

        var result = Assert.IsType<string>(host.Submit("Get-Help Start-Process"));
        Assert.Contains("Start-Process", result, StringComparison.Ordinal);
        Assert.Contains("not implemented", result, StringComparison.OrdinalIgnoreCase);
    }
}
