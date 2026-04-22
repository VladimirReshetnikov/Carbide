using System.Collections.Specialized;
using CarbideShellCore.Env;
using CarbideShellCore.Vfs;

namespace CarbidePwsh.Runtime;

/// <summary>
/// Which drive the session's "current location" lives under. Real pwsh supports
/// <c>FileSystem</c>, <c>Env</c>, <c>Alias</c>, <c>Function</c>, <c>Variable</c>, plus
/// Registry (out of scope here) and a few Windows-only ones. We ship the five portable
/// drives so <c>cd Env:</c>, <c>ls Alias:</c>, <c>$Function:Name</c>, and friends all
/// behave the same way a pwsh user expects.
/// </summary>
public enum PwshDriveKind
{
    FileSystem,
    Env,
    Alias,
    Function,
    Variable,
}

/// <summary>
/// Parse drive-qualified paths such as <c>Env:FOO</c>, <c>Alias:cd</c>,
/// <c>Function:Add</c>, <c>Variable:x</c>. Falls back to
/// <see cref="PwshDriveKind.FileSystem"/> when no qualifier is present.
/// </summary>
public static class PathQualifier
{
    public static (PwshDriveKind Drive, string SubPath) Parse(string path, PwshDriveKind fallback)
    {
        if (string.IsNullOrEmpty(path)) return (fallback, "");
        var colon = path.IndexOf(':');
        if (colon > 0)
        {
            var prefix = path.Substring(0, colon);
            var rest = path.Substring(colon + 1).TrimStart('\\', '/');
            if (TryParseDrive(prefix, out var drive)) return (drive, rest);
        }
        return (fallback, path);
    }

    public static bool TryParseDrive(string prefix, out PwshDriveKind drive)
    {
        switch (prefix.ToLowerInvariant())
        {
            case "env": drive = PwshDriveKind.Env; return true;
            case "alias": drive = PwshDriveKind.Alias; return true;
            case "function": drive = PwshDriveKind.Function; return true;
            case "variable": drive = PwshDriveKind.Variable; return true;
            default:
                drive = PwshDriveKind.FileSystem;
                return false;
        }
    }

    public static string DriveName(PwshDriveKind drive) => drive switch
    {
        PwshDriveKind.Env => "Env",
        PwshDriveKind.Alias => "Alias",
        PwshDriveKind.Function => "Function",
        PwshDriveKind.Variable => "Variable",
        PwshDriveKind.FileSystem => "FileSystem",
        _ => "FileSystem",
    };

    /// <summary>
    /// Return the string that should appear in the prompt for the given drive+subpath.
    /// FileSystem shows the VFS path; every other drive shows <c>Drive:\</c> regardless of
    /// the subpath because the non-FS providers are flat.
    /// </summary>
    public static string PromptDisplay(PwshDriveKind drive, string vfsLocation) => drive switch
    {
        PwshDriveKind.FileSystem => vfsLocation,
        _ => DriveName(drive) + ":\\",
    };
}

/// <summary>
/// A single entry surfaced by a pwsh provider. Non-FileSystem providers emit these via
/// <see cref="Providers.GetChildren"/> / <see cref="Providers.GetItem"/>.
/// For the <see cref="PwshDriveKind.Alias"/> and <see cref="PwshDriveKind.Function"/>
/// drives, the formatter renders CommandType/Name/Version/Source columns; for the others
/// it renders Name/Value.
/// </summary>
public sealed record PwshProviderItem(
    PwshDriveKind Drive,
    string Name,
    object? Value,
    string? Target = null);

/// <summary>
/// Reads from the live interpreter / env / cmdlet-registry / function-registry state to
/// enumerate drives. Each operation takes a drive + optional name and translates it into
/// a read/write against whichever session store that drive covers.
/// </summary>
public sealed class Providers
{
    private readonly Interpreter _interp;

    public Providers(Interpreter interp) { _interp = interp; }

    public IEnumerable<PwshProviderItem> GetChildren(PwshDriveKind drive)
    {
        switch (drive)
        {
            case PwshDriveKind.Env:
            {
                var env = RequireEnv();
                foreach (var kv in env.All.OrderBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                    yield return new PwshProviderItem(drive, kv.Key, kv.Value);
                break;
            }
            case PwshDriveKind.Variable:
            {
                foreach (var kv in _interp.Scope.SnapshotCurrent().OrderBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                {
                    yield return new PwshProviderItem(drive, kv.Key, kv.Value);
                }
                break;
            }
            case PwshDriveKind.Alias:
            {
                var reg = _interp.Registry;
                if (reg is null) yield break;
                foreach (var (alias, target) in reg.Aliases.OrderBy(static t => t.Item1, StringComparer.OrdinalIgnoreCase))
                {
                    yield return new PwshProviderItem(drive, alias, null, target);
                }
                break;
            }
            case PwshDriveKind.Function:
            {
                var funcs = _interp.Functions;
                if (funcs is null) yield break;
                foreach (var name in funcs.Names.OrderBy(static s => s, StringComparer.OrdinalIgnoreCase))
                {
                    yield return new PwshProviderItem(drive, name, null);
                }
                break;
            }
            case PwshDriveKind.FileSystem:
                // The FileSystem provider is served by Get-ChildItem against the VFS; the
                // dispatcher only routes through Providers for the non-FS drives.
                yield break;
        }
    }

    public PwshProviderItem? GetItem(PwshDriveKind drive, string name)
    {
        switch (drive)
        {
            case PwshDriveKind.Env:
            {
                var env = RequireEnv();
                var v = env.Get(name);
                return v is null ? null : new PwshProviderItem(drive, name, v);
            }
            case PwshDriveKind.Variable:
            {
                var v = _interp.Scope.Get(null, name);
                if (v is null && !_interp.Scope.Contains(null, name)) return null;
                return new PwshProviderItem(drive, name, v);
            }
            case PwshDriveKind.Alias:
            {
                var reg = _interp.Registry;
                if (reg is null) return null;
                if (!reg.TryResolveAliasTarget(name, out var target)) return null;
                return new PwshProviderItem(drive, name, null, target);
            }
            case PwshDriveKind.Function:
            {
                var funcs = _interp.Functions;
                if (funcs is null) return null;
                return funcs.Contains(name) ? new PwshProviderItem(drive, name, null) : null;
            }
            default: return null;
        }
    }

    public bool Exists(PwshDriveKind drive, string name) => GetItem(drive, name) is not null;

    public void SetItem(PwshDriveKind drive, string name, object? value)
    {
        switch (drive)
        {
            case PwshDriveKind.Env:
                RequireEnv().Set(name, value is null ? null : Coercion.FormatAsString(value));
                return;
            case PwshDriveKind.Variable:
                _interp.Scope.Set(null, name, value);
                return;
            case PwshDriveKind.Alias:
            case PwshDriveKind.Function:
                throw new Errors.PwshRuntimeException(
                    $"Set-Item on the {PathQualifier.DriveName(drive)} drive is not supported in Phase 1.");
        }
    }

    public void Remove(PwshDriveKind drive, string name)
    {
        switch (drive)
        {
            case PwshDriveKind.Env:
                RequireEnv().Unset(name);
                return;
            case PwshDriveKind.Variable:
                _interp.Scope.Remove(null, name);
                return;
            case PwshDriveKind.Alias:
            {
                var reg = _interp.Registry
                    ?? throw new Errors.PwshRuntimeException("No cmdlet registry configured.");
                reg.RemoveAlias(name);
                return;
            }
            case PwshDriveKind.Function:
            {
                var funcs = _interp.Functions
                    ?? throw new Errors.PwshRuntimeException("No function registry configured.");
                funcs.Remove(name);
                return;
            }
        }
    }

    private EnvVarStore RequireEnv()
        => _interp.Env ?? throw new Errors.PwshRuntimeException("No EnvVarStore is configured for this session.");
}
