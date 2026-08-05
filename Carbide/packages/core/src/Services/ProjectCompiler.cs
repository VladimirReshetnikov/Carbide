// Adapted from WasmSharp.Core.Services.CodeSession.
// M1 shape (single document) is superseded here by M2's path-keyed document dictionary.
// Extensions over upstream:
//   * Compilation.GetEntryPoint used for entry-point discovery (see WasmSharp#5).
//   * Task / Task<int> / int return types from Main handled.
//   * stderr is captured alongside stdout.
//   * Duration and exit code reported in RunResult.
//   * Multi-document support with AddSource / UpdateSource / RemoveSource (M2).
//   * Hidden implicit-usings document always present so bare Console.WriteLine compiles.
// Upstream: https://github.com/JakeYallop/WasmSharp (Apache-2.0).

using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using Carbide.Core.Hosting;
using Carbide.Terminal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace Carbide.Core.Services;

internal sealed class ProjectCompiler : IDisposable
{
    private readonly ILogger<ProjectCompiler> _logger = Host.Services.GetService<ILogger<ProjectCompiler>>();
    private readonly ReferenceRegistry _referenceRegistry;
    private readonly AnalyzerRegistry? _analyzerRegistry;
    private readonly ProjectId _projectId;
    private readonly string _assemblyName;
    private bool _disposed;

    /// <summary>
    /// Release the underlying <see cref="AdhocWorkspace"/>. Review R1 M6: long-lived
    /// browser sessions that create and tear down many projects accumulate Roslyn caches
    /// on each abandoned workspace; this makes the clean-up explicit rather than relying
    /// on GC to eventually run.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Workspace.Dispose();
    }

    /// <summary>
    /// Default implicit-usings set for console applications. Mirrors the SDK's ImplicitUsings
    /// behaviour so bare <c>Console.WriteLine("hello")</c> compiles without an explicit
    /// <c>using System;</c>.
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

    /// <summary>Reserved path — callers may not Add/Update/Remove this document.</summary>
    internal const string ImplicitUsingsDocumentPath = "Carbide.GlobalUsings.g.cs";

    /// <summary>Default path used when callers don't supply one (kept for M1 compatibility).</summary>
    public const string DefaultDocumentPath = "Program.cs";

    public ProjectCompiler(
        ReferenceRegistry referenceRegistry,
        string? assemblyName = null,
        DocumentOptions? options = null,
        AnalyzerRegistry? analyzerRegistry = null)
    {
        _referenceRegistry = referenceRegistry ?? throw new ArgumentNullException(nameof(referenceRegistry));
        _analyzerRegistry = analyzerRegistry;
        options ??= DocumentOptions.Default;
        // M5: DocumentOptions.AssemblyName takes precedence over the constructor default so
        // .csproj-derived options override the caller's fallback.
        _assemblyName = options.AssemblyName ?? assemblyName ?? "Carbide.Project";

        Workspace = new AdhocWorkspace();
        _projectId = ProjectId.CreateNewId();

        Solution = Workspace.CurrentSolution
            .AddProject(_projectId, _assemblyName, _assemblyName, LanguageNames.CSharp);

        // Hidden implicit-usings document. When the caller opts out via ImplicitUsings=false
        // (set by parseCsproj from <ImplicitUsings>disable</ImplicitUsings>), skip the
        // document entirely so user code that expects strict namespace discipline isn't
        // silently relaxed.
        if (options.ImplicitUsings)
        {
            _implicitUsingsId = DocumentId.CreateNewId(_projectId, ImplicitUsingsDocumentPath);
            Solution = Solution.AddDocument(
                _implicitUsingsId,
                ImplicitUsingsDocumentPath,
                SourceText.From(ImplicitUsingsSource, Encoding.UTF8),
                filePath: ImplicitUsingsDocumentPath);
        }
        else
        {
            _implicitUsingsId = null;
        }

        Solution = Solution.AddMetadataReferences(_projectId, MetadataReferenceCache.MetadataReferences);
        Solution = Solution.WithProjectCompilationOptions(_projectId, options.CSharpCompilationOptions);
        Solution = Solution.WithProjectParseOptions(_projectId, options.CSharpParseOptions);
    }

    public AdhocWorkspace Workspace { get; }
    public Solution Solution { get; private set; }

    private readonly DocumentId? _implicitUsingsId;

    // Path → DocumentId for user-added documents only. The implicit-usings document is
    // intentionally not in this map so it can't leak into AddSource/UpdateSource/RemoveSource.
    private readonly Dictionary<string, DocumentId> _documentsByPath = new(StringComparer.Ordinal);

    // UI-M4: axaml path → DocumentId of the paired auto-generated .g.cs. Tracks only the
    // generated companion; the .axaml content itself is not handed to Roslyn (it would be
    // mis-parsed as C#). The user-facing source lives as a string in _axamlSources and is
    // re-read on UpdateSource to regenerate the companion.
    private readonly Dictionary<string, DocumentId> _axamlCompanionIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _axamlSources = new(StringComparer.Ordinal);

    // Set of attached reference ids (session-scoped). Looked up through _referenceRegistry at
    // rebuild time — if the registry removes a ref while it's still attached here, the next
    // rebuild silently skips the orphan (architecture §3.1).
    private readonly HashSet<string> _attachedReferenceIds = new(StringComparer.Ordinal);

    // M12 — ids of session-registered generator assemblies driving this project's compilation.
    // Kept apart from _attachedReferenceIds because generators must never become metadata
    // references; see AnalyzerRegistry's remarks.
    private readonly HashSet<string> _attachedAnalyzerIds = new(StringComparer.Ordinal);

    /// <summary>
    /// The set of user-visible document paths currently in the project. Includes both
    /// C# sources (backed by Roslyn documents) and .axaml sources (UI-M4; backed by a
    /// generated companion, not a Roslyn document). Auto-generated <c>.g.cs</c>
    /// companions are intentionally excluded — they are an implementation detail.
    /// Callers must not mutate either collection.
    /// </summary>
    public IReadOnlyCollection<string> DocumentPaths
    {
        get
        {
            if (_axamlSources.Count == 0) return _documentsByPath.Keys;
            var combined = new List<string>(_documentsByPath.Count + _axamlSources.Count);
            combined.AddRange(_documentsByPath.Keys);
            combined.AddRange(_axamlSources.Keys);
            return combined;
        }
    }

    public void AddSource(string path, string code)
    {
        ValidatePath(path);
        if (_documentsByPath.ContainsKey(path) || _axamlSources.ContainsKey(path))
        {
            throw new InvalidOperationException(
                $"Document path '{path}' is already in the project; use UpdateSource to replace its content. " +
                "Note: paths are compared byte-for-byte, so casing and slash direction matter.");
        }

        // UI-M4: .axaml documents are paired with a generated .g.cs companion that wires
        // InitializeComponent() to AvaloniaRuntimeXamlLoader.Load. The .axaml itself is
        // not added as a Roslyn document — it would be mis-parsed as C# — but its
        // content is retained for UpdateSource-driven regeneration and diagnostics.
        if (IsAxamlPath(path))
        {
            _axamlSources[path] = code;
            var companion = AxamlCompanionGenerator.Generate(path, code);
            if (companion is not null)
            {
                var companionPath = AxamlCompanionGenerator.CompanionPath(path);
                var companionId = DocumentId.CreateNewId(_projectId, companionPath);
                Solution = Solution.AddDocument(
                    companionId, companionPath, SourceText.From(companion, Encoding.UTF8),
                    filePath: companionPath);
                _axamlCompanionIds[path] = companionId;
                Workspace.TryApplyChanges(Solution);
            }
            _logger.LogTrace("AddSource('{Path}') [.axaml] -> companion {HasCompanion}.",
                path, _axamlCompanionIds.ContainsKey(path));
            return;
        }

        var id = DocumentId.CreateNewId(_projectId, path);
        // Encoding is required so portable-PDB emission in BuildAsync can record source
        // offsets. Without it, Roslyn surfaces CS8055 at emit time.
        Solution = Solution.AddDocument(id, path, SourceText.From(code, Encoding.UTF8), filePath: path);
        _documentsByPath[path] = id;
        Workspace.TryApplyChanges(Solution);
        _logger.LogTrace("AddSource('{Path}') -> now {Count} user document(s).", path, _documentsByPath.Count);
    }

    private static bool IsAxamlPath(string path) =>
        path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase);

    public void UpdateSource(string path, string code)
    {
        ValidatePath(path);

        // UI-M4: an .axaml update regenerates the paired .g.cs companion. If the updated
        // XAML removes x:Class, the existing companion is dropped (the .axaml becomes a
        // manual-load artefact for this update). Adding x:Class back on a later update
        // re-creates the companion.
        if (IsAxamlPath(path))
        {
            if (!_axamlSources.ContainsKey(path))
            {
                throw new InvalidOperationException(
                    $"Unknown document path '{path}'; call AddSource first. " +
                    "Note: paths are compared byte-for-byte, so casing and slash direction matter.");
            }
            _axamlSources[path] = code;
            var companion = AxamlCompanionGenerator.Generate(path, code);
            if (_axamlCompanionIds.TryGetValue(path, out var existingId))
            {
                if (companion is null)
                {
                    Solution = Solution.RemoveDocument(existingId);
                    _axamlCompanionIds.Remove(path);
                }
                else
                {
                    Solution = Solution.WithDocumentText(existingId, SourceText.From(companion, Encoding.UTF8));
                }
            }
            else if (companion is not null)
            {
                var companionPath = AxamlCompanionGenerator.CompanionPath(path);
                var newId = DocumentId.CreateNewId(_projectId, companionPath);
                Solution = Solution.AddDocument(
                    newId, companionPath, SourceText.From(companion, Encoding.UTF8),
                    filePath: companionPath);
                _axamlCompanionIds[path] = newId;
            }
            Workspace.TryApplyChanges(Solution);
            _logger.LogTrace("UpdateSource('{Path}') [.axaml] -> companion {HasCompanion}.",
                path, _axamlCompanionIds.ContainsKey(path));
            return;
        }

        if (!_documentsByPath.TryGetValue(path, out var id))
        {
            throw new InvalidOperationException(
                $"Unknown document path '{path}'; call AddSource first. " +
                "Note: paths are compared byte-for-byte, so casing and slash direction matter.");
        }

        Solution = Solution.WithDocumentText(id, SourceText.From(code, Encoding.UTF8));
        Workspace.TryApplyChanges(Solution);
        _logger.LogTrace("UpdateSource('{Path}').", path);
    }

    /// <summary>
    /// Attaches a session-registered reference to this project. Idempotent — attaching the
    /// same id twice is a no-op.
    /// </summary>
    public void AttachReference(string referenceId)
    {
        if (string.IsNullOrEmpty(referenceId))
        {
            throw new ArgumentException("Reference id must be non-empty.", nameof(referenceId));
        }
        if (!_referenceRegistry.Contains(referenceId))
        {
            throw new InvalidOperationException(
                $"Unknown reference id '{referenceId}'. Register bytes via AddReference first.");
        }
        if (_attachedReferenceIds.Add(referenceId))
        {
            RebuildMetadataReferences();
            _logger.LogTrace("AttachReference('{Id}') -> now {Count} attached.", referenceId, _attachedReferenceIds.Count);
        }
    }

    /// <summary>
    /// M12 — attaches a session-registered source generator to this project. Idempotent.
    ///
    /// <para>Generators are compile-time tools, so unlike <see cref="AttachReference"/> this
    /// does not touch the compilation's metadata references: attaching a generator must not
    /// make its types visible to user code.</para>
    /// </summary>
    public void AttachAnalyzer(string analyzerId)
    {
        if (string.IsNullOrEmpty(analyzerId))
        {
            throw new ArgumentException("Analyzer id must be non-empty.", nameof(analyzerId));
        }
        if (_analyzerRegistry is null || !_analyzerRegistry.Contains(analyzerId))
        {
            throw new InvalidOperationException(
                $"Unknown analyzer id '{analyzerId}'. Register bytes via AddAnalyzer first.");
        }
        if (_attachedAnalyzerIds.Add(analyzerId))
        {
            _logger.LogTrace("AttachAnalyzer('{Id}') -> now {Count} attached.", analyzerId, _attachedAnalyzerIds.Count);
        }
    }

    /// <summary>Detaches a generator from this project. No-op if not attached.</summary>
    public void DetachAnalyzer(string analyzerId)
    {
        if (_attachedAnalyzerIds.Remove(analyzerId))
        {
            _logger.LogTrace("DetachAnalyzer('{Id}') -> now {Count} attached.", analyzerId, _attachedAnalyzerIds.Count);
        }
    }

    /// <summary>
    /// Detaches a reference from this project. No-op if not attached. Called by SessionSolutions
    /// when a reference is removed from the session registry, so every affected project's
    /// solution drops the stale reference on the spot.
    /// </summary>
    public void DetachReference(string referenceId)
    {
        if (_attachedReferenceIds.Remove(referenceId))
        {
            RebuildMetadataReferences();
            _logger.LogTrace("DetachReference('{Id}') -> now {Count} attached.", referenceId, _attachedReferenceIds.Count);
        }
    }

    private void RebuildMetadataReferences()
    {
        // Compile-time references = built-in BCL cache ∪ currently-attached user references.
        // Orphaned ids (attached but removed from the registry) are silently skipped.
        var refs = new List<MetadataReference>(MetadataReferenceCache.MetadataReferences);
        foreach (var id in _attachedReferenceIds)
        {
            if (_referenceRegistry.TryGet(id, out var reference))
            {
                refs.Add(reference);
            }
        }
        Solution = Solution.WithProjectMetadataReferences(_projectId, refs);
        Workspace.TryApplyChanges(Solution);
    }

    public void RemoveSource(string path)
    {
        ValidatePath(path);

        // UI-M4: dropping an .axaml drops its paired companion too.
        if (IsAxamlPath(path) && _axamlSources.ContainsKey(path))
        {
            if (_axamlCompanionIds.TryGetValue(path, out var companionId))
            {
                Solution = Solution.RemoveDocument(companionId);
                _axamlCompanionIds.Remove(path);
            }
            _axamlSources.Remove(path);
            Workspace.TryApplyChanges(Solution);
            _logger.LogTrace("RemoveSource('{Path}') [.axaml].", path);
            return;
        }

        if (!_documentsByPath.TryGetValue(path, out var id))
        {
            // Silent no-op per architecture M2 D16: teardown code commonly tries to clean
            // multiple files without knowing which survived the last failure.
            _logger.LogTrace("RemoveSource('{Path}'): not present, no-op.", path);
            return;
        }

        Solution = Solution.RemoveDocument(id);
        _documentsByPath.Remove(path);
        Workspace.TryApplyChanges(Solution);
        _logger.LogTrace("RemoveSource('{Path}') -> now {Count} user document(s).", path, _documentsByPath.Count);
    }

    private static void ValidatePath(string path)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }
        if (path.Length == 0)
        {
            throw new ArgumentException("Document path must not be empty.", nameof(path));
        }
        if (string.Equals(path, ImplicitUsingsDocumentPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{ImplicitUsingsDocumentPath}' is a reserved path owned by Carbide's implicit-usings machinery.");
        }
    }

    public async Task<Diagnostic[]> GetDiagnosticsAsync()
    {
        using var _ = new Tracer(nameof(GetDiagnosticsAsync));
        // OutputKind is inferred, matching BuildAsync, so library projects (no top-level
        // statements, no Main) don't trip CS5001 during validate. Aligns `validate` with
        // `build` for multi-project graphs where libraries are routine.
        var generated = await GetGeneratedCompilationAsync(outputKind: null).ConfigureAwait(false);
        return generated.Compilation.GetDiagnostics()
            .Concat(generated.GeneratorDiagnostics)
            .ToCarbideDiagnosticArray();
    }

    /// <summary>
    /// A compilation with every attached source generator's output folded in, alongside the
    /// diagnostics the generator pass itself produced.
    /// </summary>
    private readonly record struct GeneratedCompilation(
        Compilation Compilation,
        ImmutableArray<Microsoft.CodeAnalysis.Diagnostic> GeneratorDiagnostics);

    /// <summary>
    /// Fetch the project's compilation, run the attached generators over it, and settle the
    /// output kind.
    ///
    /// <para>Order matters in both directions. Generators run before <see cref="InferOutputKind"/>
    /// because a generator may be what supplies <c>Main</c> — inferring first would compile
    /// such a project to a library. And the output kind is applied after generation rather
    /// than before, so this stays a single generator pass; re-running generators for the
    /// inference probe would double-report every generator diagnostic.</para>
    ///
    /// <para><paramref name="outputKind"/> of <c>null</c> means infer; the run paths pass
    /// <see cref="OutputKind.ConsoleApplication"/> explicitly because they are about to look
    /// for an entry point.</para>
    /// </summary>
    private async Task<GeneratedCompilation> GetGeneratedCompilationAsync(OutputKind? outputKind)
    {
        var project = Solution.GetProject(_projectId)
            ?? throw new InvalidOperationException("Project missing from solution.");
        var compilation = await project.GetCompilationAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no compilation.");

        var (generated, generatorDiagnostics) = RunGenerators(compilation);

        var kind = outputKind ?? InferOutputKind(generated);
        if (generated.Options is CSharpCompilationOptions csOptions && csOptions.OutputKind != kind)
        {
            generated = generated.WithOptions(csOptions.WithOutputKind(kind));
        }

        // Analyzers run after generation and after the output kind is settled, so a rule that
        // inspects generated code or the compilation's kind sees the final picture — which is
        // what it would see under the SDK.
        var analyzerDiagnostics = await RunDiagnosticAnalyzersAsync(generated).ConfigureAwait(false);
        return new GeneratedCompilation(generated, generatorDiagnostics.AddRange(analyzerDiagnostics));
    }

    /// <summary>
    /// Run the attached diagnostic analyzers over <paramref name="compilation"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>concurrentAnalysis: false</c> because a single-threaded runtime has no parallelism to
    /// win and asking for it only widens what the driver may schedule. To be precise about what
    /// that is and is not: it was *not* required to make analyzers work. Both settings were
    /// measured, on Node and in headless Chromium, and both passed — so this is a deliberately
    /// conservative default, not a workaround for a failure anyone has seen. Analyzers that call
    /// <c>EnableConcurrentExecution()</c> (most real ones do) are unaffected either way; the
    /// host's setting is what decides.
    /// </para>
    /// <para>
    /// An analyzer that throws is reported through <c>onAnalyzerException</c> as a diagnostic
    /// rather than taking the compilation down; a rule that crashes should cost its own
    /// results, not the build.
    /// </para>
    /// </remarks>
    private async Task<ImmutableArray<Microsoft.CodeAnalysis.Diagnostic>> RunDiagnosticAnalyzersAsync(
        Compilation compilation)
    {
        if (_attachedAnalyzerIds.Count == 0 || _analyzerRegistry is null)
        {
            return ImmutableArray<Microsoft.CodeAnalysis.Diagnostic>.Empty;
        }

        var analyzers = new List<DiagnosticAnalyzer>();
        foreach (var id in _attachedAnalyzerIds)
        {
            analyzers.AddRange(_analyzerRegistry.GetDiagnosticAnalyzers(id));
        }
        if (analyzers.Count == 0)
        {
            return ImmutableArray<Microsoft.CodeAnalysis.Diagnostic>.Empty;
        }

        var failures = ImmutableArray.CreateBuilder<Microsoft.CodeAnalysis.Diagnostic>();
        try
        {
            var options = new CompilationWithAnalyzersOptions(
                options: new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty),
                onAnalyzerException: (ex, analyzer, diagnostic) =>
                {
                    _logger.LogWarning(
                        "Analyzer '{Analyzer}' threw: {Message}", analyzer.GetType().FullName, ex.Message);
                    failures.Add(Microsoft.CodeAnalysis.Diagnostic.Create(
                        AnalyzerFailedDescriptor,
                        Location.None,
                        analyzer.GetType().FullName,
                        $"{ex.GetType().Name}: {ex.Message}"));
                },
                concurrentAnalysis: false,
                logAnalyzerExecutionTime: false);

            var withAnalyzers = compilation.WithAnalyzers([.. analyzers], options);
            var diagnostics = await withAnalyzers
                .GetAnalyzerDiagnosticsAsync(CancellationToken.None)
                .ConfigureAwait(false);
            return diagnostics.AddRange(failures);
        }
#pragma warning disable CA1031 // a broken driver must not take down the session
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning("Diagnostic-analyzer driver failed: {Message}", ex.Message);
            return failures.ToImmutable().Add(Microsoft.CodeAnalysis.Diagnostic.Create(
                AnalyzerDriverFailedDescriptor,
                Location.None,
                $"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    private static readonly DiagnosticDescriptor AnalyzerFailedDescriptor = new(
        id: "CARBIDE_GEN002",
        title: "Diagnostic analyzer threw",
        messageFormat: "Analyzer '{0}' threw and its results were discarded: {1}",
        category: "Carbide",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor AnalyzerDriverFailedDescriptor = new(
        id: "CARBIDE_GEN003",
        title: "Diagnostic analyzer driver failed",
        messageFormat: "The diagnostic-analyzer driver failed, so no analyzer ran: {0}",
        category: "Carbide",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// M12 — drive the attached source generators over <paramref name="compilation"/> and
    /// return the augmented compilation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>RunGeneratorsAndUpdateCompilation</c> already contains a throwing generator and
    /// reports it as CS8785, so a badly behaved generator degrades to a diagnostic rather than
    /// taking down the session. The try/catch here covers the driver itself — a failure to
    /// even construct or start it — and reports it the same way, because returning the
    /// ungenerated compilation instead would leave the user staring at errors about types
    /// their generator was supposed to have produced.
    /// </para>
    /// <para>
    /// Carbide does not attempt to pre-screen generators for filesystem or network access.
    /// There is no reliable static test for it, and a wrong verdict either way is worse than
    /// none: refusing a well-behaved generator blocks real work, and clearing a misbehaving
    /// one proves nothing. The browser runtime has no filesystem to reach in the first place,
    /// so a generator that tries lands as a CS8785 naming the exception.
    /// </para>
    /// </remarks>
    private (Compilation Compilation, ImmutableArray<Microsoft.CodeAnalysis.Diagnostic> Diagnostics) RunGenerators(
        Compilation compilation)
    {
        if (_attachedAnalyzerIds.Count == 0 || _analyzerRegistry is null)
        {
            return (compilation, ImmutableArray<Microsoft.CodeAnalysis.Diagnostic>.Empty);
        }

        var generators = new List<ISourceGenerator>();
        foreach (var id in _attachedAnalyzerIds)
        {
            // Orphaned ids (attached, then removed from the session registry) contribute
            // nothing, matching RebuildMetadataReferences' handling of the same race.
            generators.AddRange(_analyzerRegistry.GetGenerators(id));
        }
        if (generators.Count == 0)
        {
            return (compilation, ImmutableArray<Microsoft.CodeAnalysis.Diagnostic>.Empty);
        }

        try
        {
            // Parse options are taken from the project so generated source is parsed at the
            // same LangVersion as user code. Without this the driver falls back to the
            // default, and a generator emitting modern syntax would fail to parse in a
            // project pinned to an older language version — an error pointing at generated
            // code the user never wrote.
            var parseOptions = Solution.GetProject(_projectId)?.ParseOptions as CSharpParseOptions;
            var driver = CSharpGeneratorDriver.Create(
                generators: generators,
                additionalTexts: null,
                parseOptions: parseOptions,
                optionsProvider: null);

            driver.RunGeneratorsAndUpdateCompilation(
                compilation,
                out var updated,
                out var diagnostics,
                CancellationToken.None);
            return (updated, diagnostics);
        }
#pragma warning disable CA1031 // a broken driver must not take down the session
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning("Source-generator driver failed: {Message}", ex.Message);
            var descriptor = new DiagnosticDescriptor(
                id: "CARBIDE_GEN001",
                title: "Source generator driver failed",
                messageFormat: "The source-generator driver failed and no generated source was produced: {0}",
                category: "Carbide",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true);
            return (
                compilation,
                ImmutableArray.Create(Microsoft.CodeAnalysis.Diagnostic.Create(
                    descriptor, Location.None, $"{ex.GetType().Name}: {ex.Message}")));
        }
    }

    /// <summary>
    /// Emits the compilation as PE + portable-PDB bytes. Returns a <see cref="BuildResult"/>
    /// that is ready to hand to JS callers through <c>CompilationInterop.BuildAsync</c>.
    ///
    /// <para>Auto-selects <see cref="OutputKind.ConsoleApplication"/> if any syntax tree has
    /// top-level statements (C# 9+) or a suitable <c>Main</c>, otherwise
    /// <see cref="OutputKind.DynamicallyLinkedLibrary"/>. This lets both library code
    /// ("just build a DLL of these types") and top-level-statements apps build via the
    /// same entry point without caller configuration (M5 bugfix for CS8805).</para>
    /// </summary>
    public async Task<BuildResult> BuildAsync()
    {
        var sw = Stopwatch.StartNew();
        // `null` = infer the output kind, which now happens inside the single generator pass
        // rather than from a separate pre-generator compilation. Inferring from the
        // ungenerated compilation would have compiled a project whose Main comes from a
        // generator to a library.
        var (compilation, preEmitDiagnostics) = await TryGetErrorFreeCompilationAsync(outputKind: null).ConfigureAwait(false);
        if (compilation is null)
        {
            sw.Stop();
            return BuildResult.Failed(preEmitDiagnostics, sw.Elapsed.TotalMilliseconds);
        }

        var (pe, pdb, emitDiagnostics) = EmitPeAndPdb(compilation);
        sw.Stop();
        return pe is null
            ? BuildResult.Failed(emitDiagnostics, sw.Elapsed.TotalMilliseconds)
            : BuildResult.Succeeded(pe, pdb!, sw.Elapsed.TotalMilliseconds, compilation.AssemblyName);
    }

    private static OutputKind InferOutputKind(Compilation compilation)
    {
        // Top-level statements are unambiguous — any file with `GlobalStatementSyntax`
        // forces a console app.
        foreach (var tree in compilation.SyntaxTrees)
        {
            var roslynRoot = tree.GetRoot();
            foreach (var member in roslynRoot.ChildNodes())
            {
                if (member is Microsoft.CodeAnalysis.CSharp.Syntax.GlobalStatementSyntax)
                {
                    return OutputKind.ConsoleApplication;
                }
            }
        }

        // Review R1 §5 / R2 §4: without this check a source set that uses an explicit
        // `static Main(...)` but no top-level statements compiled to a DLL under
        // `project.build()`, while `project.run()` (which forces ConsoleApplication)
        // could still execute it — so the same sources produced different-shaped
        // artefacts depending on the entry point. Ask Roslyn for the entry point under
        // a speculative ConsoleApplication compilation; if one exists, use ConsoleApplication.
        var consoleProbe = compilation.WithOptions(
            compilation.Options.WithOutputKind(OutputKind.ConsoleApplication));
        return consoleProbe.GetEntryPoint(CancellationToken.None) is not null
            ? OutputKind.ConsoleApplication
            : OutputKind.DynamicallyLinkedLibrary;
    }

    /// <summary>
    /// P0.1 (post-M9 usability plan §2.1) — the output-side capture bypass.
    ///
    /// <para>Carbide captures program output with <see cref="Console.SetOut"/>. Writes made
    /// through the handle-level streams (<c>Console.OpenStandardOutput()</c> /
    /// <c>OpenStandardError()</c>) never touch <c>Console.Out</c>: Mono-WASM sends them
    /// straight down the file-descriptor path, so they surface on the host process's real
    /// stdio and are absent from the returned <see cref="RunResult"/>.</para>
    ///
    /// <para>The plan's preferred fix was to intercept at the runtime's fd layer. That is
    /// not reachable through a supported extension point: on the Node host neither the
    /// emscripten <c>print</c>/<c>printErr</c> overlays (which the browser's T1 terminal
    /// bridge relies on) nor a <c>preRun</c> hook fire for these writes. So Carbide takes
    /// the plan's warning-first path (U1.D5) with a reliable detector — Roslyn already knows
    /// exactly which call sites reach these APIs — rather than a brittle runtime patch.</para>
    /// </summary>
    private static readonly DiagnosticDescriptor CaptureBypassOutputRule = new(
        id: "MSCAP001",
        title: "Console output bypasses Carbide's capture",
        messageFormat:
            "'Console.{0}()' writes past Carbide's output capture. The bytes reach the host process's "
            + "{1} directly and will be missing from RunResult.{2}. Use Console.{3} for captured output, "
            + "or Project.runInteractive, where the terminal bridge does receive handle-level writes.",
        category: "Carbide.Capture",
        defaultSeverity: Microsoft.CodeAnalysis.DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// The input-side mirror of <see cref="CaptureBypassOutputRule"/>. Carbide seeds program
    /// input by replacing <see cref="Console.In"/>; <c>Console.OpenStandardInput()</c> hands
    /// back the runtime's own stream, which is never connected to it. Unlike the output side
    /// this applies to interactive runs too — T2 patches <c>Console.In</c>, not the handle.
    /// </summary>
    private static readonly DiagnosticDescriptor CaptureBypassInputRule = new(
        id: "MSCAP002",
        title: "Console input bypasses Carbide's stdin plumbing",
        messageFormat:
            "'Console.OpenStandardInput()' reads past Carbide's stdin plumbing. Carbide seeds program "
            + "input through Console.In, which this handle does not observe, so reads see no data. "
            + "Use Console.In / Console.ReadLine() instead.",
        category: "Carbide.Capture",
        defaultSeverity: Microsoft.CodeAnalysis.DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// Reports every call site in the user's sources that reaches a stream-handle API which
    /// bypasses Carbide's capture. Binding is only attempted for invocations whose simple
    /// name already matches, so the common case costs one syntax walk and no semantic work.
    /// </summary>
    /// <param name="interactive">
    /// When true, the output-side rule is suppressed: the interactive path installs
    /// emscripten <c>print</c>/<c>printErr</c> multiplexers that do route handle-level
    /// writes into the terminal bridge (T1.3 DT-T1.6). The input-side rule still applies.
    /// </param>
    private static Diagnostic[] DetectCaptureBypasses(Compilation compilation, bool interactive)
    {
        var consoleType = compilation.GetTypeByMetadataName("System.Console");
        if (consoleType is null)
        {
            return [];
        }

        List<Microsoft.CodeAnalysis.Diagnostic>? found = null;
        foreach (var tree in compilation.SyntaxTrees)
        {
            // The implicit-usings document is Carbide's own and contains only directives.
            if (tree.FilePath == ImplicitUsingsDocumentPath)
            {
                continue;
            }

            SemanticModel? model = null;
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                if (node is not Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax invocation)
                {
                    continue;
                }

                var simpleName = InvokedSimpleName(invocation);
                var rule = simpleName switch
                {
                    "OpenStandardOutput" or "OpenStandardError" when !interactive => CaptureBypassOutputRule,
                    "OpenStandardInput" => CaptureBypassInputRule,
                    _ => null,
                };
                if (rule is null)
                {
                    continue;
                }

                // Only now is semantic binding worth its cost — and it is required, because
                // the name alone could belong to any type the user happens to define.
                model ??= compilation.GetSemanticModel(tree);
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
                    || !SymbolEqualityComparer.Default.Equals(method.ContainingType, consoleType))
                {
                    continue;
                }

                var arguments = simpleName == "OpenStandardError"
                    ? new object[] { simpleName, "stderr", "stdErr", "Error.Write" }
                    : new object[] { simpleName, "stdout", "stdOut", "Write" };
                found ??= [];
                found.Add(Microsoft.CodeAnalysis.Diagnostic.Create(
                    rule,
                    invocation.GetLocation(),
                    rule == CaptureBypassInputRule ? [] : arguments));
            }
        }

        return found is null ? [] : found.ToCarbideDiagnosticArray();
    }

    /// <summary>The invoked member's simple name, for both `X.Y()` and bare `Y()` forms.</summary>
    private static string? InvokedSimpleName(Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax invocation)
        => invocation.Expression switch
        {
            Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            Microsoft.CodeAnalysis.CSharp.Syntax.MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
            _ => null,
        };

    /// <summary>
    /// Fetches the compilation with the requested output kind, bails with diagnostics when
    /// pre-emit errors exist, and otherwise returns the compilation for emission or
    /// execution. Centralises the "get compilation + filter for errors" dance that both
    /// <see cref="BuildAsync"/> and <see cref="RunAsync"/> perform, and lets each call
    /// control whether CS5001 "no suitable Main" is an error or not.
    /// </summary>
    private async Task<(Compilation? Compilation, Diagnostic[] Diagnostics)> TryGetErrorFreeCompilationAsync(
        OutputKind? outputKind)
    {
        var generated = await GetGeneratedCompilationAsync(outputKind).ConfigureAwait(false);
        var compilation = generated.Compilation;

        // Generator diagnostics are folded in with the compiler's own, so a generator that
        // failed is reported as the reason the build failed rather than showing up only as
        // the downstream errors its missing output caused.
        var diagnostics = compilation.GetDiagnostics().Concat(generated.GeneratorDiagnostics).ToList();
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return (null, diagnostics.ToCarbideDiagnosticArray());
        }
        return (compilation, Array.Empty<Diagnostic>());
    }

    /// <summary>
    /// Emits PE + portable-PDB from an already-validated compilation. On emit failure, PE is
    /// null and the emit diagnostics come back instead.
    /// </summary>
    private static (byte[]? Pe, byte[]? Pdb, Diagnostic[] Diagnostics) EmitPeAndPdb(Compilation compilation)
    {
        using var peStream = new MemoryStream();
        using var pdbStream = new MemoryStream();
        var options = new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb);
        var emit = compilation.Emit(peStream, pdbStream, options: options);
        if (!emit.Success)
        {
            return (null, null, emit.Diagnostics.ToCarbideDiagnosticArray());
        }
        return (peStream.ToArray(), pdbStream.ToArray(), Array.Empty<Diagnostic>());
    }

    public Task<RunResult> RunAsync() => RunAsync(Array.Empty<string>(), null);

    /// <summary>
    /// U2 — compile and run. <paramref name="args"/> is forwarded to the entry point's
    /// <c>Main(string[])</c> parameter (bound by parameter count: 1 string[] = forward,
    /// otherwise ignored). <paramref name="stdin"/> pre-seeds <see cref="Console.In"/> with
    /// a <see cref="System.IO.StringReader"/> when non-null; <see cref="Console.In"/> is
    /// restored in the finally block alongside stdout/stderr.
    /// </summary>
    public async Task<RunResult> RunAsync(string[] args, string? stdin)
    {
        var sw = Stopwatch.StartNew();
        var (compilation, preEmitDiagnostics) = await TryGetErrorFreeCompilationAsync(OutputKind.ConsoleApplication).ConfigureAwait(false);
        if (compilation is null)
        {
            return RunResult.CompileFailure(preEmitDiagnostics, sw.Elapsed.TotalMilliseconds);
        }

        var (peBytes, _, emitDiagnostics) = EmitPeAndPdb(compilation);
        if (peBytes is null)
        {
            return RunResult.CompileFailure(emitDiagnostics, sw.Elapsed.TotalMilliseconds);
        }

        // P0.1 — computed before the run so the advisory survives an uncaught exception.
        var bypassDiagnostics = DetectCaptureBypasses(compilation, interactive: false);

        var entryPoint = compilation.GetEntryPoint(CancellationToken.None)
            ?? throw new InvalidOperationException("No entry point discovered in compilation.");

        // core-P2 (plan §10.2): load the user PE and its references into a per-run
        // collectible AssemblyLoadContext. Previously used Assembly.Load(byte[]), which
        // lands in the default ALC and accumulates across runs — browser sessions that
        // built/ran many programs leaked every prior PE. A per-run collectible ALC frees
        // the assemblies when RunAsync returns (or throws) so memory usage stays bounded.
        // Also prerequisite for the UI-M3 runner's teardown/reload flow.
        var runContext = new AssemblyLoadContext(
            name: $"CarbideRun-{Guid.NewGuid():N}",
            isCollectible: true);

        // Pre-load every attached reference so the runtime can resolve them by identity when
        // the user's PE references their types. Key the loaded assemblies by simple name so
        // the Resolving handler below can answer requests that arrive during JIT.
        var loadedReferences = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        foreach (var refId in _attachedReferenceIds)
        {
            if (!_referenceRegistry.TryGetBytes(refId, out var refBytes))
            {
                continue;
            }
            try
            {
                var refAssembly = LoadAssembly(runContext, refBytes);
                var simpleName = refAssembly.GetName().Name;
                if (!string.IsNullOrEmpty(simpleName))
                {
                    loadedReferences[simpleName] = refAssembly;
                }
            }
            catch (Exception ex) when (ex is FileLoadException or BadImageFormatException)
            {
                _logger.LogWarning(
                    "Attached reference '{Id}' could not be loaded into the run context: {Message}",
                    refId, ex.Message);
            }
        }

        // Mono-WASM's default AssemblyLoadContext does not always find assemblies loaded via
        // LoadFromStream when resolving references from a later LoadFromStream. A Resolving
        // handler scoped to the run context fills the gap by answering by simple name.
        Func<AssemblyLoadContext, AssemblyName, Assembly?> resolveHandler = (_, name) =>
        {
            var simpleName = name.Name;
            return simpleName is not null && loadedReferences.TryGetValue(simpleName, out var found)
                ? found
                : null;
        };
        // Wrap everything from the Resolving subscription onward in a try/finally so the
        // handler is detached and the ALC is unloaded regardless of where in the path a
        // failure lands. Review R1 C1 / R2 §1: a throw in LoadAssembly or Console.SetOut
        // used to leak the handler into subsequent runs ("everything is weird after one
        // failed run") — scoping to the run context eliminates that class of bug entirely
        // because the whole context goes away after each call.
        runContext.Resolving += resolveHandler;
        using var stdOutCapture = new StringWriter();
        using var stdErrCapture = new StringWriter();

        int exitCode = 0;
        string? uncaught = null;

        try
        {
            var assembly = LoadAssembly(runContext, peBytes);
            var declaredEntry = assembly.EntryPoint
                ?? throw new InvalidOperationException(
                    $"Entry point '{entryPoint.ContainingType}.{entryPoint.Name}' was resolved by Roslyn but not reflected from the emitted assembly.");
            // T2.1 — bypass the synthesised sync wrapper for `async Task Main` and top-level-
            // statements-with-`await`. See `ResolveAsyncEntryOrFallback` at the bottom of this
            // class for the full rationale.
            var reflectedEntry = ResolveAsyncEntryOrFallback(declaredEntry);

            var oldOut = Console.Out;
            var oldError = Console.Error;
            // U2: Mono-WASM marks Console.In / Console.SetIn with UnsupportedOSPlatform("browser")
            // AND throws PlatformNotSupportedException at runtime when these APIs touch the
            // real stdin handle. Setting the internal static field via reflection bypasses
            // both the code-analysis warning and the runtime guard: once the field is non-null,
            // the getter returns our TextReader without going through EnsureInitialized.
            System.IO.TextReader? oldIn = null;
            if (stdin is not null)
            {
                oldIn = GetConsoleInField();
                SetConsoleInField(new System.IO.StringReader(stdin));
            }
            Console.SetOut(stdOutCapture);
            Console.SetError(stdErrCapture);

            try
            {
                var parameters = reflectedEntry.GetParameters();
                // U2: bind by parameter count. `Main()` gets no args; `Main(string[] args)`
                // (including Roslyn's synthesised top-level-statements wrapper) gets the
                // forwarded array. Any other shape falls back to the empty-array behaviour.
                var invocationArgs = parameters.Length == 0
                    ? Array.Empty<object?>()
                    : new object?[] { args };

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
                if (stdin is not null)
                {
                    // Restore whatever was there before (including null — the default "never
                    // initialised" state on Mono-WASM).
                    SetConsoleInField(oldIn);
                }
                // Drop this program's Console.CancelKeyPress handlers — the fork holds them
                // in a static field that outlives the run's collectible ALC, and a handler
                // registered here would go on vetoing Ctrl+C in later interactive runs.
                TerminalInputState.ResetForkedConsoleCancelKeyPress();
            }
        }
        finally
        {
            runContext.Resolving -= resolveHandler;
            // Mark the context unloadable. Actual collection happens when GC runs and
            // no references to types from this context remain — we only hand strings /
            // ints / diagnostics back to the caller, so the `assembly` local and all
            // reference assemblies become unreachable as soon as this frame returns.
            runContext.Unload();
        }

        sw.Stop();
        var stdOut = stdOutCapture.ToString();
        var stdErr = stdErrCapture.ToString();

        return uncaught is null
            ? RunResult.Success_(stdOut, stdErr, exitCode, sw.Elapsed.TotalMilliseconds, bypassDiagnostics)
            : RunResult.Uncaught(stdOut, stdErr, uncaught, sw.Elapsed.TotalMilliseconds, bypassDiagnostics);
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code",
        Justification = "The in-memory emitted assembly is the user's program; reflection into it is expected and not subject to trimming.")]
    private static Assembly LoadAssembly(AssemblyLoadContext context, byte[] bytes)
    {
        // Wrap the byte[] in a MemoryStream for AssemblyLoadContext.LoadFromStream. The
        // MemoryStream wraps the existing array without copying; after the call returns
        // the ALC has internalised the image bytes and the stream can be disposed.
        using var stream = new MemoryStream(bytes, writable: false);
        return context.LoadFromStream(stream);
    }

    // U2 — reflection into Console's internal `s_in` (or `_in`) field. Bypasses both the
    // `[UnsupportedOSPlatform("browser")]` code-analysis guard and the runtime-side
    // PlatformNotSupportedException that Console.In / SetIn throw on Mono-WASM. The field
    // name differs between .NET versions; we probe the two historic names and cache the
    // resolved FieldInfo (once resolved, it's cheap). Returns null when the field is not
    // found — in that case stdin forwarding silently becomes a no-op and the user program
    // gets the default "empty" Console.In behaviour.
    private static System.Reflection.FieldInfo? s_cachedConsoleInField;
    private static System.Reflection.FieldInfo? ResolveConsoleInField()
    {
        if (s_cachedConsoleInField is not null) return s_cachedConsoleInField;
        var t = typeof(Console);
        var field = t.GetField("s_in", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? t.GetField("_in", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        s_cachedConsoleInField = field;
        return field;
    }

    private static System.IO.TextReader? GetConsoleInField()
    {
        var field = ResolveConsoleInField();
        return field?.GetValue(null) as System.IO.TextReader;
    }

    private static void SetConsoleInField(System.IO.TextReader? value)
    {
        var field = ResolveConsoleInField();
        field?.SetValue(null, value);
    }

    // T2.1 — resolve the user assembly's async entry point, if one exists.
    //
    // `Assembly.EntryPoint` points to the method the CLR considers the entry point.
    // For `static async Task Main` and top-level-statements-with-`await`, Roslyn
    // synthesises a synchronous wrapper that calls `.GetAwaiter().GetResult()` on
    // the real async method's Task — and the wrapper is what `Assembly.EntryPoint`
    // returns. Invoking the wrapper blocks on an infinite `Monitor.Wait` on Mono-WASM
    // single-threaded browser when the task actually suspends, tripping PNSE.
    //
    // If the declared entry point already returns an awaitable (the CLR handles the
    // Task-returning entry directly on newer frameworks), we use it as-is. Otherwise
    // we search the declaring type for a sibling static method with the same
    // parameter signature whose return type IS awaitable; that's the underlying
    // async method the wrapper calls. Invoking THAT and awaiting it in our own
    // async frame puts the wait on our TaskAwaiter path (which resumes via the
    // scheduling pump) instead of the wrapper's blocking one.
    private static System.Reflection.MethodInfo ResolveAsyncEntryOrFallback(
        System.Reflection.MethodInfo declared)
    {
        if (IsAwaitableReturn(declared.ReturnType))
        {
            return declared;
        }

        // The substitution above exists solely to step around Roslyn's *synthesised*
        // wrapper, so it must only ever apply to one. Without this guard the sibling
        // search ran for every non-awaitable entry point, including a user's genuine
        // `static void Main` — and a single unrelated helper such as
        // `static async Task WarmUpAsync()` in the same class was then invoked *instead
        // of Main*, with `success: true`, empty stdout, and no diagnostic. The wrapper is
        // always compiler-generated and always angle-bracket named (`<Main>` for
        // `async Task Main`, `<Main>$` for top-level statements); a method the user
        // actually declared is the one `Assembly.EntryPoint`, `dotnet run`, and the PE
        // emitted by `project.build()` all agree on, so it has to be the one we run.
        if (!IsCompilerGenerated(declared))
        {
            return declared;
        }

        var declaringType = declared.DeclaringType;
        if (declaringType is null)
        {
            return declared;
        }

        var declaredParams = declared.GetParameters();
        var candidates = declaringType
            .GetMethods(System.Reflection.BindingFlags.Static
                      | System.Reflection.BindingFlags.Public
                      | System.Reflection.BindingFlags.NonPublic)
            .Where(m => IsAwaitableReturn(m.ReturnType))
            .Where(m => ParametersMatch(m.GetParameters(), declaredParams))
            // `Type.GetMethods` order is explicitly unspecified, so pin it: otherwise the
            // choice among equally-matching candidates could vary between runtimes.
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToList();

        if (candidates.Count == 0)
        {
            return declared;
        }

        // Exactly one match — that's the underlying async method.
        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        // Multiple — prefer a user-defined (non-compiler-generated) name. Roslyn's
        // top-level-statements wrapper uses angle-bracket names; the underlying async
        // method is sometimes also angle-bracketed (e.g. `<Main>$`) and sometimes the
        // user's declared name (e.g. `Main`). Fall through to any awaitable match.
        var userDefined = candidates.FirstOrDefault(m => !m.Name.Contains('<'));
        return userDefined ?? candidates[0];
    }

    /// <summary>
    /// Whether a method was emitted by the compiler rather than written by the user.
    /// Roslyn marks synthesised entry-point wrappers with
    /// <see cref="System.Runtime.CompilerServices.CompilerGeneratedAttribute"/> and gives them
    /// a name no C# identifier can have, so either signal alone is conclusive.
    /// </summary>
    private static bool IsCompilerGenerated(System.Reflection.MethodInfo method)
        => method.Name.Contains('<')
            || method.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false);

    private static bool IsAwaitableReturn(Type t)
    {
        if (t == typeof(Task) || t == typeof(ValueTask)) return true;
        if (!t.IsGenericType) return false;
        var gtd = t.GetGenericTypeDefinition();
        return gtd == typeof(Task<>) || gtd == typeof(ValueTask<>);
    }

    private static bool ParametersMatch(
        System.Reflection.ParameterInfo[] a,
        System.Reflection.ParameterInfo[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].ParameterType != b[i].ParameterType) return false;
        }
        return true;
    }

    /// <summary>
    /// RAII helper so <c>RunInteractiveAsync</c>'s `using` pattern unwinds the
    /// <see cref="TerminalInputState"/> registry slot deterministically on both the
    /// success and failure paths, without an explicit try/finally pair around the new
    /// state lifetime.
    /// </summary>
    private sealed class InputStateDisposer(TerminalInputState state) : IDisposable
    {
        public void Dispose() => state.Dispose();
    }

    /// <summary>
    /// T1 — compile and run interactively. Mirrors <see cref="RunAsync(string[], string?)"/>
    /// but installs <see cref="Carbide.Terminal.StreamingStdOutWriter"/> instances that push
    /// buffered chunks through <see cref="Carbide.Terminal.CarbideTerminalInterop"/> into
    /// the JS terminal bridge while the program runs. Bytes are teed into a
    /// <see cref="StringBuilder"/> so the returned <see cref="RunResult"/> still carries the
    /// full transcript (parity with <c>project.run()</c>'s return shape). Stderr bytes are
    /// SGR-wrapped per <see cref="Carbide.Terminal.InteractiveOptions.StderrStyle"/> on
    /// their way to the bridge, but the tee captures unwrapped text. T1 is output-only;
    /// <see cref="Console.In"/> stays disconnected — stdin lands in T2.
    /// </summary>
    public async Task<RunResult> RunInteractiveAsync(string projectId, InteractiveOptions options)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        // T2 — Per-run input state: reader for Console.In, resize cache, Ctrl+C
        // handlers, cancellation token. Dispose in the finally block to pull the state
        // out of the projectId->state registry and close the reader (so a pending
        // ReadLineAsync resolves to null/EOF).
        var inputState = TerminalInputState.Create(projectId);
        using var _inputStateDisposer = new InputStateDisposer(inputState);

        // T2 — install CarbideSyncContext. T2.1 Option F experiment removed this and
        // empirically confirmed it did not fix the Assembly.Load-plus-await trap;
        // restored. Review R2 §1: compile/emit failures used to return early while
        // leaving the CarbideSyncContext installed, which corrupted subsequent runs.
        // Restore the old context before every early-return path.
        var oldSyncContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(CarbideSyncContext.Instance);

        var sw = Stopwatch.StartNew();
        var (compilation, preEmitDiagnostics) = await TryGetErrorFreeCompilationAsync(OutputKind.ConsoleApplication).ConfigureAwait(false);
        if (compilation is null)
        {
            SynchronizationContext.SetSynchronizationContext(oldSyncContext);
            return RunResult.CompileFailure(preEmitDiagnostics, sw.Elapsed.TotalMilliseconds);
        }

        var (peBytes, _, emitDiagnostics) = EmitPeAndPdb(compilation);
        if (peBytes is null)
        {
            SynchronizationContext.SetSynchronizationContext(oldSyncContext);
            return RunResult.CompileFailure(emitDiagnostics, sw.Elapsed.TotalMilliseconds);
        }

        // P0.1 — output-side rule suppressed here: on the interactive path handle-level
        // writes do reach the terminal. `Console.OpenStandardOutput()` resolves to the T3
        // forked System.Console's own `CarbideStdWriteStream`, which writes straight to the
        // terminal bridge (confirmed by reflecting the returned stream's type in a browser
        // fixture — it is that stream, not the emscripten print/printErr overlay, that
        // carries these bytes once the fork is loaded).
        var bypassDiagnostics = DetectCaptureBypasses(compilation, interactive: true);

        var entryPoint = compilation.GetEntryPoint(CancellationToken.None)
            ?? throw new InvalidOperationException("No entry point discovered in compilation.");

        // Reference pre-load and Resolving wiring — mirrors RunAsync. core-P2: per-run
        // collectible ALC so interactive runs don't leak PEs or references across sessions
        // either. The duplication with RunAsync is intentional: keeping the two run paths
        // independent is cheaper than factoring out a shared helper that has to carry the
        // entire instance surface (_attachedReferenceIds, _referenceRegistry, _logger).
        // If a third run path lands, revisit the refactor.
        var runContext = new AssemblyLoadContext(
            name: $"CarbideRunInteractive-{Guid.NewGuid():N}",
            isCollectible: true);
        var loadedReferences = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        foreach (var refId in _attachedReferenceIds)
        {
            if (!_referenceRegistry.TryGetBytes(refId, out var refBytes))
            {
                continue;
            }
            try
            {
                var refAssembly = LoadAssembly(runContext, refBytes);
                var simpleName = refAssembly.GetName().Name;
                if (!string.IsNullOrEmpty(simpleName))
                {
                    loadedReferences[simpleName] = refAssembly;
                }
            }
            catch (Exception ex) when (ex is FileLoadException or BadImageFormatException)
            {
                _logger.LogWarning(
                    "Attached reference '{Id}' could not be loaded into the run context: {Message}",
                    refId, ex.Message);
            }
        }

        Func<AssemblyLoadContext, AssemblyName, Assembly?> resolveHandler = (_, name) =>
        {
            var simpleName = name.Name;
            return simpleName is not null && loadedReferences.TryGetValue(simpleName, out var found)
                ? found
                : null;
        };
        // Wrap everything from the Resolving subscription onward in try/finally.
        // Review R1 C1 / R2 §1: a throw in LoadAssembly, EntryPoint reflection, or any of
        // the Console.SetOut / SetConsoleInField calls used to leak the handler and the
        // SynchronizationContext into later runs — scoping Resolving to the run context
        // plus unloading the context in finally closes that class of bug.
        runContext.Resolving += resolveHandler;

        // Tee each flushed chunk into a StringBuilder (for the RunResult trailer) while also
        // pushing it through the JS bridge. Stderr gets its SGR wrap on the JS-bound path
        // only, so the tee captures raw text (matches what user code wrote).
        var stdOutTee = new StringBuilder();
        var stdErrTee = new StringBuilder();
        var stdOutSink = (string text) =>
        {
            stdOutTee.Append(text);
            CarbideTerminalInterop.WriteStdOut(text);
        };
        var stdErrJsSink = StderrSink.Wrap(
            static text => CarbideTerminalInterop.WriteStdErr(text),
            options.StderrStyle);
        var stdErrSink = (string text) =>
        {
            stdErrTee.Append(text);
            stdErrJsSink(text);
        };

        using var stdOutWriter = new StreamingStdOutWriter(stdOutSink);
        using var stdErrWriter = new StreamingStdOutWriter(stdErrSink);

        int exitCode = 0;
        string? uncaught = null;

        try
        {
            var assembly = LoadAssembly(runContext, peBytes);
            var declaredEntry = assembly.EntryPoint
                ?? throw new InvalidOperationException(
                    $"Entry point '{entryPoint.ContainingType}.{entryPoint.Name}' was resolved by Roslyn but not reflected from the emitted assembly.");

            // T2.1 — if the user program is `async Task Main` (or top-level statements with
            // `await`), Roslyn synthesises a synchronous wrapper that calls
            // `.GetAwaiter().GetResult()` on the real async method. `Assembly.EntryPoint`
            // points at THAT wrapper. Calling the wrapper blocks on the task — on Mono-WASM
            // single-threaded browser that blocking wait reaches `Monitor.Wait(INFINITE)` and
            // trips `Cannot wait on monitors on this runtime`. Bypass the wrapper: if the
            // declared entry point is sync-returning (`void`/`int`) and its declaring type has
            // a sibling static method with the same parameter signature that returns
            // Task/Task<int>/ValueTask/ValueTask<int>, invoke THAT instead and await it
            // ourselves. That moves the "wait" out of the blocking BCL path and into our own
            // async method where it resumes via the scheduling pump correctly.
            var reflectedEntry = ResolveAsyncEntryOrFallback(declaredEntry);

            var oldOut = Console.Out;
            var oldError = Console.Error;
            Console.SetOut(stdOutWriter);
            Console.SetError(stdErrWriter);

            // T2 — install the BrowserTerminalReader into Console._in via the U2 reflection path
            // so Console.In.ReadLineAsync() resolves against the same queue CarbideConsole uses.
            // Preserve whatever was in the slot before (null in the normal case) and restore in
            // finally. `SetConsoleInField` is the same helper U2 uses for pre-seeded stdin.
            var oldIn = GetConsoleInField();
            SetConsoleInField(inputState.Reader);

            // T3 — signal the forked System.Console.dll that an interactive bridge is live so
            // the fork's cosmetic emitters (`Console.ForegroundColor`, `Console.Clear()`, etc.)
            // emit ANSI instead of throwing. Outside this region the flag is false and those
            // members throw PlatformNotSupportedException with a "use runInteractive" message
            // — preserving the pre-T3 contract for plain `Project.run` programs.
            AppContext.SetData("Carbide.InteractiveBridge", true);

            // T3.1 defensive — re-install before user-code invoke in case the intervening
            // Roslyn await cleared the SC.
            SynchronizationContext.SetSynchronizationContext(CarbideSyncContext.Instance);

            try
            {
                var parameters = reflectedEntry.GetParameters();
                var invocationArgs = parameters.Length == 0
                    ? Array.Empty<object?>()
                    : new object?[] { options.Args };

                object? result;
                try
                {
                    result = reflectedEntry.Invoke(null, invocationArgs);
                }
                catch (TargetInvocationException tie) when (tie.InnerException is not null)
                {
                    throw tie.InnerException;
                }

                // T3.1 defensive — Do NOT use `ConfigureAwait(false)` here. Mono-WASM browser's
                // default `TaskScheduler` dispatches to an absent thread pool; keeping the
                // captured context (our inline-Post `CarbideSyncContext`) avoids that fall-
                // through. Does not on its own fix T2.1 for suspended-completion awaits.
                switch (result)
                {
                    case Task<int> taskInt:
                        exitCode = await taskInt;
                        break;
                    case Task task:
                        await task;
                        break;
                    case int i:
                        exitCode = i;
                        break;
                    case ValueTask<int> vti:
                        exitCode = await vti;
                        break;
                    case ValueTask vt:
                        await vt;
                        break;
                }
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031
            {
                // Walk inner exceptions so the stderr carries the full chain — useful for
                // diagnosing Mono-WASM async failures where the outer wrapper hides the real
                // throwing site (e.g. TaskSchedulerException -> PlatformNotSupportedException).
                var chain = new StringBuilder();
                for (var e = (Exception?)ex; e is not null; e = e.InnerException)
                {
                    if (chain.Length > 0) chain.AppendLine().AppendLine("--- inner ---");
                    chain.Append(e.GetType().FullName).Append(": ").AppendLine(e.Message);
                    if (e.StackTrace is { } st) chain.AppendLine(st);
                }
                uncaught = chain.ToString();
                await stdErrWriter.WriteLineAsync(uncaught).ConfigureAwait(false);
            }
            finally
            {
                // Drain order: flush the writers first so in-buffer bytes make it to the bridge,
                // then restore Console.{Out,Error,In} to their previous values. Writers are
                // also disposed by the `using` block, which flushes again defensively. The
                // `_inputStateDisposer` runs last (via `using`) so the BrowserTerminalReader
                // completes (wakes a pending ReadLineAsync with EOF) only after Console.In no
                // longer points at it.
                stdOutWriter.FlushNow();
                stdErrWriter.FlushNow();
                Console.SetOut(oldOut);
                Console.SetError(oldError);
                SetConsoleInField(oldIn);
                // T3 — clear the flag so subsequent non-interactive runs see PNS again.
                AppContext.SetData("Carbide.InteractiveBridge", false);
                // Drop the finished program's Console.CancelKeyPress handlers. The forked
                // System.Console keeps that chain in a static field, so it outlives the run's
                // collectible ALC; a handler left behind that sets `e.Cancel = true` vetoes
                // Ctrl+C for every later run on the page.
                TerminalInputState.ResetForkedConsoleCancelKeyPress();
                SynchronizationContext.SetSynchronizationContext(oldSyncContext);
            }
        }
        finally
        {
            runContext.Resolving -= resolveHandler;
            runContext.Unload();
        }

        sw.Stop();
        var stdOut = stdOutTee.ToString();
        var stdErr = stdErrTee.ToString();

        return uncaught is null
            ? RunResult.Success_(stdOut, stdErr, exitCode, sw.Elapsed.TotalMilliseconds, bypassDiagnostics)
            : RunResult.Uncaught(stdOut, stdErr, uncaught, sw.Elapsed.TotalMilliseconds, bypassDiagnostics);
    }
}
