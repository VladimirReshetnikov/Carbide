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
    public static void UpdateSource(string projectId, string path, string code)
        => Host.Dispatch(s => s.UpdateSource(projectId, path, code));

    [JSExport]
    public static void RemoveSource(string projectId, string path)
        => Host.Dispatch(s => s.RemoveSource(projectId, path));

    // --- M3: reference registry surface -------------------------------------------------

    [JSExport]
    public static string AddReference(string sessionId, string base64Bytes, string? name)
    {
        var bytes = Convert.FromBase64String(base64Bytes);
        return Host.Dispatch(s => s.AddReference(sessionId, bytes, name));
    }

    [JSExport]
    public static bool RemoveReference(string sessionId, string referenceId)
        => Host.Dispatch(s => s.RemoveReference(sessionId, referenceId));

    [JSExport]
    public static void AttachReference(string projectId, string referenceId)
        => Host.Dispatch(s => s.AttachReference(projectId, referenceId));

    [JSExport]
    public static async Task<string> GetDiagnosticsAsync(string projectId)
    {
        var diagnostics = await Host.Dispatch(s => s.GetDiagnosticsAsync(projectId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(diagnostics, CarbideJsonContext.Default.DiagnosticArray);
    }

    /// <summary>
    /// Emits PE + portable-PDB bytes without running. Returns a JSON payload with the byte
    /// arrays base64-encoded; the TS side decodes them back into Uint8Array. See M4 plan §5 D38.
    /// </summary>
    [JSExport]
    public static async Task<string> BuildAsync(string projectId)
    {
        var result = await Host.Dispatch(s => s.BuildAsync(projectId)).ConfigureAwait(false);
        var dto = new BuildResultDto
        {
            SchemaVersion = result.SchemaVersion,
            Success = result.Success,
            PeBase64 = result.Pe is null ? null : Convert.ToBase64String(result.Pe),
            PdbBase64 = result.Pdb is null ? null : Convert.ToBase64String(result.Pdb),
            Diagnostics = result.Diagnostics,
            DurationMs = result.DurationMs,
        };
        return JsonSerializer.Serialize(dto, CarbideJsonContext.Default.BuildResultDto);
    }

    [JSExport]
    public static async Task<string> RunAsync(string projectId)
    {
        var result = await Host.Dispatch(s => s.RunAsync(projectId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, CarbideJsonContext.Default.RunResult);
    }

    private static void ValidateSchemaVersion(int? schemaVersion, string name)
    {
        // M5 bumped the schema to 2; accept both 1 and 2 so pre-M5 clients that don't
        // populate the new fields still work. Higher numbers are a definite mismatch.
        if (schemaVersion is not null and not 1 and not 2)
        {
            throw new InvalidOperationException(
                $"Unsupported {name} schemaVersion: expected 1 or 2, got {schemaVersion}.");
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
        if (dto.DefineConstants is { Length: > 0 } defines)
        {
            parseOptions = parseOptions.WithPreprocessorSymbols(defines);
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
            ImplicitUsings = dto.ImplicitUsings ?? true,
            AssemblyName = string.IsNullOrWhiteSpace(dto.AssemblyName) ? null : dto.AssemblyName,
            RootNamespace = string.IsNullOrWhiteSpace(dto.RootNamespace) ? null : dto.RootNamespace,
        };
    }
}

internal sealed class ProjectOptionsDto
{
    public int? SchemaVersion { get; set; } = 2;
    public string? TargetFramework { get; set; }
    public string? LanguageVersion { get; set; }
    public bool? Nullable { get; set; }
    public bool? ImplicitUsings { get; set; }
    public string? AssemblyName { get; set; }
    public string? RootNamespace { get; set; }
    public string[]? DefineConstants { get; set; }
}

/// <summary>
/// Wire shape of <see cref="Carbide.Core.Services.BuildResult"/>. PE and PDB bytes are
/// base64-encoded so the JSExport string pipeline can carry them without custom
/// <c>Uint8Array</c> marshalling (M4 plan §5 D38).
/// </summary>
internal sealed class BuildResultDto
{
    public int SchemaVersion { get; set; } = 2;
    public bool Success { get; set; }
    public string? PeBase64 { get; set; }
    public string? PdbBase64 { get; set; }
    public Carbide.Core.Services.Diagnostic[] Diagnostics { get; set; } = [];
    public double DurationMs { get; set; }
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
[JsonSerializable(typeof(BuildResultDto))]
[JsonSerializable(typeof(ProjectOptionsDto))]
internal sealed partial class CarbideJsonContext : JsonSerializerContext
{
}
