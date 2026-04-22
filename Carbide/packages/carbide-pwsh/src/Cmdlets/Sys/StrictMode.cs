namespace CarbidePwsh.Cmdlets.Sys;

/// <summary>
/// <c>Set-StrictMode</c> — in real pwsh this enforces stricter variable-reference rules
/// inside the current scope. Phase 1 carbide-pwsh is structurally strict already (undefined
/// variables yield <c>$null</c>, not errors); we accept the cmdlet as a no-op so scripts
/// that call it at top level continue to work.
/// </summary>
public sealed class SetStrictModeCommand : Cmdlet
{
    public override string Name => "Set-StrictMode";

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        // `-Version` / `-Off` are consumed but not honored.
        yield break;
    }
}

/// <summary>
/// <c>Out-Null</c> — silently consume any pipeline input. A common pwsh idiom for
/// discarding output from noisy cmdlets: <c>New-Item /tmp/x -ItemType Directory | Out-Null</c>.
/// </summary>
public sealed class OutNullCommand : Cmdlet
{
    public override string Name => "Out-Null";

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        if (input is not null)
        {
            // Drain the input without yielding anything.
            foreach (var _ in input) { }
        }
        yield break;
    }
}

/// <summary>
/// <c>Write-Warning</c> — write a warning-prefixed message to the error stream. Real pwsh
/// emits yellow; we match the common <c>WARNING:</c> prefix.
/// </summary>
public sealed class WriteWarningCommand : Cmdlet
{
    public override string Name => "Write-Warning";

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var msg = binding.GetValue<string>("Message", 0, null)
            ?? throw new Errors.PwshRuntimeException("Write-Warning requires a -Message argument.");
        context.Error.WriteLine($"\x1b[33mWARNING: {msg}\x1b[0m");
        yield break;
    }
}
