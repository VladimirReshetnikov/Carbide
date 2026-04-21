using CarbidePwsh.Runtime;

namespace CarbidePwsh.Cmdlets.Output;

/// <summary>
/// Writes text directly to the host's output stream. Unlike Write-Output it doesn't emit into
/// the pipeline, and it supports SGR-coded foreground/background colors via the xterm stream.
/// </summary>
public sealed class WriteHostCommand : Cmdlet
{
    public override string Name => "Write-Host";

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var objects = new List<object?>();
        if (binding.Named.TryGetValue("Object", out var obj)) objects.Add(obj);
        else objects.AddRange(binding.Positional);

        var separator = binding.GetOrDefault<string>("Separator", " ") ?? " ";
        var noNewline = binding.HasSwitch("NoNewline");
        var fg = binding.GetOrDefault<ConsoleColor?>("ForegroundColor", null);
        var bg = binding.GetOrDefault<ConsoleColor?>("BackgroundColor", null);

        var text = string.Join(separator, objects.Select(o =>
        {
            if (o is System.Collections.IEnumerable e && o is not string)
            {
                var parts = new List<string>();
                foreach (var i in e) parts.Add(Coercion.FormatAsString(i));
                return string.Join(" ", parts);
            }
            return Coercion.FormatAsString(o);
        }));

        var writer = context.Output;
        if (fg.HasValue) writer.Write(AnsiSgr.Foreground(fg.Value));
        if (bg.HasValue) writer.Write(AnsiSgr.Background(bg.Value));
        writer.Write(text);
        if (fg.HasValue || bg.HasValue) writer.Write("\x1b[0m");
        if (!noNewline) writer.WriteLine();

        return Enumerable.Empty<object?>();
    }
}

internal static class AnsiSgr
{
    public static string Foreground(ConsoleColor c) => c switch
    {
        ConsoleColor.Black => "\x1b[30m",
        ConsoleColor.DarkRed => "\x1b[31m",
        ConsoleColor.DarkGreen => "\x1b[32m",
        ConsoleColor.DarkYellow => "\x1b[33m",
        ConsoleColor.DarkBlue => "\x1b[34m",
        ConsoleColor.DarkMagenta => "\x1b[35m",
        ConsoleColor.DarkCyan => "\x1b[36m",
        ConsoleColor.Gray => "\x1b[37m",
        ConsoleColor.DarkGray => "\x1b[90m",
        ConsoleColor.Red => "\x1b[91m",
        ConsoleColor.Green => "\x1b[92m",
        ConsoleColor.Yellow => "\x1b[93m",
        ConsoleColor.Blue => "\x1b[94m",
        ConsoleColor.Magenta => "\x1b[95m",
        ConsoleColor.Cyan => "\x1b[96m",
        ConsoleColor.White => "\x1b[97m",
        _ => "",
    };

    public static string Background(ConsoleColor c) => c switch
    {
        ConsoleColor.Black => "\x1b[40m",
        ConsoleColor.DarkRed => "\x1b[41m",
        ConsoleColor.DarkGreen => "\x1b[42m",
        ConsoleColor.DarkYellow => "\x1b[43m",
        ConsoleColor.DarkBlue => "\x1b[44m",
        ConsoleColor.DarkMagenta => "\x1b[45m",
        ConsoleColor.DarkCyan => "\x1b[46m",
        ConsoleColor.Gray => "\x1b[47m",
        ConsoleColor.DarkGray => "\x1b[100m",
        ConsoleColor.Red => "\x1b[101m",
        ConsoleColor.Green => "\x1b[102m",
        ConsoleColor.Yellow => "\x1b[103m",
        ConsoleColor.Blue => "\x1b[104m",
        ConsoleColor.Magenta => "\x1b[105m",
        ConsoleColor.Cyan => "\x1b[106m",
        ConsoleColor.White => "\x1b[107m",
        _ => "",
    };
}
