// M4 BuildResult: the structured outcome of ProjectCompiler.BuildAsync. Mirrors the layout
// of RunResult (SchemaVersion, Success, Diagnostics, DurationMs) so consumers can treat the
// two uniformly in match/switch code.

namespace Carbide.Core.Services;

public sealed class BuildResult
{
    // See interop/schema.ts: single SCHEMA_VERSION across all Carbide payloads. M5 = 2;
    // U2 = 3 (bumped when RunAsync's request shape gained args/stdin); T1 = 4 (bumped when
    // the interactive terminal path landed); T2 = 5 (bumped when the input-side bridge
    // exports landed).
    public int SchemaVersion { get; init; } = 5;
    public bool Success { get; init; }
    public byte[]? Pe { get; init; }
    public byte[]? Pdb { get; init; }
    public Diagnostic[] Diagnostics { get; init; } = [];
    public double DurationMs { get; init; }

    public static BuildResult Succeeded(byte[] pe, byte[] pdb, double durationMs) => new()
    {
        Success = true,
        Pe = pe,
        Pdb = pdb,
        DurationMs = durationMs,
    };

    public static BuildResult Failed(Diagnostic[] diagnostics, double durationMs) => new()
    {
        Success = false,
        Diagnostics = diagnostics,
        DurationMs = durationMs,
    };
}
