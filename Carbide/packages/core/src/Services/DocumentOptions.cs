// Adapted from WasmSharp.Core.Services.DocumentOptions.
// Upstream: https://github.com/JakeYallop/WasmSharp (Apache-2.0).

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Carbide.Core.Services;

public sealed class DocumentOptions
{
    public static DocumentOptions Default => new();
    public static CSharpParseOptions DefaultParseOptions => CSharpParseOptions.Default;

    public static CSharpCompilationOptions DefaultCompilationOptions =>
        new CSharpCompilationOptions(OutputKind.ConsoleApplication).WithConcurrentBuild(false);

    public DocumentOptions()
    {
        CSharpCompilationOptions = DefaultCompilationOptions;
        CSharpParseOptions = DefaultParseOptions;
    }

    public CSharpCompilationOptions CSharpCompilationOptions { get; init; }
    public CSharpParseOptions CSharpParseOptions { get; init; }
}
