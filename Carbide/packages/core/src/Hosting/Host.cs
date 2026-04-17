// Adapted from WasmSharp.Core.Hosting.Host. Upstream: https://github.com/JakeYallop/WasmSharp (Apache-2.0).
// M0 scaffold: ships only the Jab-generated ServiceProvider and a logger singleton.
// Later milestones add the session / solution services that higher layers dispatch through.

using System.Diagnostics;
using Jab;
using Microsoft.Extensions.Logging;

namespace Carbide.Core.Hosting;

internal static class Host
{
    public static CarbideServiceProvider Services { get; } = new();
}

[ServiceProvider]
[Singleton(typeof(ILogger<>), typeof(WebAssemblyConsoleLogger<>))]
internal sealed partial class CarbideServiceProvider : IServiceProvider { }

public static class CarbideServiceProviderExtensions
{
    public static T GetService<T>(this IServiceProvider serviceProvider)
    {
        if (serviceProvider is CarbideServiceProvider carbideServiceProvider)
        {
            return carbideServiceProvider.GetService<T>();
        }
        throw new UnreachableException();
    }
}
