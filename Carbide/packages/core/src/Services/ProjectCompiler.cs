// Adapted from WasmSharp.Core.Services.CodeSession. M1 single-document shape:
// the compiler keeps one logical document that AddSource overwrites.
// Extensions over upstream:
//   * Compilation.GetEntryPoint used for entry-point discovery (see WasmSharp#5).
//   * Task / Task<int> / int return types from Main handled.
//   * stderr is captured alongside stdout.
//   * Duration and exit code reported in RunResult.
// Upstream: https://github.com/JakeYallop/WasmSharp (Apache-2.0).

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Carbide.Core.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace Carbide.Core.Services;

internal sealed class ProjectCompiler
{
    private static readonly SourceText EmptySourceText = SourceText.From(string.Empty);

    private readonly ILogger<ProjectCompiler> _logger = Host.Services.GetService<ILogger<ProjectCompiler>>();
    private readonly ProjectId _projectId;
    private readonly string _assemblyName;

    /// <summary>
    /// Default implicit-usings set for console applications. Carbide's M1 shape expects bare
    /// <c>Console.WriteLine("hello")</c> to compile, mirroring the SDK's ImplicitUsings behaviour.
    /// M2 can let callers disable this via ProjectOptions.ImplicitUsings=false.
    /// </summary>
    private const string ImplicitUsingsSource = """
        global using global::System;
        global using global::System.Collections.Generic;
        global using global::System.IO;
        global using global::System.Linq;
        global using global::System.Net.Http;
        global using global::System.Threading;
        global using global::System.Threading.Tasks;
        """;

    private const string ImplicitUsingsDocumentPath = "Carbide.GlobalUsings.g.cs";

    public ProjectCompiler(string? assemblyName = null, DocumentOptions? options = null)
    {
        options ??= DocumentOptions.Default;
        _assemblyName = assemblyName ?? "Carbide.Project";

        Workspace = new AdhocWorkspace();
        _projectId = ProjectId.CreateNewId();
        _documentPath = DefaultDocumentPath;
        _documentId = DocumentId.CreateNewId(_projectId, _documentPath);

        Solution = Workspace.CurrentSolution
            .AddProject(_projectId, _assemblyName, _assemblyName, LanguageNames.CSharp)
            .AddDocument(_documentId, _documentPath, EmptySourceText, filePath: _documentPath);

        // Hidden implicit-usings document so bare Console.WriteLine compiles. The document is
        // not exposed through AddSource / GetSource APIs.
        var implicitUsingsId = DocumentId.CreateNewId(_projectId, ImplicitUsingsDocumentPath);
        Solution = Solution.AddDocument(
            implicitUsingsId,
            ImplicitUsingsDocumentPath,
            SourceText.From(ImplicitUsingsSource),
            filePath: ImplicitUsingsDocumentPath);

        Workspace.OpenDocument(_documentId);
        Solution = Solution.AddMetadataReferences(_projectId, MetadataReferenceCache.MetadataReferences);
        Solution = Solution.WithProjectCompilationOptions(_projectId, options.CSharpCompilationOptions);
        Solution = Solution.WithProjectParseOptions(_projectId, options.CSharpParseOptions);
    }

    public const string DefaultDocumentPath = "Program.cs";

    public AdhocWorkspace Workspace { get; }
    public Solution Solution { get; private set; }

    private readonly DocumentId _documentId;
    private string _documentPath;
    private SourceText _sourceText = EmptySourceText;

    private Document CurrentDocument => Solution.GetDocument(_documentId)
        ?? throw new InvalidOperationException("Document missing from solution.");

    public void AddSource(string path, string code)
    {
        if (!string.Equals(path, _documentPath, StringComparison.Ordinal))
        {
            if (_sourceText.Length != 0)
            {
                _logger.LogWarning(
                    "Carbide M1 supports only one source document per project. Ignoring path '{Path}'; the source at '{Existing}' remains active.",
                    path, _documentPath);
                return;
            }
            _documentPath = path;
            Solution = Solution
                .WithDocumentFilePath(_documentId, path)
                .WithDocumentName(_documentId, path);
        }

        _sourceText = SourceText.From(code);
        Solution = Solution.WithDocumentText(_documentId, _sourceText);
        Workspace.TryApplyChanges(Solution);
    }

    public async Task<Diagnostic[]> GetDiagnosticsAsync()
    {
        using var _ = new Tracer(nameof(GetDiagnosticsAsync));
        var compilation = await CurrentDocument.Project.GetCompilationAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no compilation.");
        return compilation.GetDiagnostics().ToCarbideDiagnosticArray();
    }

    public async Task<RunResult> RunAsync()
    {
        var sw = Stopwatch.StartNew();
        var compilation = await CurrentDocument.Project.GetCompilationAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no compilation.");

        var compileDiagnostics = compilation.GetDiagnostics();
        if (compileDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return RunResult.CompileFailure(compileDiagnostics.ToCarbideDiagnosticArray(), sw.Elapsed.TotalMilliseconds);
        }

        using var peStream = new MemoryStream();
        var emit = compilation.Emit(peStream);
        if (!emit.Success)
        {
            return RunResult.CompileFailure(emit.Diagnostics.ToCarbideDiagnosticArray(), sw.Elapsed.TotalMilliseconds);
        }

        var peBytes = peStream.ToArray();
        var entryPoint = compilation.GetEntryPoint(CancellationToken.None)
            ?? throw new InvalidOperationException("No entry point discovered in compilation.");

        var assembly = LoadAssembly(peBytes);
        var reflectedEntry = assembly.EntryPoint
            ?? throw new InvalidOperationException(
                $"Entry point '{entryPoint.ContainingType}.{entryPoint.Name}' was resolved by Roslyn but not reflected from the emitted assembly.");

        using var stdOutCapture = new StringWriter();
        using var stdErrCapture = new StringWriter();
        var oldOut = Console.Out;
        var oldError = Console.Error;
        Console.SetOut(stdOutCapture);
        Console.SetError(stdErrCapture);

        int exitCode = 0;
        string? uncaught = null;

        try
        {
            var parameters = reflectedEntry.GetParameters();
            var invocationArgs = parameters.Length == 0
                ? Array.Empty<object?>()
                : new object?[] { Array.Empty<string>() };

            object? result;
            try
            {
                result = reflectedEntry.Invoke(null, invocationArgs);
            }
            catch (TargetInvocationException tie) when (tie.InnerException is not null)
            {
                throw tie.InnerException;
            }

            switch (result)
            {
                case Task<int> taskInt:
                    exitCode = await taskInt.ConfigureAwait(false);
                    break;
                case Task task:
                    await task.ConfigureAwait(false);
                    break;
                case int i:
                    exitCode = i;
                    break;
                case ValueTask<int> vti:
                    exitCode = await vti.ConfigureAwait(false);
                    break;
                case ValueTask vt:
                    await vt.ConfigureAwait(false);
                    break;
            }
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031
        {
            uncaught = ex.ToString();
            await stdErrCapture.WriteLineAsync(uncaught).ConfigureAwait(false);
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
        }

        sw.Stop();
        var stdOut = stdOutCapture.ToString();
        var stdErr = stdErrCapture.ToString();

        return uncaught is null
            ? RunResult.Success_(stdOut, stdErr, exitCode, sw.Elapsed.TotalMilliseconds)
            : RunResult.Uncaught(stdOut, stdErr, uncaught, sw.Elapsed.TotalMilliseconds);
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code",
        Justification = "The in-memory emitted assembly is the user's program; reflection into it is expected and not subject to trimming.")]
    private static Assembly LoadAssembly(byte[] bytes) => Assembly.Load(bytes);

    // Parse options used when wiring up a target framework are expected to come through DocumentOptions
    // once M2 grows the option surface. Keeping this helper here makes the expansion site explicit.
    internal static CSharpParseOptions WithLanguageVersion(CSharpParseOptions options, LanguageVersion version)
        => options.WithLanguageVersion(version);
}
