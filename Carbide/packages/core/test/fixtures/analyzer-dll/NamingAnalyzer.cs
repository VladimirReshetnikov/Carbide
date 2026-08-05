// Test fixture for Carbide's diagnostic-analyzer support. Not shipped — Carbide's tests read
// the built DLL's bytes and register it with `session.addAnalyzer`.
//
// Deliberately analyzer-only, with no source generator. That is the shape Carbide refused
// before analyzers ran, and the shape a real analyzer package (Microsoft.CodeAnalysis.NetAnalyzers,
// StyleCop.Analyzers, …) actually ships.
//
// Two rules, chosen so a test can assert on both severities independently:
//   CARBIDETEST001 (warning) — a type name that does not start with an uppercase letter.
//   CARBIDETEST002 (error)   — a type named exactly `Forbidden`.
//
// The error rule matters: an analyzer that can only warn cannot demonstrate that analyzer
// diagnostics participate in whether a build succeeds.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CarbideTestAnalyzer;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NamingAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor LowercaseTypeRule = new(
        id: "CARBIDETEST001",
        title: "Type name should start with an uppercase letter",
        messageFormat: "Type '{0}' should start with an uppercase letter",
        category: "Naming",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ForbiddenTypeRule = new(
        id: "CARBIDETEST002",
        title: "Forbidden type name",
        messageFormat: "Type '{0}' is forbidden",
        category: "Naming",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(LowercaseTypeRule, ForbiddenTypeRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        // Carbide runs the analyzer driver in non-concurrent mode (Mono-WASM browser is
        // single-threaded), but EnableConcurrentExecution is what a real analyzer declares,
        // so the fixture declares it too — the host's setting is what decides.
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context)
    {
        var symbol = (INamedTypeSymbol)context.Symbol;
        if (symbol.Name.Length == 0) return;

        if (symbol.Name == "Forbidden")
        {
            context.ReportDiagnostic(Diagnostic.Create(
                ForbiddenTypeRule, symbol.Locations[0], symbol.Name));
            return;
        }

        if (char.IsLower(symbol.Name[0]))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                LowercaseTypeRule, symbol.Locations[0], symbol.Name));
        }
    }
}
