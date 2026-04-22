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

    /// <summary>
    /// core-P3 (plan §10.3): version of the PE/PDB wire contract. Always <c>1</c> at v1 —
    /// exists so future launchers (e.g. <c>@carbide-ui/launcher</c>) can guard against
    /// shape changes. Null when <see cref="Success"/> is false.
    /// </summary>
    public int? PeSchemaVersion { get; init; }

    /// <summary>
    /// core-P3 (plan §10.3): the compilation's assembly name (e.g. "MyApp"). The
    /// <c>@carbide-ui/launcher</c> uses this as a fallback for <c>LaunchOptions.appClass</c>
    /// when the caller doesn't specify one (proposal §12 Q.2 v1.1 follow-up). Null when
    /// <see cref="Success"/> is false.
    /// </summary>
    public string? PrimaryAssemblyName { get; init; }

    public static BuildResult Succeeded(byte[] pe, byte[] pdb, double durationMs, string? primaryAssemblyName) => new()
    {
        Success = true,
        Pe = pe,
        Pdb = pdb,
        DurationMs = durationMs,
        PeSchemaVersion = 1,
        PrimaryAssemblyName = primaryAssemblyName,
    };

    public static BuildResult Failed(Diagnostic[] diagnostics, double durationMs) => new()
    {
        Success = false,
        Diagnostics = diagnostics,
        DurationMs = durationMs,
    };
}
