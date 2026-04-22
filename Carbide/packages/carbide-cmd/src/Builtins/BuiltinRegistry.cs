using CarbideCmd.Runtime;

namespace CarbideCmd.Builtins;

/// <summary>
/// Signature every cmd built-in satisfies. Returns the exit code. Implementations receive
/// the interpreter (for env, VFS, positional args), a fully-expanded argv list, and the
/// currently-redirected I/O streams.
/// </summary>
public delegate int CmdBuiltin(
    Interpreter interpreter,
    IReadOnlyList<string> args,
    TextReader stdin,
    TextWriter stdout,
    TextWriter stderr);

/// <summary>
/// Case-insensitive name → <see cref="CmdBuiltin"/> table. The default instance is populated
/// from <see cref="Builtins"/>; tests can construct a fresh registry to exercise a minimal
/// surface.
/// </summary>
public static class BuiltinRegistry
{
    private static readonly Dictionary<string, CmdBuiltin> _map =
        new(StringComparer.OrdinalIgnoreCase);

    static BuiltinRegistry()
    {
        _map["ECHO"] = Builtins.Echo;
        _map["SET"] = Builtins.Set;
        _map["CD"] = Builtins.ChangeDir;
        _map["CHDIR"] = Builtins.ChangeDir;
        _map["DIR"] = Builtins.Dir;
        _map["MD"] = Builtins.MakeDir;
        _map["MKDIR"] = Builtins.MakeDir;
        _map["RD"] = Builtins.RemoveDir;
        _map["RMDIR"] = Builtins.RemoveDir;
        _map["DEL"] = Builtins.Delete;
        _map["ERASE"] = Builtins.Delete;
        _map["REN"] = Builtins.Rename;
        _map["RENAME"] = Builtins.Rename;
        _map["COPY"] = Builtins.Copy;
        _map["MOVE"] = Builtins.Move;
        _map["TYPE"] = Builtins.Type;
        _map["CLS"] = Builtins.Cls;
        _map["PAUSE"] = Builtins.Pause;
        _map["VER"] = Builtins.Ver;
        _map["TITLE"] = Builtins.Title;
        _map["COLOR"] = Builtins.Color;
        _map["FIND"] = Builtins.Find;
        _map["FINDSTR"] = Builtins.FindStr;
        _map["SORT"] = Builtins.Sort;
        _map["MORE"] = Builtins.More;
    }

    public static CmdBuiltin? TryGet(string name)
        => _map.TryGetValue(name, out var b) ? b : null;

    public static void Register(string name, CmdBuiltin impl) => _map[name] = impl;
}
