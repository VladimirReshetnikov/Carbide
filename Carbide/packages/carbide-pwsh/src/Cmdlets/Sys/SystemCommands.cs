using System.Globalization;

namespace CarbidePwsh.Cmdlets.Sys;

/// <summary>
/// <c>Clear-Host</c> / <c>cls</c> / <c>clear</c> — clear the terminal screen and home the
/// cursor via the ANSI sequence <c>\x1b[2J\x1b[H</c>. Works against any VT100-compatible
/// host; in the Carbide xterm.js session that's the only surface we target, so the
/// sequence is emitted unconditionally.
/// </summary>
public sealed class ClearHostCommand : Cmdlet
{
    public override string Name => "Clear-Host";
    public override IEnumerable<string> Aliases => new[] { "cls", "clear" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        context.Output.Write("\x1b[2J\x1b[H");
        yield break;
    }
}

public sealed class StartSleepCommand : Cmdlet
{
    public override string Name => "Start-Sleep";
    public override IEnumerable<string> Aliases => new[] { "sleep" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        int seconds = binding.GetOrDefault<int>("Seconds", 0);
        int milliseconds = binding.GetOrDefault<int>("Milliseconds", 0);
        if (seconds == 0 && milliseconds == 0 && binding.TryGetPositional(0, out var v))
            seconds = (int)Runtime.Coercion.ToInt64(v);
        var total = seconds * 1000 + milliseconds;
        if (total > 0) global::System.Threading.Thread.Sleep(total);
        yield break;
    }
}

public sealed class GetDateCommand : Cmdlet
{
    public override string Name => "Get-Date";

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var format = binding.GetOrDefault<string>("Format", null);
        var date = binding.GetOrDefault<string>("Date", null);
        DateTime d = DateTime.Now;
        if (!string.IsNullOrEmpty(date) && DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            d = parsed;
        }
        if (!string.IsNullOrEmpty(format))
        {
            yield return d.ToString(format, CultureInfo.InvariantCulture);
        }
        else
        {
            yield return d;
        }
    }
}

public sealed class GetRandomCommand : Cmdlet
{
    public override string Name => "Get-Random";

    private static readonly global::System.Random _rng = new();

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var count = binding.GetOrDefault<int>("Count", 1);
        var minimum = binding.GetOrDefault<int>("Minimum", 0);
        var maximum = binding.GetOrDefault<int?>("Maximum", null);

        // If -InputObject or pipeline supplied, pick from the collection.
        object?[]? pool = null;
        if (binding.Named.TryGetValue("InputObject", out var inObj) && inObj is global::System.Collections.IEnumerable en1 && inObj is not string)
            pool = en1.Cast<object?>().ToArray();
        else if (input != null)
        {
            var buffered = input.ToArray();
            if (buffered.Length > 0) pool = buffered;
        }

        if (pool != null)
        {
            var takes = Math.Min(count, pool.Length);
            var copy = pool.ToArray();
            for (int i = 0; i < takes; i++)
            {
                var j = _rng.Next(i, copy.Length);
                (copy[i], copy[j]) = (copy[j], copy[i]);
                yield return copy[i];
            }
            yield break;
        }

        int max = maximum ?? (minimum == 0 ? int.MaxValue : minimum + 1);
        for (int i = 0; i < count; i++) yield return _rng.Next(minimum, max);
    }
}

public sealed class NewGuidCommand : Cmdlet
{
    public override string Name => "New-Guid";

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        yield return Guid.NewGuid();
    }
}

public sealed class InvokeExpressionCommand : Cmdlet
{
    public override string Name => "Invoke-Expression";
    public override IEnumerable<string> Aliases => new[] { "iex" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var expr = binding.GetValue<string>("Command", 0, null)
            ?? (input != null ? string.Join("\n", input.Select(Runtime.Coercion.FormatAsString)) : "");
        if (string.IsNullOrWhiteSpace(expr)) yield break;
        var script = Parser.Parser.ParseString(expr);
        var result = context.Interpreter.Evaluate(script);
        foreach (var item in Pipeline.ExpressionToEnumerable(result)) yield return item;
    }
}
