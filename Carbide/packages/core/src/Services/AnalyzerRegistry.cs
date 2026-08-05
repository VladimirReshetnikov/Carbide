// M12 — session-scoped registry of source-generator and diagnostic-analyzer assemblies.
//
// Deliberately separate from ReferenceRegistry rather than a `kind` flag on it. The two are
// not variations of one thing: a metadata reference is part of the program's API surface and
// must be resolvable at run time, while a generator is a compile-time tool that must NOT
// appear in the compilation's references at all. Keeping them apart makes it impossible to
// attach one where the other belongs.

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Carbide.Core.Services;

/// <summary>
/// Holds generator assemblies registered on a session, keyed by a server-assigned id, and
/// the <see cref="ISourceGenerator"/> instances reflected out of each.
/// </summary>
/// <remarks>
/// <para>
/// Generators are instantiated once, at registration, rather than per compilation. That is
/// what a real Roslyn host does — an incremental generator instance is designed to be reused
/// across compilations, and its caches are the point of the incremental model.
/// </para>
/// <para>
/// Discovery also happens at registration so a DLL that contains no generators is refused on
/// the spot. Accepting it and contributing nothing would surface far from its cause: the user
/// would see their code failing to compile against source that never got generated, with no
/// indication that the assembly they registered was the wrong one.
/// </para>
/// </remarks>
internal sealed class AnalyzerRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, RegisteredAnalyzer> _analyzers =
        new(StringComparer.Ordinal);

    /// <summary>
    /// One collectible context for every generator assembly in the session. Generators are
    /// compile-time tools with session lifetime, so there is nothing to gain from a context
    /// per assembly — and generators that reference each other (a shared helper library) need
    /// to see one another, which separate contexts would prevent.
    /// </summary>
    private readonly AssemblyLoadContext _loadContext =
        new(name: $"CarbideAnalyzers-{Guid.NewGuid():N}", isCollectible: true);

    private bool _disposed;

    /// <summary>
    /// Validates, loads, and reflects a generator assembly. Returns a new id.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the bytes are not a managed PE, when the assembly cannot be loaded, or
    /// when it contains no usable source generator.
    /// </exception>
    public string Add(byte[] bytes, string? name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var label = name ?? "<unnamed>";
        if (!WasmMetadataReferenceResolver.HasManagedMetadata(bytes))
        {
            throw new InvalidOperationException(
                $"Analyzer '{label}' ({bytes.Length} bytes) is not a valid managed PE image.");
        }

        Assembly assembly;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            assembly = _loadContext.LoadFromStream(stream);
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException)
        {
            throw new InvalidOperationException(
                $"Analyzer '{label}' could not be loaded: {ex.Message}", ex);
        }

        var (generators, diagnosticAnalyzers) = Discover(assembly, label);
        var id = Guid.NewGuid().ToString("N");
        _analyzers[id] = new RegisteredAnalyzer(id, name, generators, diagnosticAnalyzers);
        return id;
    }

    private static (ISourceGenerator[] Generators, DiagnosticAnalyzer[] Analyzers) Discover(
        Assembly assembly,
        string label)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Partial results are still useful — a generator assembly commonly carries helper
            // types that reference something absent here, and those failures do not matter as
            // long as the generator type itself loaded.
            types = ex.Types.Where(t => t is not null).ToArray()!;
        }

        var generators = new List<ISourceGenerator>();
        var diagnosticAnalyzers = new List<DiagnosticAnalyzer>();
        var uninstantiable = new List<string>();

        foreach (var type in types)
        {
            if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
            {
                continue;
            }

            var isIncremental = typeof(IIncrementalGenerator).IsAssignableFrom(type);
            var isSource = typeof(ISourceGenerator).IsAssignableFrom(type);
            var isDiagnosticAnalyzer = typeof(DiagnosticAnalyzer).IsAssignableFrom(type);
            if (!isIncremental && !isSource && !isDiagnosticAnalyzer)
            {
                continue;
            }

            try
            {
                var instance = Activator.CreateInstance(type);
                if (isIncremental)
                {
                    generators.Add(((IIncrementalGenerator)instance!).AsSourceGenerator());
                }
                else if (isSource)
                {
                    generators.Add((ISourceGenerator)instance!);
                }
                else
                {
                    diagnosticAnalyzers.Add((DiagnosticAnalyzer)instance!);
                }
            }
            catch (Exception ex) when (ex is MissingMethodException or TargetInvocationException or MemberAccessException)
            {
                // A type whose constructor throws, or that has no parameterless constructor,
                // is reported rather than skipped — silently dropping it would leave the
                // user's code failing to compile against source that was supposed to exist,
                // or passing a rule that was supposed to run.
                uninstantiable.Add($"{type.FullName} ({ex.GetType().Name}: {ex.Message})");
            }
        }

        if (generators.Count > 0 || diagnosticAnalyzers.Count > 0)
        {
            return ([.. generators], [.. diagnosticAnalyzers]);
        }

        var detail = uninstantiable.Count > 0
            ? $" {uninstantiable.Count} type(s) could not be instantiated: {string.Join("; ", uninstantiable)}."
            : string.Empty;

        throw new InvalidOperationException(
            $"Analyzer '{label}' contains no usable source generator or diagnostic analyzer " +
            "(no public parameterless IIncrementalGenerator, ISourceGenerator, or " +
            $"DiagnosticAnalyzer implementation).{detail}");
    }

    /// <summary>Removes the analyzer with the given id. Returns <c>true</c> if found.</summary>
    /// <remarks>
    /// The assembly stays loaded: an <see cref="AssemblyLoadContext"/> unloads as a unit, and
    /// other analyzers in the session share this one. The generator instances are dropped, so
    /// nothing further is generated from it.
    /// </remarks>
    public bool Remove(string id) => _analyzers.TryRemove(id, out _);

    public bool Contains(string id) => _analyzers.ContainsKey(id);

    /// <summary>The generators registered under <paramref name="id"/>, or empty if unknown.</summary>
    public IReadOnlyList<ISourceGenerator> GetGenerators(string id) =>
        _analyzers.TryGetValue(id, out var entry) ? entry.Generators : [];

    /// <summary>The diagnostic analyzers registered under <paramref name="id"/>, or empty.</summary>
    public IReadOnlyList<DiagnosticAnalyzer> GetDiagnosticAnalyzers(string id) =>
        _analyzers.TryGetValue(id, out var entry) ? entry.DiagnosticAnalyzers : [];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _analyzers.Clear();
        _loadContext.Unload();
    }

    private sealed record RegisteredAnalyzer(
        string Id,
        string? Name,
        ISourceGenerator[] Generators,
        DiagnosticAnalyzer[] DiagnosticAnalyzers);
}
