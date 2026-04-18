// Adapted from WasmSharp.Core.Services.RunResult. Extended with exit code, duration,
// and uncaught-exception fields per carbide architecture §3.4.
// Upstream: https://github.com/JakeYallop/WasmSharp (Apache-2.0).

namespace Carbide.Core.Services;

public sealed class RunResult
{
    public int SchemaVersion { get; init; } = 1;
    public bool Success { get; init; }
    public int? ExitCode { get; init; }
    public string StdOut { get; init; } = string.Empty;
    public string StdErr { get; init; } = string.Empty;
    public string? UncaughtException { get; init; }
    public double DurationMs { get; init; }
    public Diagnostic[] Diagnostics { get; init; } = [];

    public static RunResult Success_(string stdOut, string stdErr, int exitCode, double durationMs) => new()
    {
        Success = true,
        StdOut = stdOut,
        StdErr = stdErr,
        ExitCode = exitCode,
        DurationMs = durationMs,
    };

    public static RunResult Uncaught(string stdOut, string stdErr, string exceptionText, double durationMs) => new()
    {
        Success = false,
        StdOut = stdOut,
        StdErr = stdErr,
        UncaughtException = exceptionText,
        DurationMs = durationMs,
    };

    public static RunResult CompileFailure(Diagnostic[] diagnostics, double durationMs) => new()
    {
        Success = false,
        Diagnostics = diagnostics,
        DurationMs = durationMs,
    };
}
