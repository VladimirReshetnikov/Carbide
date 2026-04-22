using CarbideBash.Runtime;

namespace CarbideBash.Builtins;

public delegate int BashBuiltin(
    Interpreter interp,
    IReadOnlyList<string> args,
    TextReader stdin,
    TextWriter stdout,
    TextWriter stderr);

public static class BuiltinRegistry
{
    private static readonly Dictionary<string, BashBuiltin> _map =
        new(StringComparer.Ordinal);

    static BuiltinRegistry()
    {
        _map["echo"] = Builtins.Echo;
        _map["printf"] = Builtins.Printf;
        _map["cd"] = Builtins.Cd;
        _map["pwd"] = Builtins.Pwd;
        _map["ls"] = Builtins.Ls;
        _map["cat"] = Builtins.Cat;
        _map["cp"] = Builtins.Cp;
        _map["mv"] = Builtins.Mv;
        _map["rm"] = Builtins.Rm;
        _map["mkdir"] = Builtins.Mkdir;
        _map["rmdir"] = Builtins.Rmdir;
        _map["touch"] = Builtins.Touch;
        _map["head"] = Builtins.Head;
        _map["tail"] = Builtins.Tail;
        _map["wc"] = Builtins.Wc;
        _map["grep"] = Builtins.Grep;
        _map["sort"] = Builtins.Sort;
        _map["uniq"] = Builtins.Uniq;
        _map["tr"] = Builtins.Tr;
        _map["export"] = Builtins.Export;
        _map["unset"] = Builtins.Unset;
        _map["env"] = Builtins.Env;
        _map["read"] = Builtins.Read;
        _map["test"] = Builtins.Test;
        _map["["] = Builtins.TestSquare;
        _map["[["] = Builtins.TestSquare;
        _map["true"] = Builtins.True;
        _map["false"] = Builtins.False;
        _map["exit"] = Builtins.Exit;
        _map["return"] = Builtins.Return;
        _map["break"] = Builtins.Break;
        _map["continue"] = Builtins.Continue;
        _map["shift"] = Builtins.Shift;
        _map["source"] = Builtins.Source;
        _map["."] = Builtins.Source;
        _map["eval"] = Builtins.Eval;
        _map["type"] = Builtins.Type;
        _map["alias"] = Builtins.Alias;
        _map["declare"] = Builtins.Declare;
        _map["local"] = Builtins.Declare;
        _map["set"] = Builtins.SetBuiltin;
    }

    public static BashBuiltin? TryGet(string name)
        => _map.TryGetValue(name, out var b) ? b : null;

    public static void Register(string name, BashBuiltin impl) => _map[name] = impl;
}
