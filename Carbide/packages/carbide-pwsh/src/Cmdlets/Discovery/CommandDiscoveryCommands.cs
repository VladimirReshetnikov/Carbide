using System.Text;
using CarbidePwsh.Errors;
using CarbidePwsh.Runtime;

namespace CarbidePwsh.Cmdlets.Discovery;

public sealed class GetCommandCommand : Cmdlet
{
    public override string Name => "Get-Command";
    public override IEnumerable<string> Aliases => new[] { "gcm" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var patterns = DiscoveryHelpers.GetPatterns(binding, "Name", 0);
        var commandTypes = DiscoveryHelpers.GetPatterns(binding, "CommandType", -1, defaultPattern: "")
            .Where(static s => s.Length > 0)
            .Select(static s => s.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var info in DiscoveryHelpers.EnumerateCommandInfos(context.Interpreter, context.Interpreter.Registry))
        {
            if (commandTypes.Count > 0 && !commandTypes.Contains(info.CommandType))
                continue;
            if (!WildcardPattern.IsMatchAny(info.Name, patterns))
                continue;
            yield return info;
        }
    }
}

public sealed class GetAliasCommand : Cmdlet
{
    public override string Name => "Get-Alias";
    public override IEnumerable<string> Aliases => new[] { "gal" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var registry = context.Interpreter.Registry
            ?? throw new PwshRuntimeException("No cmdlet registry configured.");
        var namePatterns = DiscoveryHelpers.GetPatterns(binding, "Name", 0);
        var definitionPatterns = DiscoveryHelpers.GetPatterns(binding, "Definition", -1, defaultPattern: "*");

        foreach (var alias in registry.AliasDefinitions.OrderBy(static a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!WildcardPattern.IsMatchAny(alias.Name, namePatterns))
                continue;
            if (!WildcardPattern.IsMatchAny(alias.Definition, definitionPatterns))
                continue;

            yield return new PwshCommandInfo(
                "Alias",
                alias.Name,
                alias.Definition,
                alias.Source,
                DiscoveryHelpers.IsImplementedCommand(alias.Definition, registry));
        }
    }
}

public sealed class GetHelpCommand : Cmdlet
{
    public override string Name => "Get-Help";
    public override IEnumerable<string> Aliases => new[] { "help" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var registry = context.Interpreter.Registry
            ?? throw new PwshRuntimeException("No cmdlet registry configured.");
        var topic = binding.GetValue<string>("Name", 0, null);
        if (string.IsNullOrWhiteSpace(topic))
        {
            yield return "Use `Get-Help <command>` or `help <command>` to inspect the builtin command catalog that carbide-pwsh recognizes.";
            yield break;
        }

        var resolved = registry.ResolveAliasChain(topic);
        var alias = registry.TryGetAliasDefinition(topic, out var aliasDefinition) ? aliasDefinition : null;
        var source = BuiltinCommandCatalog.TryGetCmdlet(resolved, out var builtin) ? builtin.Source : "CarbidePwsh";
        var implemented = DiscoveryHelpers.IsImplementedCommand(resolved, registry);
        var aliases = registry.AliasDefinitions
            .Where(a => string.Equals(registry.ResolveAliasChain(a.Name), resolved, StringComparison.OrdinalIgnoreCase))
            .Select(static a => a.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static a => a, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var sb = new StringBuilder();
        sb.AppendLine("NAME");
        sb.Append("    ").AppendLine(resolved);
        if (alias is not null)
        {
            sb.AppendLine();
            sb.AppendLine("ALIAS");
            sb.Append("    ").Append(alias.Name).Append(" -> ").AppendLine(alias.Definition);
        }
        sb.AppendLine();
        sb.AppendLine("SOURCE");
        sb.Append("    ").AppendLine(source);
        sb.AppendLine();
        sb.AppendLine("STATUS");
        sb.Append("    ").AppendLine(implemented
            ? "Implemented by carbide-pwsh."
            : "Recognized from the PowerShell 7.6 builtin catalog, but not implemented in carbide-pwsh yet.");
        if (aliases.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("ALIASES");
            sb.Append("    ").AppendLine(string.Join(", ", aliases));
        }

        yield return sb.ToString().TrimEnd();
    }
}

public sealed class GetModuleCommand : Cmdlet
{
    public override string Name => "Get-Module";
    public override IEnumerable<string> Aliases => new[] { "gmo" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var listAvailable = binding.HasSwitch("ListAvailable");
        var patterns = DiscoveryHelpers.GetPatterns(binding, "Name", 0);
        var imported = context.Interpreter.ImportedModules;
        IEnumerable<string> moduleSource = listAvailable
            ? BuiltinCommandCatalog.ModuleNames
            : imported.OrderBy(static m => m, StringComparer.OrdinalIgnoreCase);
        var modules = moduleSource
            .Where(module => WildcardPattern.IsMatchAny(module, patterns))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static module => module, StringComparer.OrdinalIgnoreCase);

        foreach (var module in modules)
        {
            yield return new PwshModuleInfo(
                module,
                imported.Contains(module),
                module.StartsWith("Microsoft.PowerShell.", StringComparison.OrdinalIgnoreCase));
        }
    }
}

public sealed class ImportModuleCommand : Cmdlet
{
    public override string Name => "Import-Module";
    public override IEnumerable<string> Aliases => new[] { "ipmo" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var names = DiscoveryHelpers.GetPatterns(binding, "Name", 0, defaultPattern: "");
        if (names.Count == 0 || names.All(static n => n.Length == 0))
            throw new PwshRuntimeException("Import-Module requires a module name.");

        foreach (var name in names)
        {
            if (name.Length == 0) continue;
            var module = BuiltinCommandCatalog.ModuleNames.FirstOrDefault(
                module => string.Equals(module, name, StringComparison.OrdinalIgnoreCase));
            if (module is null)
                throw new PwshRuntimeException($"Module '{name}' is not available in carbide-pwsh.");

            context.Interpreter.ImportedModules.Add(module);
            if (binding.HasSwitch("PassThru"))
                yield return new PwshModuleInfo(module, IsImported: true, IsImplemented: true);
        }
    }
}

public sealed class GetPSDriveCommand : Cmdlet
{
    public override string Name => "Get-PSDrive";
    public override IEnumerable<string> Aliases => new[] { "gdr" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        foreach (var drive in DiscoveryHelpers.GetDriveInfos(context))
            yield return drive;
    }
}

public sealed class GetPSProviderCommand : Cmdlet
{
    public override string Name => "Get-PSProvider";

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        yield return new PwshProviderInfo("Alias", "Alias");
        yield return new PwshProviderInfo("Environment", "Env");
        yield return new PwshProviderInfo("FileSystem", "FileSystem", "/home/user");
        yield return new PwshProviderInfo("Function", "Function");
        yield return new PwshProviderInfo("Variable", "Variable");
    }
}

internal static class DiscoveryHelpers
{
    public static IReadOnlyList<string> GetPatterns(
        ParameterBinding binding,
        string parameterName,
        int positionalIndex,
        string defaultPattern = "*")
    {
        object? value = binding.GetNamedRaw(parameterName);
        if (value is null && positionalIndex >= 0 && binding.TryGetPositional(positionalIndex, out var positional))
            value = positional;

        if (value is null)
            return defaultPattern.Length == 0 ? Array.Empty<string>() : new[] { defaultPattern };

        var list = new List<string>();
        if (value is IEnumerable<object?> objectEnumerable)
        {
            foreach (var item in objectEnumerable)
                list.Add(Coercion.FormatAsString(item));
            return list;
        }

        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            foreach (var item in enumerable)
                list.Add(Coercion.FormatAsString(item));
            return list;
        }

        list.Add(Coercion.FormatAsString(value));
        return list;
    }

    public static IEnumerable<PwshCommandInfo> EnumerateCommandInfos(Interpreter interpreter, CmdletRegistry? registry)
    {
        if (registry is null)
            throw new PwshRuntimeException("No cmdlet registry configured.");

        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in registry.CanonicalCmdletNames.OrderBy(static n => n, StringComparer.OrdinalIgnoreCase))
        {
            yielded.Add(name);
            yield return new PwshCommandInfo(
                "Cmdlet",
                name,
                Source: BuiltinCommandCatalog.TryGetCmdlet(name, out var builtin) ? builtin.Source : "CarbidePwsh",
                IsImplemented: true);
        }

        foreach (var builtin in BuiltinCommandCatalog.Cmdlets.OrderBy(static c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (yielded.Contains(builtin.Name))
                continue;
            yield return new PwshCommandInfo("Cmdlet", builtin.Name, Source: builtin.Source, IsImplemented: false);
        }

        foreach (var alias in registry.AliasDefinitions.OrderBy(static a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            yield return new PwshCommandInfo(
                "Alias",
                alias.Name,
                alias.Definition,
                alias.Source,
                IsImplementedCommand(alias.Definition, registry));
        }

        if (interpreter.Functions is null)
            yield break;

        foreach (var functionName in interpreter.Functions.Names.OrderBy(static n => n, StringComparer.OrdinalIgnoreCase))
            yield return new PwshCommandInfo("Function", functionName, Source: "CarbidePwsh", IsImplemented: true);
    }

    public static bool IsImplementedCommand(string name, CmdletRegistry registry)
        => registry.TryResolve(name, out _);

    public static IEnumerable<PwshDriveInfo> GetDriveInfos(CmdletContext context)
    {
        yield return new PwshDriveInfo(
            "/",
            "FileSystem",
            "/",
            context.Interpreter.CurrentDrive == PwshDriveKind.FileSystem ? context.Vfs.CurrentLocation : "/");
        yield return new PwshDriveInfo("Alias", "Alias", "Alias:\\", context.Interpreter.CurrentDrive == PwshDriveKind.Alias ? "Alias:\\" : "");
        yield return new PwshDriveInfo("Env", "Environment", "Env:\\", context.Interpreter.CurrentDrive == PwshDriveKind.Env ? "Env:\\" : "");
        yield return new PwshDriveInfo("Function", "Function", "Function:\\", context.Interpreter.CurrentDrive == PwshDriveKind.Function ? "Function:\\" : "");
        yield return new PwshDriveInfo("Variable", "Variable", "Variable:\\", context.Interpreter.CurrentDrive == PwshDriveKind.Variable ? "Variable:\\" : "");
    }
}
