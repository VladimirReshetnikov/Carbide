namespace CarbidePwsh.Cmdlets.Discovery;

/// <summary>
/// Snapshot of the stock PowerShell 7.6 builtin cmdlet and alias surface, captured from a
/// local <c>pwsh -NoProfile</c> session on Windows and curated down to the
/// <c>Microsoft.PowerShell.*</c> cmdlet sources plus the default alias table. carbide-pwsh
/// uses this for command discovery parity even when a given builtin is not implemented yet.
/// </summary>
public static class BuiltinCommandCatalog
{
    private static readonly Dictionary<string, BuiltinCmdletDefinition> s_cmdlets = BuildCmdlets();
    private static readonly Dictionary<string, BuiltinAliasDefinition> s_aliases = BuildAliases();
    private static readonly string[] s_moduleNames = s_cmdlets.Values
        .Select(static c => c.Source)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(static s => s, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static IEnumerable<BuiltinCmdletDefinition> Cmdlets => s_cmdlets.Values;
    public static IEnumerable<BuiltinAliasDefinition> Aliases => s_aliases.Values;
    public static IReadOnlyList<string> ModuleNames => s_moduleNames;

    public static bool TryGetCmdlet(string name, out BuiltinCmdletDefinition definition)
        => s_cmdlets.TryGetValue(name, out definition!);

    public static bool TryGetAlias(string name, out BuiltinAliasDefinition definition)
        => s_aliases.TryGetValue(name, out definition!);

    private static Dictionary<string, BuiltinCmdletDefinition> BuildCmdlets()
    {
        var map = new Dictionary<string, BuiltinCmdletDefinition>(StringComparer.OrdinalIgnoreCase);

        void AddCmdlets(string source, IEnumerable<string> names)
        {
            foreach (var name in names)
                map[name] = new BuiltinCmdletDefinition(name, source);
        }

        AddCmdlets("Microsoft.PowerShell.Core", new[]
        {
            "Add-History",
            "Clear-History",
            "Connect-PSSession",
            "Debug-Job",
            "Disable-ExperimentalFeature",
            "Disable-PSRemoting",
            "Disable-PSSessionConfiguration",
            "Disconnect-PSSession",
            "Enable-ExperimentalFeature",
            "Enable-PSRemoting",
            "Enable-PSSessionConfiguration",
            "Enter-PSHostProcess",
            "Enter-PSSession",
            "Exit-PSHostProcess",
            "Exit-PSSession",
            "Export-ModuleMember",
            "ForEach-Object",
            "Get-Command",
            "Get-ExperimentalFeature",
            "Get-Help",
            "Get-History",
            "Get-Job",
            "Get-Module",
            "Get-PSHostProcessInfo",
            "Get-PSSession",
            "Get-PSSessionCapability",
            "Get-PSSessionConfiguration",
            "Get-PSSubsystem",
            "Import-Module",
            "Invoke-Command",
            "Invoke-History",
            "New-Module",
            "New-ModuleManifest",
            "New-PSRoleCapabilityFile",
            "New-PSSession",
            "New-PSSessionConfigurationFile",
            "New-PSSessionOption",
            "New-PSTransportOption",
            "Out-Default",
            "Out-Host",
            "Out-Null",
            "Receive-Job",
            "Receive-PSSession",
            "Register-ArgumentCompleter",
            "Register-PSSessionConfiguration",
            "Remove-Job",
            "Remove-Module",
            "Remove-PSSession",
            "Save-Help",
            "Set-PSDebug",
            "Set-PSSessionConfiguration",
            "Set-StrictMode",
            "Start-Job",
            "Stop-Job",
            "Test-ModuleManifest",
            "Test-PSSessionConfigurationFile",
            "Unregister-PSSessionConfiguration",
            "Update-Help",
            "Wait-Job",
            "Where-Object",
        });

        AddCmdlets("Microsoft.PowerShell.Diagnostics", new[]
        {
            "Export-Counter",
            "Get-Counter",
            "Get-WinEvent",
            "Import-Counter",
            "New-WinEvent",
        });

        AddCmdlets("Microsoft.PowerShell.Host", new[]
        {
            "Start-Transcript",
            "Stop-Transcript",
        });

        AddCmdlets("Microsoft.PowerShell.LocalAccounts", new[]
        {
            "Add-LocalGroupMember",
            "Disable-LocalUser",
            "Enable-LocalUser",
            "Get-LocalGroup",
            "Get-LocalGroupMember",
            "Get-LocalUser",
            "New-LocalGroup",
            "New-LocalUser",
            "Remove-LocalGroup",
            "Remove-LocalGroupMember",
            "Remove-LocalUser",
            "Rename-LocalGroup",
            "Rename-LocalUser",
            "Set-LocalGroup",
            "Set-LocalUser",
        });

        AddCmdlets("Microsoft.PowerShell.Management", new[]
        {
            "Add-Computer",
            "Add-Content",
            "Checkpoint-Computer",
            "Clear-Content",
            "Clear-EventLog",
            "Clear-Item",
            "Clear-ItemProperty",
            "Clear-RecycleBin",
            "Complete-Transaction",
            "Convert-Path",
            "Copy-Item",
            "Copy-ItemProperty",
            "Debug-Process",
            "Disable-ComputerRestore",
            "Enable-ComputerRestore",
            "Get-ChildItem",
            "Get-Clipboard",
            "Get-ComputerInfo",
            "Get-ComputerRestorePoint",
            "Get-Content",
            "Get-ControlPanelItem",
            "Get-EventLog",
            "Get-HotFix",
            "Get-Item",
            "Get-ItemProperty",
            "Get-ItemPropertyValue",
            "Get-Location",
            "Get-PSDrive",
            "Get-PSProvider",
            "Get-Process",
            "Get-Service",
            "Get-TimeZone",
            "Get-Transaction",
            "Get-WmiObject",
            "Invoke-Item",
            "Invoke-WmiMethod",
            "Join-Path",
            "Limit-EventLog",
            "Move-Item",
            "Move-ItemProperty",
            "New-EventLog",
            "New-Item",
            "New-ItemProperty",
            "New-PSDrive",
            "New-Service",
            "New-WebServiceProxy",
            "Pop-Location",
            "Push-Location",
            "Register-WmiEvent",
            "Remove-Computer",
            "Remove-EventLog",
            "Remove-Item",
            "Remove-ItemProperty",
            "Remove-PSDrive",
            "Remove-Service",
            "Remove-WmiObject",
            "Rename-Computer",
            "Rename-Item",
            "Rename-ItemProperty",
            "Reset-ComputerMachinePassword",
            "Resolve-Path",
            "Restart-Computer",
            "Restart-Service",
            "Restore-Computer",
            "Resume-Service",
            "Set-Clipboard",
            "Set-Content",
            "Set-Item",
            "Set-ItemProperty",
            "Set-Location",
            "Set-Service",
            "Set-TimeZone",
            "Set-WmiInstance",
            "Show-ControlPanelItem",
            "Show-EventLog",
            "Split-Path",
            "Start-Process",
            "Start-Service",
            "Start-Transaction",
            "Stop-Computer",
            "Stop-Process",
            "Stop-Service",
            "Suspend-Service",
            "Test-ComputerSecureChannel",
            "Test-Connection",
            "Test-Path",
            "Undo-Transaction",
            "Use-Transaction",
            "Wait-Process",
            "Write-EventLog",
        });

        AddCmdlets("Microsoft.PowerShell.PSResourceGet", new[]
        {
            "Compress-PSResource",
            "Find-PSResource",
            "Get-InstalledPSResource",
            "Get-PSResourceRepository",
            "Get-PSScriptFileInfo",
            "Install-PSResource",
            "New-PSScriptFileInfo",
            "Publish-PSResource",
            "Register-PSResourceRepository",
            "Reset-PSResourceRepository",
            "Save-PSResource",
            "Set-PSResourceRepository",
            "Test-PSScriptFileInfo",
            "Uninstall-PSResource",
            "Unregister-PSResourceRepository",
            "Update-PSModuleManifest",
            "Update-PSResource",
            "Update-PSScriptFileInfo",
        });

        AddCmdlets("Microsoft.PowerShell.Security", new[]
        {
            "ConvertFrom-SecureString",
            "ConvertTo-SecureString",
            "Get-Acl",
            "Get-AuthenticodeSignature",
            "Get-CmsMessage",
            "Get-Credential",
            "Get-ExecutionPolicy",
            "Get-PfxCertificate",
            "New-FileCatalog",
            "Protect-CmsMessage",
            "Set-Acl",
            "Set-AuthenticodeSignature",
            "Set-ExecutionPolicy",
            "Test-FileCatalog",
            "Unprotect-CmsMessage",
        });

        AddCmdlets("Microsoft.PowerShell.ThreadJob", new[]
        {
            "Start-ThreadJob",
        });

        AddCmdlets("Microsoft.PowerShell.Utility", new[]
        {
            "Add-Member",
            "Add-Type",
            "Clear-Variable",
            "Compare-Object",
            "ConvertFrom-CliXml",
            "ConvertFrom-Csv",
            "ConvertFrom-Json",
            "ConvertFrom-Markdown",
            "ConvertFrom-SddlString",
            "ConvertFrom-StringData",
            "ConvertTo-CliXml",
            "ConvertTo-Csv",
            "ConvertTo-Html",
            "ConvertTo-Json",
            "ConvertTo-Xml",
            "Debug-Runspace",
            "Disable-PSBreakpoint",
            "Disable-RunspaceDebug",
            "Enable-PSBreakpoint",
            "Enable-RunspaceDebug",
            "Export-Alias",
            "Export-Clixml",
            "Export-Csv",
            "Export-FormatData",
            "Export-PSSession",
            "Format-Custom",
            "Format-Hex",
            "Format-List",
            "Format-Table",
            "Format-Wide",
            "Get-Alias",
            "Get-Culture",
            "Get-Date",
            "Get-Error",
            "Get-Event",
            "Get-EventSubscriber",
            "Get-FileHash",
            "Get-FormatData",
            "Get-Host",
            "Get-MarkdownOption",
            "Get-Member",
            "Get-PSBreakpoint",
            "Get-PSCallStack",
            "Get-Random",
            "Get-Runspace",
            "Get-RunspaceDebug",
            "Get-SecureRandom",
            "Get-TraceSource",
            "Get-TypeData",
            "Get-UICulture",
            "Get-Unique",
            "Get-Uptime",
            "Get-Variable",
            "Get-Verb",
            "Group-Object",
            "Import-Alias",
            "Import-Clixml",
            "Import-Csv",
            "Import-LocalizedData",
            "Import-PSSession",
            "Import-PowerShellDataFile",
            "Invoke-Expression",
            "Invoke-RestMethod",
            "Invoke-WebRequest",
            "Join-String",
            "Measure-Command",
            "Measure-Object",
            "New-Alias",
            "New-Event",
            "New-Guid",
            "New-Object",
            "New-TemporaryFile",
            "New-TimeSpan",
            "New-Variable",
            "Out-File",
            "Out-GridView",
            "Out-Printer",
            "Out-String",
            "Read-Host",
            "Register-EngineEvent",
            "Register-ObjectEvent",
            "Remove-Alias",
            "Remove-Event",
            "Remove-PSBreakpoint",
            "Remove-TypeData",
            "Remove-Variable",
            "Select-Object",
            "Select-String",
            "Select-Xml",
            "Send-MailMessage",
            "Set-Alias",
            "Set-Date",
            "Set-MarkdownOption",
            "Set-PSBreakpoint",
            "Set-TraceSource",
            "Set-Variable",
            "Show-Command",
            "Show-Markdown",
            "Sort-Object",
            "Start-Sleep",
            "Tee-Object",
            "Test-Json",
            "Trace-Command",
            "Unblock-File",
            "Unregister-Event",
            "Update-FormatData",
            "Update-List",
            "Update-TypeData",
            "Wait-Debugger",
            "Wait-Event",
            "Write-Debug",
            "Write-Error",
            "Write-Host",
            "Write-Information",
            "Write-Output",
            "Write-Progress",
            "Write-Verbose",
            "Write-Warning",
        });

        return map;
    }

    private static Dictionary<string, BuiltinAliasDefinition> BuildAliases()
    {
        var map = new Dictionary<string, BuiltinAliasDefinition>(StringComparer.OrdinalIgnoreCase);

        void AddAlias(string name, string definition, PwshAliasOptions options, string source = "")
            => map[name] = new BuiltinAliasDefinition(name, definition, options, source);

        AddAlias("?", "Where-Object", (PwshAliasOptions)9, "");
        AddAlias("%", "ForEach-Object", (PwshAliasOptions)9, "");
        AddAlias("ac", "Add-Content", (PwshAliasOptions)1, "");
        AddAlias("cat", "Get-Content", (PwshAliasOptions)0, "");
        AddAlias("cd", "Set-Location", (PwshAliasOptions)8, "");
        AddAlias("chdir", "Set-Location", (PwshAliasOptions)0, "");
        AddAlias("clc", "Clear-Content", (PwshAliasOptions)1, "");
        AddAlias("clear", "Clear-Host", (PwshAliasOptions)0, "");
        AddAlias("clhy", "Clear-History", (PwshAliasOptions)1, "");
        AddAlias("cli", "Clear-Item", (PwshAliasOptions)1, "");
        AddAlias("clp", "Clear-ItemProperty", (PwshAliasOptions)1, "");
        AddAlias("cls", "Clear-Host", (PwshAliasOptions)0, "");
        AddAlias("clv", "Clear-Variable", (PwshAliasOptions)1, "");
        AddAlias("cnsn", "Connect-PSSession", (PwshAliasOptions)1, "");
        AddAlias("compare", "Compare-Object", (PwshAliasOptions)1, "");
        AddAlias("copy", "Copy-Item", (PwshAliasOptions)8, "");
        AddAlias("cp", "Copy-Item", (PwshAliasOptions)8, "");
        AddAlias("cpi", "Copy-Item", (PwshAliasOptions)1, "");
        AddAlias("cpp", "Copy-ItemProperty", (PwshAliasOptions)1, "");
        AddAlias("cvpa", "Convert-Path", (PwshAliasOptions)1, "");
        AddAlias("dbp", "Disable-PSBreakpoint", (PwshAliasOptions)1, "");
        AddAlias("del", "Remove-Item", (PwshAliasOptions)8, "");
        AddAlias("diff", "Compare-Object", (PwshAliasOptions)1, "");
        AddAlias("dir", "Get-ChildItem", (PwshAliasOptions)8, "");
        AddAlias("dnsn", "Disconnect-PSSession", (PwshAliasOptions)1, "");
        AddAlias("ebp", "Enable-PSBreakpoint", (PwshAliasOptions)1, "");
        AddAlias("echo", "Write-Output", (PwshAliasOptions)8, "");
        AddAlias("epal", "Export-Alias", (PwshAliasOptions)1, "");
        AddAlias("epcsv", "Export-Csv", (PwshAliasOptions)1, "");
        AddAlias("erase", "Remove-Item", (PwshAliasOptions)0, "");
        AddAlias("etsn", "Enter-PSSession", (PwshAliasOptions)0, "");
        AddAlias("exsn", "Exit-PSSession", (PwshAliasOptions)0, "");
        AddAlias("fc", "Format-Custom", (PwshAliasOptions)1, "");
        AddAlias("fhx", "Format-Hex", (PwshAliasOptions)0, "Microsoft.PowerShell.Utility");
        AddAlias("fl", "Format-List", (PwshAliasOptions)1, "");
        AddAlias("foreach", "ForEach-Object", (PwshAliasOptions)9, "");
        AddAlias("ft", "Format-Table", (PwshAliasOptions)1, "");
        AddAlias("fw", "Format-Wide", (PwshAliasOptions)1, "");
        AddAlias("gal", "Get-Alias", (PwshAliasOptions)1, "");
        AddAlias("gbp", "Get-PSBreakpoint", (PwshAliasOptions)1, "");
        AddAlias("gc", "Get-Content", (PwshAliasOptions)1, "");
        AddAlias("gcb", "Get-Clipboard", (PwshAliasOptions)0, "Microsoft.PowerShell.Management");
        AddAlias("gci", "Get-ChildItem", (PwshAliasOptions)1, "");
        AddAlias("gcm", "Get-Command", (PwshAliasOptions)1, "");
        AddAlias("gcs", "Get-PSCallStack", (PwshAliasOptions)1, "");
        AddAlias("gdr", "Get-PSDrive", (PwshAliasOptions)1, "");
        AddAlias("gerr", "Get-Error", (PwshAliasOptions)1, "");
        AddAlias("ghy", "Get-History", (PwshAliasOptions)1, "");
        AddAlias("gi", "Get-Item", (PwshAliasOptions)1, "");
        AddAlias("gin", "Get-ComputerInfo", (PwshAliasOptions)0, "Microsoft.PowerShell.Management");
        AddAlias("gjb", "Get-Job", (PwshAliasOptions)0, "");
        AddAlias("gl", "Get-Location", (PwshAliasOptions)1, "");
        AddAlias("gm", "Get-Member", (PwshAliasOptions)1, "");
        AddAlias("gmo", "Get-Module", (PwshAliasOptions)1, "");
        AddAlias("gp", "Get-ItemProperty", (PwshAliasOptions)1, "");
        AddAlias("gps", "Get-Process", (PwshAliasOptions)1, "");
        AddAlias("gpv", "Get-ItemPropertyValue", (PwshAliasOptions)1, "");
        AddAlias("group", "Group-Object", (PwshAliasOptions)1, "");
        AddAlias("gsn", "Get-PSSession", (PwshAliasOptions)0, "");
        AddAlias("gsv", "Get-Service", (PwshAliasOptions)1, "");
        AddAlias("gtz", "Get-TimeZone", (PwshAliasOptions)0, "Microsoft.PowerShell.Management");
        AddAlias("gu", "Get-Unique", (PwshAliasOptions)1, "");
        AddAlias("gv", "Get-Variable", (PwshAliasOptions)1, "");
        AddAlias("h", "Get-History", (PwshAliasOptions)0, "");
        AddAlias("history", "Get-History", (PwshAliasOptions)0, "");
        AddAlias("icm", "Invoke-Command", (PwshAliasOptions)0, "");
        AddAlias("iex", "Invoke-Expression", (PwshAliasOptions)1, "");
        AddAlias("ihy", "Invoke-History", (PwshAliasOptions)1, "");
        AddAlias("ii", "Invoke-Item", (PwshAliasOptions)1, "");
        AddAlias("ipal", "Import-Alias", (PwshAliasOptions)1, "");
        AddAlias("ipcsv", "Import-Csv", (PwshAliasOptions)1, "");
        AddAlias("ipmo", "Import-Module", (PwshAliasOptions)1, "");
        AddAlias("irm", "Invoke-RestMethod", (PwshAliasOptions)1, "");
        AddAlias("iwr", "Invoke-WebRequest", (PwshAliasOptions)1, "");
        AddAlias("kill", "Stop-Process", (PwshAliasOptions)0, "");
        AddAlias("ls", "Get-ChildItem", (PwshAliasOptions)0, "");
        AddAlias("man", "help", (PwshAliasOptions)0, "");
        AddAlias("md", "mkdir", (PwshAliasOptions)8, "");
        AddAlias("measure", "Measure-Object", (PwshAliasOptions)1, "");
        AddAlias("mi", "Move-Item", (PwshAliasOptions)1, "");
        AddAlias("mount", "New-PSDrive", (PwshAliasOptions)0, "");
        AddAlias("move", "Move-Item", (PwshAliasOptions)8, "");
        AddAlias("mp", "Move-ItemProperty", (PwshAliasOptions)1, "");
        AddAlias("mv", "Move-Item", (PwshAliasOptions)0, "");
        AddAlias("nal", "New-Alias", (PwshAliasOptions)1, "");
        AddAlias("ndr", "New-PSDrive", (PwshAliasOptions)1, "");
        AddAlias("ni", "New-Item", (PwshAliasOptions)1, "");
        AddAlias("nmo", "New-Module", (PwshAliasOptions)1, "");
        AddAlias("nsn", "New-PSSession", (PwshAliasOptions)0, "");
        AddAlias("nv", "New-Variable", (PwshAliasOptions)1, "");
        AddAlias("ogv", "Out-GridView", (PwshAliasOptions)1, "");
        AddAlias("oh", "Out-Host", (PwshAliasOptions)1, "");
        AddAlias("popd", "Pop-Location", (PwshAliasOptions)8, "");
        AddAlias("ps", "Get-Process", (PwshAliasOptions)0, "");
        AddAlias("pushd", "Push-Location", (PwshAliasOptions)8, "");
        AddAlias("pwd", "Get-Location", (PwshAliasOptions)0, "");
        AddAlias("r", "Invoke-History", (PwshAliasOptions)0, "");
        AddAlias("rbp", "Remove-PSBreakpoint", (PwshAliasOptions)1, "");
        AddAlias("rcjb", "Receive-Job", (PwshAliasOptions)0, "");
        AddAlias("rcsn", "Receive-PSSession", (PwshAliasOptions)1, "");
        AddAlias("rd", "Remove-Item", (PwshAliasOptions)0, "");
        AddAlias("rdr", "Remove-PSDrive", (PwshAliasOptions)1, "");
        AddAlias("ren", "Rename-Item", (PwshAliasOptions)0, "");
        AddAlias("ri", "Remove-Item", (PwshAliasOptions)1, "");
        AddAlias("rjb", "Remove-Job", (PwshAliasOptions)0, "");
        AddAlias("rm", "Remove-Item", (PwshAliasOptions)0, "");
        AddAlias("rmdir", "Remove-Item", (PwshAliasOptions)0, "");
        AddAlias("rmo", "Remove-Module", (PwshAliasOptions)1, "");
        AddAlias("rni", "Rename-Item", (PwshAliasOptions)1, "");
        AddAlias("rnp", "Rename-ItemProperty", (PwshAliasOptions)1, "");
        AddAlias("rp", "Remove-ItemProperty", (PwshAliasOptions)1, "");
        AddAlias("rsn", "Remove-PSSession", (PwshAliasOptions)0, "");
        AddAlias("rv", "Remove-Variable", (PwshAliasOptions)1, "");
        AddAlias("rvpa", "Resolve-Path", (PwshAliasOptions)1, "");
        AddAlias("sajb", "Start-Job", (PwshAliasOptions)0, "");
        AddAlias("sal", "Set-Alias", (PwshAliasOptions)1, "");
        AddAlias("saps", "Start-Process", (PwshAliasOptions)1, "");
        AddAlias("sasv", "Start-Service", (PwshAliasOptions)1, "");
        AddAlias("sbp", "Set-PSBreakpoint", (PwshAliasOptions)1, "");
        AddAlias("scb", "Set-Clipboard", (PwshAliasOptions)0, "Microsoft.PowerShell.Management");
        AddAlias("select", "Select-Object", (PwshAliasOptions)9, "");
        AddAlias("set", "Set-Variable", (PwshAliasOptions)0, "");
        AddAlias("shcm", "Show-Command", (PwshAliasOptions)1, "");
        AddAlias("si", "Set-Item", (PwshAliasOptions)1, "");
        AddAlias("sl", "Set-Location", (PwshAliasOptions)1, "");
        AddAlias("sleep", "Start-Sleep", (PwshAliasOptions)1, "");
        AddAlias("sls", "Select-String", (PwshAliasOptions)0, "");
        AddAlias("sort", "Sort-Object", (PwshAliasOptions)1, "");
        AddAlias("sp", "Set-ItemProperty", (PwshAliasOptions)1, "");
        AddAlias("spjb", "Stop-Job", (PwshAliasOptions)0, "");
        AddAlias("spps", "Stop-Process", (PwshAliasOptions)1, "");
        AddAlias("spsv", "Stop-Service", (PwshAliasOptions)1, "");
        AddAlias("start", "Start-Process", (PwshAliasOptions)1, "");
        AddAlias("stz", "Set-TimeZone", (PwshAliasOptions)0, "Microsoft.PowerShell.Management");
        AddAlias("sv", "Set-Variable", (PwshAliasOptions)1, "");
        AddAlias("tee", "Tee-Object", (PwshAliasOptions)1, "");
        AddAlias("type", "Get-Content", (PwshAliasOptions)0, "");
        AddAlias("where", "Where-Object", (PwshAliasOptions)9, "");
        AddAlias("wjb", "Wait-Job", (PwshAliasOptions)0, "");
        AddAlias("write", "Write-Output", (PwshAliasOptions)1, "");

        return map;
    }
}
