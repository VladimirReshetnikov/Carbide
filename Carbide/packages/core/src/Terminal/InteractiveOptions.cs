namespace Carbide.Terminal;

/// <summary>
/// Internal bag carrying parsed interactive-run options from
/// <see cref="Carbide.Core.CompilationInterop.RunInteractiveAsync"/> through to
/// <see cref="Carbide.Core.Services.ProjectCompiler.RunInteractiveAsync"/>.
/// </summary>
internal sealed record InteractiveOptions(string[] Args, StderrStyle StderrStyle)
{
    internal static InteractiveOptions Default { get; } = new(Array.Empty<string>(), StderrStyle.Plain);
}
