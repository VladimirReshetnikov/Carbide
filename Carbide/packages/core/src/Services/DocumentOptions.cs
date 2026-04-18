// Adapted from WasmSharp.Core.Services.DocumentOptions.
// Upstream: https://github.com/JakeYallop/WasmSharp (Apache-2.0).
//
// M5 extensions:
//   * DefaultCompilationOptions flips Deterministic=true so emitted PE bytes are reproducible
//     across runs (see carbide-M5-detailed-plan §5 D53).
//   * ImplicitUsings, AssemblyName, and RootNamespace are plumbed through so the consumer can
//     control whether the hidden Carbide.GlobalUsings.g.cs document is injected and how the
//     emitted assembly identifies itself.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Carbide.Core.Services;

public sealed class DocumentOptions
{
    public static DocumentOptions Default => new();
    public static CSharpParseOptions DefaultParseOptions => CSharpParseOptions.Default;

    public static CSharpCompilationOptions DefaultCompilationOptions =>
        new CSharpCompilationOptions(OutputKind.ConsoleApplication)
            .WithConcurrentBuild(false)
            .WithDeterministic(true);

    public DocumentOptions()
    {
        CSharpCompilationOptions = DefaultCompilationOptions;
        CSharpParseOptions = DefaultParseOptions;
    }

    public CSharpCompilationOptions CSharpCompilationOptions { get; init; }
    public CSharpParseOptions CSharpParseOptions { get; init; }

    /// <summary>
    /// When <c>true</c> (default), <see cref="ProjectCompiler"/> injects a hidden
    /// <c>Carbide.GlobalUsings.g.cs</c> document so bare <c>Console.WriteLine(…)</c>
    /// compiles. Set to <c>false</c> from <c>.csproj</c>'s
    /// <c>&lt;ImplicitUsings&gt;disable&lt;/ImplicitUsings&gt;</c>.
    /// </summary>
    public bool ImplicitUsings { get; init; } = true;

    /// <summary>
    /// Optional assembly name override. When null, <see cref="ProjectCompiler"/> uses its
    /// constructor-supplied default (<c>Carbide.Project</c>).
    /// </summary>
    public string? AssemblyName { get; init; }

    /// <summary>Optional root namespace. Currently informational; Roslyn handles it per file.</summary>
    public string? RootNamespace { get; init; }
}
