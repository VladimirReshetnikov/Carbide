using CarbidePwsh.Errors;
using CarbideShellCore.Vfs;

namespace CarbidePwsh.Cmdlets.App;

public sealed class RegisterCarbideAppCommand : Cmdlet
{
    public override string Name => "Register-CarbideApp";

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var appName = binding.GetValue<string>("Name", 0, null)
            ?? throw new PwshRuntimeException("Register-CarbideApp requires -Name.");
        var path = binding.GetValue<string>("Path", 1, null)
            ?? throw new PwshRuntimeException("Register-CarbideApp requires -Path.");
        var abs = context.Vfs.Normalize(path);
        var node = context.Vfs.Resolve(abs);
        if (node is not VfsFile)
            throw new PwshRuntimeException($"App path '{abs}' is not an existing file in the VFS.");
        if (context.Interpreter.Apps == null)
            throw new PwshRuntimeException("App registry not wired up.");
        context.Interpreter.Apps.Register(appName, abs);
        yield break;
    }
}

public sealed class UnregisterCarbideAppCommand : Cmdlet
{
    public override string Name => "Unregister-CarbideApp";

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var appName = binding.GetValue<string>("Name", 0, null)
            ?? throw new PwshRuntimeException("Unregister-CarbideApp requires -Name.");
        context.Interpreter.Apps?.Remove(appName);
        yield break;
    }
}

public sealed class GetCarbideAppCommand : Cmdlet
{
    public override string Name => "Get-CarbideApp";

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        if (context.Interpreter.Apps == null) yield break;
        foreach (var kv in context.Interpreter.Apps.All)
        {
            var dict = new global::System.Collections.Specialized.OrderedDictionary();
            dict["Name"] = kv.Key;
            dict["Path"] = kv.Value;
            yield return dict;
        }
    }
}
