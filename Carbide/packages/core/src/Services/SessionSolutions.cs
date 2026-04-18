// Adapted from WasmSharp.Core.Services.WasmSolution. Expanded with an explicit Session layer
// to make room for multi-project sessions later (architecture §3.1, §14). M1 keeps it
// single-project-per-session in practice.
// Upstream: https://github.com/JakeYallop/WasmSharp (Apache-2.0).

using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Carbide.Core.Services;

internal sealed class SessionSolutions(ILogger<SessionSolutions> logger)
{
    private readonly ILogger<SessionSolutions> _logger = logger;
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProjectEntry> _projects = new(StringComparer.Ordinal);
    private bool _referencesLoaded;

    public async Task InitializeReferencesAsync(string[] assemblyUrls)
    {
        if (_referencesLoaded)
        {
            return;
        }

        var resolver = new WasmMetadataReferenceResolver();
        var tasks = new List<Task<(byte[] bytes, MetadataReference? reference)>>(assemblyUrls.Length);
        foreach (var url in assemblyUrls)
        {
            tasks.Add(resolver.ResolveWithBytesAsync(url));
        }

        var resolved = await Task.WhenAll(tasks).ConfigureAwait(false);
        int added = 0;
        int skipped = 0;
        for (int i = 0; i < resolved.Length; i++)
        {
            var (bytes, reference) = resolved[i];
            if (reference is null)
            {
                skipped++;
                _logger.LogInformation(
                    "Skipped '{Url}' — no managed metadata (length={Length} bytes).",
                    assemblyUrls[i], bytes.Length);
                continue;
            }
            MetadataReferenceCache.AddReference(reference);
            added++;
        }

        _referencesLoaded = true;
        _logger.LogInformation("Loaded {Count} metadata references (skipped {Skipped} without managed metadata).", added, skipped);
    }

    public string CreateSession()
    {
        var id = Guid.NewGuid().ToString("N");
        _sessions[id] = new SessionState();
        return id;
    }

    public void DisposeSession(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var state))
        {
            return;
        }

        foreach (var projectId in state.ProjectIds)
        {
            _projects.TryRemove(projectId, out _);
        }
    }

    public string CreateProject(string sessionId, DocumentOptions? options = null, string? assemblyName = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            throw new InvalidOperationException($"Unknown session id '{sessionId}'.");
        }

        var projectId = Guid.NewGuid().ToString("N");
        var compiler = new ProjectCompiler(assemblyName, options);
        _projects[projectId] = new ProjectEntry(sessionId, compiler);
        state.ProjectIds.Add(projectId);
        return projectId;
    }

    public void AddSource(string projectId, string path, string code)
        => GetProject(projectId).AddSource(path, code);

    public Task<Diagnostic[]> GetDiagnosticsAsync(string projectId)
        => GetProject(projectId).GetDiagnosticsAsync();

    public Task<RunResult> RunAsync(string projectId)
        => GetProject(projectId).RunAsync();

    private ProjectCompiler GetProject(string projectId)
    {
        if (!_projects.TryGetValue(projectId, out var entry))
        {
            throw new InvalidOperationException($"Unknown project id '{projectId}'.");
        }
        return entry.Compiler;
    }

    private sealed class SessionState
    {
        public HashSet<string> ProjectIds { get; } = new(StringComparer.Ordinal);
    }

    private sealed record ProjectEntry(string SessionId, ProjectCompiler Compiler);
}
