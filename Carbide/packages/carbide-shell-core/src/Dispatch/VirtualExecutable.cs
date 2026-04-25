using CarbideShellCore.Apps;
using CarbideShellCore.Env;
using CarbideShellCore.Vfs;

namespace CarbideShellCore.Dispatch;

/// <summary>
/// Personality of a virtual executable catalog entry. The same basename can map to
/// different handlers under different roots (for example GNU <c>find</c> vs Windows
/// <c>find.exe</c>), so the personality remains part of the stable definition.
/// </summary>
public enum VirtualExecutablePersonality
{
    Shell,
    Gnu,
    Windows,
    Language,
}

/// <summary>
/// Catalog entry for one virtual executable command surface.
/// </summary>
public sealed record VirtualExecutableDefinition(
    string CommandId,
    VirtualExecutablePersonality Personality,
    IReadOnlyList<string> StubPaths,
    IReadOnlyList<string> SearchNames,
    string HandlerKey);

/// <summary>
/// Concrete path match for a <see cref="VirtualExecutableDefinition"/>.
/// </summary>
public sealed record VirtualExecutableMatch(
    VirtualExecutableDefinition Definition,
    string ResolvedPath);

/// <summary>
/// Invocation payload passed to a concrete virtual executable handler.
/// </summary>
public sealed class VirtualExecutableInvocation
{
    public required VirtualExecutableDefinition Definition { get; init; }
    public required string ResolvedPath { get; init; }
    public required string InvokedAs { get; init; }
    public required IReadOnlyList<string> Args { get; init; }
    public required TextReader Input { get; init; }
    public required TextWriter Output { get; init; }
    public required TextWriter Error { get; init; }
    public required VirtualFileSystem Vfs { get; init; }
    public required EnvVarStore Env { get; init; }
    public required AppRegistry Apps { get; init; }
    public required ShellDispatcher Dispatcher { get; init; }
}

/// <summary>
/// Handler interface implemented by concrete multishell utility commands.
/// </summary>
public interface IVirtualExecutableHandler
{
    int Execute(VirtualExecutableInvocation invocation);
}

/// <summary>
/// Session-global catalog of virtual executable definitions together with the shell-aware
/// search rules needed to resolve a bare command name into an installed stub path.
/// </summary>
public sealed class VirtualExecutableRegistry
{
    private readonly Dictionary<string, VirtualExecutableDefinition> _byPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<VirtualExecutableDefinition> _definitions = new();

    public IReadOnlyList<VirtualExecutableDefinition> All => _definitions;

    public void Register(VirtualExecutableDefinition definition)
    {
        _definitions.Add(definition);
        foreach (var stubPath in definition.StubPaths)
            _byPath[stubPath] = definition;
    }

    public bool TryResolveByPath(string absolutePath, out VirtualExecutableDefinition? definition)
    {
        if (_byPath.TryGetValue(absolutePath, out var match))
        {
            definition = match;
            return true;
        }
        definition = null;
        return false;
    }

    public IReadOnlyList<VirtualExecutableMatch> ResolveMatches(
        string commandName,
        ShellExecutionContext ctx,
        string? callerShellName)
    {
        var matches = new List<VirtualExecutableMatch>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (LooksLikePath(commandName))
        {
            foreach (var candidate in BuildPathCandidates(commandName, ctx, callerShellName))
            {
                if (!seen.Add(candidate)) continue;
                if (ctx.Vfs.Resolve(candidate) is not VfsFile) continue;
                if (TryResolveByPath(candidate, out var definition) && definition is not null)
                    matches.Add(new VirtualExecutableMatch(definition, candidate));
            }
            return matches;
        }

        foreach (var root in GetSearchRoots(ctx.Env, callerShellName))
        {
            foreach (var leaf in GetLeafCandidates(commandName, ctx.Env, callerShellName))
            {
                var abs = ctx.Vfs.Normalize(VfsPath.Join(root, leaf));
                if (!seen.Add(abs)) continue;
                if (ctx.Vfs.Resolve(abs) is not VfsFile) continue;
                if (TryResolveByPath(abs, out var definition) && definition is not null)
                    matches.Add(new VirtualExecutableMatch(definition, abs));
            }
        }

        return matches;
    }

    public IReadOnlyList<string> BuildPathCandidates(
        string commandName,
        ShellExecutionContext ctx,
        string? callerShellName)
    {
        var normalized = ctx.Vfs.Normalize(commandName);
        var list = new List<string> { normalized };
        if (HasExtension(commandName)) return list;

        foreach (var ext in GetImplicitExtensions(ctx.Env, callerShellName))
        {
            var candidate = normalized + ext;
            if (!list.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                list.Add(candidate);
        }
        return list;
    }

    private static bool LooksLikePath(string commandName)
        => commandName.Contains('/')
        || commandName.Contains('\\')
        || commandName.StartsWith(".", StringComparison.Ordinal)
        || commandName.StartsWith("~", StringComparison.Ordinal)
        || (commandName.Length >= 3
            && char.IsLetter(commandName[0])
            && commandName[1] == ':'
            && (commandName[2] == '/' || commandName[2] == '\\'));

    private static bool HasExtension(string commandName)
    {
        var leaf = commandName;
        var slash = Math.Max(leaf.LastIndexOf('/'), leaf.LastIndexOf('\\'));
        if (slash >= 0 && slash < leaf.Length - 1) leaf = leaf.Substring(slash + 1);
        var dot = leaf.LastIndexOf('.');
        return dot > 0;
    }

    private static IReadOnlyList<string> GetSearchRoots(EnvVarStore env, string? callerShellName)
    {
        var path = env.Get("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            var parts = SplitPathList(path!).ToList();
            if (parts.Count > 0) return parts;
        }

        return callerShellName?.ToLowerInvariant() switch
        {
            "cmd" => new[] { "/Windows/System32", "/usr/bin", "/bin" },
            "pwsh" => new[] { "/Windows/System32", "/Windows/System32/WindowsPowerShell/v1.0", "/usr/bin", "/bin" },
            _ => new[] { "/usr/bin", "/bin", "/Windows/System32" },
        };
    }

    private static IEnumerable<string> SplitPathList(string path)
    {
        var separator = path.Contains(';') ? ';' : ':';
        foreach (var part in path.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Length == 0) continue;
            yield return part;
        }
    }

    private static IReadOnlyList<string> GetLeafCandidates(string commandName, EnvVarStore env, string? callerShellName)
    {
        var results = new List<string> { commandName };
        if (HasExtension(commandName)) return results;

        foreach (var ext in GetImplicitExtensions(env, callerShellName))
        {
            var candidate = commandName + ext;
            if (!results.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                results.Add(candidate);
        }
        return results;
    }

    private static IReadOnlyList<string> GetImplicitExtensions(EnvVarStore env, string? callerShellName)
    {
        if (string.Equals(callerShellName, "bash", StringComparison.OrdinalIgnoreCase))
            return new[] { ".exe", ".com" };

        var pathext = env.Get("PATHEXT");
        if (string.IsNullOrWhiteSpace(pathext))
            return new[] { ".com", ".exe", ".cmd", ".bat" };

        var list = new List<string>();
        foreach (var ext in pathext!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ext.Length == 0) continue;
            list.Add(ext.StartsWith('.') ? ext.ToLowerInvariant() : "." + ext.ToLowerInvariant());
        }
        return list.Count == 0 ? new[] { ".com", ".exe", ".cmd", ".bat" } : list;
    }
}
