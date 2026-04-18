// Adapted from WasmSharp.Core.Hosting.Host. Upstream: https://github.com/JakeYallop/WasmSharp (Apache-2.0).

using System.Diagnostics;
using Carbide.Core.Services;
using Jab;
using Microsoft.Extensions.Logging;

namespace Carbide.Core.Hosting;

internal static class Host
{
    public static CarbideServiceProvider Services { get; } = new();

    public static void Dispatch(Action<SessionSolutions> action)
    {
        var solutions = Services.GetService<SessionSolutions>();
        action(solutions);
    }

    public static T Dispatch<T>(Func<SessionSolutions, T> action)
    {
        var solutions = Services.GetService<SessionSolutions>();
        return action(solutions);
    }

    public static Task Dispatch(Func<SessionSolutions, Task> action)
    {
        var solutions = Services.GetService<SessionSolutions>();
        return action(solutions);
    }

    public static Task<T> Dispatch<T>(Func<SessionSolutions, Task<T>> action)
    {
        var solutions = Services.GetService<SessionSolutions>();
        return action(solutions);
    }
}

[ServiceProvider]
[Singleton(typeof(ILogger<>), typeof(WebAssemblyConsoleLogger<>))]
[Singleton<SessionSolutions>]
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
