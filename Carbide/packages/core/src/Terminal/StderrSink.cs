namespace Carbide.Terminal;

/// <summary>
/// Helpers that wrap a stderr sink with an SGR prefix/suffix so the terminal renders stderr
/// text in the caller's chosen style. Wrap happens per flush chunk (not per
/// <c>Write(char)</c>) so a single line is not fragmented across SGR boundaries.
/// </summary>
internal static class StderrSink
{
    /// <summary>
    /// Returns a sink that writes each chunk through <paramref name="inner"/> wrapped in the
    /// ANSI SGR codes for <paramref name="style"/>. <see cref="StderrStyle.Plain"/> returns
    /// <paramref name="inner"/> unchanged so the common case has zero overhead.
    /// </summary>
    internal static Action<string> Wrap(Action<string> inner, StderrStyle style) => style switch
    {
        StderrStyle.Dim => s => inner($"\x1b[2m{s}\x1b[22m"),
        StderrStyle.Red => s => inner($"\x1b[31m{s}\x1b[39m"),
        _ => inner,
    };
}
