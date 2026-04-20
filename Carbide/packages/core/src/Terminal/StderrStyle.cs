namespace Carbide.Terminal;

/// <summary>
/// Style wrapped (on the C# side) around each stderr flush chunk before it reaches the JS
/// terminal bridge. Matches the wire values on <see cref="Carbide.Core.Services.InteractiveOptions"/>'s
/// <c>stderrStyle</c> field.
/// </summary>
internal enum StderrStyle
{
    /// <summary>Emit stderr bytes unchanged; xterm.js renders them with no SGR modification.</summary>
    Plain,
    /// <summary>Wrap each flush chunk with <c>\x1b[2m…\x1b[22m</c> (dim).</summary>
    Dim,
    /// <summary>Wrap each flush chunk with <c>\x1b[31m…\x1b[39m</c> (default-red foreground).</summary>
    Red,
}
