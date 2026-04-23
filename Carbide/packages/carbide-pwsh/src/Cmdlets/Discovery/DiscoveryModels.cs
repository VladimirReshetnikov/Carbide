namespace CarbidePwsh.Cmdlets.Discovery;

[Flags]
public enum PwshAliasOptions
{
    None = 0,
    ReadOnly = 1,
    Constant = 2,
    Private = 4,
    AllScope = 8,
}

public sealed record BuiltinCmdletDefinition(string Name, string Source);

public sealed record BuiltinAliasDefinition(
    string Name,
    string Definition,
    PwshAliasOptions Options,
    string Source = "");

public sealed record PwshCommandInfo(
    string CommandType,
    string Name,
    string? Definition = null,
    string Source = "",
    bool IsImplemented = false);

public sealed record PwshDriveInfo(
    string Name,
    string Provider,
    string Root,
    string CurrentLocation);

public sealed record PwshProviderInfo(
    string Name,
    string Drives,
    string Home = "");

public sealed record PwshModuleInfo(
    string Name,
    bool IsImported,
    bool IsImplemented);

public sealed record PwshMemberInfo(
    string Name,
    string MemberType,
    string Definition);
