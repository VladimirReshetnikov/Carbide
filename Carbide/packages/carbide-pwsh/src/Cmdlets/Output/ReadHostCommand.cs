namespace CarbidePwsh.Cmdlets.Output;

public sealed class ReadHostCommand : Cmdlet
{
    public override string Name => "Read-Host";

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var prompt = binding.GetValue<string>("Prompt", 0, null);
        if (!string.IsNullOrEmpty(prompt))
        {
            context.Output.Write(prompt);
            context.Output.Write(": ");
        }
        var line = Console.In.ReadLine();
        yield return line ?? "";
    }
}
