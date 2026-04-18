// Adapted from WasmSharp.Core.CompilationInterop. Expanded to carry the M1 JSExport surface
// (session / project management + build/run) with JSON at the boundary per architecture §5.
// Upstream: https://github.com/JakeYallop/WasmSharp (Apache-2.0).

using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Text.Json.Serialization;
using Carbide.Core.Hosting;
using Carbide.Core.Services;
using Microsoft.Extensions.Logging;

namespace Carbide.Core;

public static partial class CompilationInterop
{
    [JSExport]
    public static Task InitAsync(string[] assemblyUrls)
    {
        var logger = Host.Services.GetService<ILogger<SessionSolutions>>();
        logger.LogInformation("Carbide initialising with {Count} assembly urls.", assemblyUrls.Length);
        return Host.Dispatch(s => s.InitializeReferencesAsync(assemblyUrls));
    }

    [JSExport]
    public static string CreateSession(string optionsJson)
    {
        _ = optionsJson; // M1: options are parsed but no fields are yet consumed at the session level.
        return Host.Dispatch(s => s.CreateSession());
    }

    [JSExport]
    public static void DisposeSession(string sessionId)
        => Host.Dispatch(s => s.DisposeSession(sessionId));

    [JSExport]
    public static string CreateProject(string sessionId, string optionsJson)
    {
        var options = string.IsNullOrWhiteSpace(optionsJson)
            ? null
            : JsonSerializer.Deserialize(optionsJson, CarbideJsonContext.Default.ProjectOptionsDto);
        ValidateSchemaVersion(options?.SchemaVersion, "ProjectOptions");
        var documentOptions = BuildDocumentOptions(options);
        return Host.Dispatch(s => s.CreateProject(sessionId, documentOptions, options?.AssemblyName));
    }

    [JSExport]
    public static void AddSource(string projectId, string path, string code)
        => Host.Dispatch(s => s.AddSource(projectId, path, code));

    [JSExport]
    public static async Task<string> GetDiagnosticsAsync(string projectId)
    {
        var diagnostics = await Host.Dispatch(s => s.GetDiagnosticsAsync(projectId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(diagnostics, CarbideJsonContext.Default.DiagnosticArray);
    }

    [JSExport]
    public static async Task<string> RunAsync(string projectId)
    {
        var result = await Host.Dispatch(s => s.RunAsync(projectId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, CarbideJsonContext.Default.RunResult);
    }

    private static void ValidateSchemaVersion(int? schemaVersion, string name)
    {
        if (schemaVersion is not null and not 1)
        {
            throw new InvalidOperationException(
                $"Unsupported {name} schemaVersion: expected 1, got {schemaVersion}.");
        }
    }

    private static DocumentOptions? BuildDocumentOptions(ProjectOptionsDto? dto)
    {
        if (dto is null)
        {
            return null;
        }

        var parseOptions = DocumentOptions.DefaultParseOptions;
        if (!string.IsNullOrWhiteSpace(dto.LanguageVersion)
            && Microsoft.CodeAnalysis.CSharp.LanguageVersionFacts.TryParse(dto.LanguageVersion, out var langVersion))
        {
            parseOptions = parseOptions.WithLanguageVersion(langVersion);
        }

        var compilationOptions = DocumentOptions.DefaultCompilationOptions;
        if (dto.Nullable == true)
        {
            compilationOptions = compilationOptions.WithNullableContextOptions(
                Microsoft.CodeAnalysis.NullableContextOptions.Enable);
        }

        return new DocumentOptions
        {
            CSharpCompilationOptions = compilationOptions,
            CSharpParseOptions = parseOptions,
        };
    }
}

internal sealed class ProjectOptionsDto
{
    public int? SchemaVersion { get; set; } = 1;
    public string? TargetFramework { get; set; }
    public string? LanguageVersion { get; set; }
    public bool? Nullable { get; set; }
    public bool? ImplicitUsings { get; set; }
    public string? AssemblyName { get; set; }
    public string? RootNamespace { get; set; }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.Strict,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Diagnostic))]
[JsonSerializable(typeof(Diagnostic[]), TypeInfoPropertyName = "DiagnosticArray")]
[JsonSerializable(typeof(RunResult))]
[JsonSerializable(typeof(ProjectOptionsDto))]
internal sealed partial class CarbideJsonContext : JsonSerializerContext
{
}
