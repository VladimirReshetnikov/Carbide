// @carbide-ui/avalonia-runner — UI-M2 bootstrap.
// Boots Avalonia.Browser into the iframe's <div id="out">. At UI-M3 this grows a
// postMessage bridge (RunnerProgram) that accepts user PE payloads and mounts them
// via a collectible AssemblyLoadContext.

using Avalonia;
using Avalonia.Browser;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("browser")]

internal sealed class Program
{
    public static Task Main(string[] args) =>
        BuildAvaloniaApp().StartBrowserAppAsync("out");

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Carbide.UI.Runner.App>();
}
