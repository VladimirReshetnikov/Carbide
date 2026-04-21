namespace CarbidePwsh.Cmdlets.Output;

public sealed class WriteOutputCommand : Cmdlet
{
    public override string Name => "Write-Output";
    public override IEnumerable<string> Aliases => new[] { "echo", "write" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        // Write-Output -InputObject <obj> is equivalent to emitting the positionals. If both
        // are provided, named -InputObject wins. If neither, forward input.
        if (binding.Named.TryGetValue("InputObject", out var v))
        {
            return Pipeline.ExpressionToEnumerable(v);
        }
        if (binding.Positional.Count > 0)
        {
            return binding.Positional.SelectMany(Pipeline.ExpressionToEnumerable);
        }
        return input ?? Enumerable.Empty<object?>();
    }
}
