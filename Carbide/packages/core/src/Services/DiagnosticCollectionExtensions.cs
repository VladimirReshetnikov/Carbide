// Adapted from WasmSharp.Core.Services.DiagnosticCollectionExtensions.
// Upstream: https://github.com/JakeYallop/WasmSharp (Apache-2.0).

using System.Globalization;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;
using RoslynDiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace Carbide.Core.Services;

public static class DiagnosticCollectionExtensions
{
    public static IEnumerable<Diagnostic> ToCarbideDiagnostics(this IEnumerable<RoslynDiagnostic> diagnostics)
        => diagnostics.Select(ToCarbideDiagnostic);

    public static Diagnostic[] ToCarbideDiagnosticArray(this IEnumerable<RoslynDiagnostic> diagnostics)
        => diagnostics.Select(ToCarbideDiagnostic).ToArray();

    private static Diagnostic ToCarbideDiagnostic(RoslynDiagnostic d)
    {
        var location = d.Location;
        var span = location.SourceSpan;
        string? path = null;
        int? lineStart = null;
        int? lineEnd = null;
        int? columnStart = null;
        int? columnEnd = null;

        if (location.IsInSource)
        {
            var mapped = location.GetLineSpan();
            path = mapped.Path;
            if (string.IsNullOrEmpty(path))
            {
                path = location.SourceTree?.FilePath;
            }
            lineStart = mapped.StartLinePosition.Line + 1;
            lineEnd = mapped.EndLinePosition.Line + 1;
            columnStart = mapped.StartLinePosition.Character + 1;
            columnEnd = mapped.EndLinePosition.Character + 1;
        }

        return new Diagnostic
        {
            Id = d.Id,
            Severity = SeverityToString(d.Severity),
            Message = d.GetMessage(CultureInfo.InvariantCulture),
            Path = path,
            SpanStart = span.Start,
            SpanEnd = span.End,
            LineStart = lineStart,
            LineEnd = lineEnd,
            ColumnStart = columnStart,
            ColumnEnd = columnEnd,
        };
    }

    private static string SeverityToString(RoslynDiagnosticSeverity severity) => severity switch
    {
        RoslynDiagnosticSeverity.Error => "error",
        RoslynDiagnosticSeverity.Warning => "warning",
        RoslynDiagnosticSeverity.Info => "info",
        RoslynDiagnosticSeverity.Hidden => "hidden",
        _ => "hidden",
    };
}
