// Adapted from WasmSharp.Core.Services.Tracer.
// Upstream: https://github.com/JakeYallop/WasmSharp (Apache-2.0).

using Carbide.Core.Hosting;
using Microsoft.Extensions.Logging;

namespace Carbide.Core.Services;

internal sealed class Tracer(string actionName) : IDisposable
{
    private readonly DateTime _startTime = DateTime.UtcNow;
    private readonly string _actionName = actionName;
    private readonly ILogger<Tracer> _logger = Host.Services.GetService<ILogger<Tracer>>();

    public static Tracer Trace(string actionName) => new(actionName);

    public void Dispose()
    {
        var endTime = DateTime.UtcNow;
        _logger.LogDebug($"{_actionName} took {(endTime - _startTime).TotalMilliseconds}ms");
    }
}
