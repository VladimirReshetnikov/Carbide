using CarbidePwsh.Errors;
using CarbidePwsh.Runtime;

namespace CarbidePwsh.Cmdlets.Discovery;

public sealed class NewAliasCommand : Cmdlet
{
    public override string Name => "New-Alias";
    public override IEnumerable<string> Aliases => new[] { "nal" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var registry = context.Interpreter.Registry
            ?? throw new PwshRuntimeException("No cmdlet registry configured.");
        var name = binding.GetValue<string>("Name", 0, null)
            ?? throw new PwshRuntimeException("New-Alias requires a -Name argument.");
        var value = binding.GetValue<string>("Value", 1, null)
            ?? throw new PwshRuntimeException("New-Alias requires a -Value argument.");

        if (!binding.HasSwitch("Force") && registry.TryGetAliasDefinition(name, out _))
            throw new PwshRuntimeException($"Alias '{name}' already exists.");

        registry.SetAlias(name, value, SessionStateCommandHelpers.ParseAliasOptions(binding.GetValue<string>("Option", -1, null)));
        if (binding.HasSwitch("PassThru"))
            yield return new PwshCommandInfo("Alias", name, value, IsImplemented: DiscoveryHelpers.IsImplementedCommand(registry.ResolveAliasChain(name), registry));
    }
}

public sealed class SetAliasCommand : Cmdlet
{
    public override string Name => "Set-Alias";
    public override IEnumerable<string> Aliases => new[] { "sal" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var registry = context.Interpreter.Registry
            ?? throw new PwshRuntimeException("No cmdlet registry configured.");
        var name = binding.GetValue<string>("Name", 0, null)
            ?? throw new PwshRuntimeException("Set-Alias requires a -Name argument.");
        var value = binding.GetValue<string>("Value", 1, null)
            ?? throw new PwshRuntimeException("Set-Alias requires a -Value argument.");

        registry.SetAlias(name, value, SessionStateCommandHelpers.ParseAliasOptions(binding.GetValue<string>("Option", -1, null)));
        if (binding.HasSwitch("PassThru"))
            yield return new PwshCommandInfo("Alias", name, value, IsImplemented: DiscoveryHelpers.IsImplementedCommand(registry.ResolveAliasChain(name), registry));
    }
}

public sealed class RemoveAliasCommand : Cmdlet
{
    public override string Name => "Remove-Alias";

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var registry = context.Interpreter.Registry
            ?? throw new PwshRuntimeException("No cmdlet registry configured.");
        var namePatterns = DiscoveryHelpers.GetPatterns(binding, "Name", 0);

        foreach (var alias in registry.AliasDefinitions
                     .Where(alias => WildcardPattern.IsMatchAny(alias.Name, namePatterns))
                     .Select(static alias => alias.Name)
                     .ToArray())
        {
            registry.RemoveAlias(alias);
        }

        yield break;
    }
}

public sealed class GetVariableCommand : Cmdlet
{
    public override string Name => "Get-Variable";
    public override IEnumerable<string> Aliases => new[] { "gv" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var patterns = DiscoveryHelpers.GetPatterns(binding, "Name", 0);
        var snapshot = context.Scope.SnapshotCurrent()
            .OrderBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in snapshot)
        {
            if (!WildcardPattern.IsMatchAny(name, patterns))
                continue;
            if (binding.HasSwitch("ValueOnly"))
                yield return value;
            else
                yield return new PwshProviderItem(PwshDriveKind.Variable, name, value);
        }
    }
}

public sealed class NewVariableCommand : Cmdlet
{
    public override string Name => "New-Variable";
    public override IEnumerable<string> Aliases => new[] { "nv" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var name = binding.GetValue<string>("Name", 0, null)
            ?? throw new PwshRuntimeException("New-Variable requires a -Name argument.");
        if (!binding.HasSwitch("Force") && context.Scope.Contains(null, name))
            throw new PwshRuntimeException($"Variable '{name}' already exists.");

        var value = binding.GetNamedRaw("Value");
        context.Scope.Set(null, name, value);
        if (binding.HasSwitch("PassThru"))
            yield return new PwshProviderItem(PwshDriveKind.Variable, name, value);
    }
}

public sealed class SetVariableCommand : Cmdlet
{
    public override string Name => "Set-Variable";
    public override IEnumerable<string> Aliases => new[] { "sv", "set" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var name = binding.GetValue<string>("Name", 0, null)
            ?? throw new PwshRuntimeException("Set-Variable requires a -Name argument.");
        object? value = binding.GetNamedRaw("Value");
        if (value is null && binding.TryGetPositional(1, out var positionalValue))
            value = positionalValue;
        context.Scope.Set(null, name, value);
        if (binding.HasSwitch("PassThru"))
            yield return new PwshProviderItem(PwshDriveKind.Variable, name, value);
    }
}

public sealed class RemoveVariableCommand : Cmdlet
{
    public override string Name => "Remove-Variable";
    public override IEnumerable<string> Aliases => new[] { "rv" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var patterns = DiscoveryHelpers.GetPatterns(binding, "Name", 0);
        foreach (var name in context.Scope.SnapshotCurrent().Keys.Where(name => WildcardPattern.IsMatchAny(name, patterns)).ToArray())
            context.Scope.Remove(null, name);
        yield break;
    }
}

public sealed class ClearVariableCommand : Cmdlet
{
    public override string Name => "Clear-Variable";
    public override IEnumerable<string> Aliases => new[] { "clv" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var patterns = DiscoveryHelpers.GetPatterns(binding, "Name", 0);
        foreach (var name in context.Scope.SnapshotCurrent().Keys.Where(name => WildcardPattern.IsMatchAny(name, patterns)).ToArray())
            context.Scope.Set(null, name, null);
        yield break;
    }
}

internal static partial class SessionStateCommandHelpers
{
    public static PwshAliasOptions ParseAliasOptions(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return PwshAliasOptions.None;

        var combined = PwshAliasOptions.None;
        foreach (var piece in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<PwshAliasOptions>(piece, ignoreCase: true, out var parsed))
                combined |= parsed;
        }

        return combined;
    }
}
