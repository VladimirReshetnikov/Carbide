// Adapted from WasmSharp.Core.Services.Diagnostic. Extended with path and line/column
// fields so consumers can render diagnostics without re-parsing source.
// Upstream: https://github.com/JakeYallop/WasmSharp (Apache-2.0).

namespace Carbide.Core.Services;

public sealed class Diagnostic
{
    public string Id { get; init; } = string.Empty;
    public string Severity { get; init; } = "hidden";
    public string Message { get; init; } = string.Empty;
    public string? Path { get; init; }
    public int SpanStart { get; init; }
    public int SpanEnd { get; init; }
    public int? LineStart { get; init; }
    public int? LineEnd { get; init; }
    public int? ColumnStart { get; init; }
    public int? ColumnEnd { get; init; }
}
