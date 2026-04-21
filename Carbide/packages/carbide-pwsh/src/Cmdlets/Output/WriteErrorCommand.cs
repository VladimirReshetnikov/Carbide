using CarbidePwsh.Runtime;

namespace CarbidePwsh.Cmdlets.Output;

public sealed class WriteErrorCommand : Cmdlet
{
    public override string Name => "Write-Error";

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var message = binding.GetValue<string>("Message", 0, null)
            ?? (input != null ? string.Join(" ", input.Select(Coercion.FormatAsString)) : "");
        context.Error.Write("\x1b[31mError: ");
        context.Error.Write(message);
        context.Error.WriteLine("\x1b[0m");
        return Enumerable.Empty<object?>();
    }
}
