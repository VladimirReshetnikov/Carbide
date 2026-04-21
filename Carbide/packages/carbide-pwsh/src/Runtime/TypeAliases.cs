namespace CarbidePwsh.Runtime;

/// <summary>
/// PowerShell-style type accelerators — the short names you can use in <c>[Type]</c>. Matches
/// the commonly-used set from upstream PowerShell; extra entries can be added without
/// affecting correctness of the interpreter.
/// </summary>
public static class TypeAliases
{
    public static readonly IReadOnlyDictionary<string, Type> Aliases = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
    {
        ["int"] = typeof(int),
        ["int32"] = typeof(int),
        ["int64"] = typeof(long),
        ["long"] = typeof(long),
        ["short"] = typeof(short),
        ["int16"] = typeof(short),
        ["byte"] = typeof(byte),
        ["sbyte"] = typeof(sbyte),
        ["uint"] = typeof(uint),
        ["uint32"] = typeof(uint),
        ["uint64"] = typeof(ulong),
        ["ulong"] = typeof(ulong),
        ["ushort"] = typeof(ushort),
        ["uint16"] = typeof(ushort),
        ["double"] = typeof(double),
        ["float"] = typeof(float),
        ["single"] = typeof(float),
        ["decimal"] = typeof(decimal),
        ["bool"] = typeof(bool),
        ["boolean"] = typeof(bool),
        ["char"] = typeof(char),
        ["string"] = typeof(string),
        ["object"] = typeof(object),
        ["type"] = typeof(Type),

        ["datetime"] = typeof(DateTime),
        ["timespan"] = typeof(TimeSpan),
        ["guid"] = typeof(Guid),
        ["uri"] = typeof(Uri),
        ["regex"] = typeof(System.Text.RegularExpressions.Regex),
        ["array"] = typeof(Array),
        ["hashtable"] = typeof(System.Collections.Hashtable),
        ["ordered"] = typeof(System.Collections.Specialized.OrderedDictionary),
        ["math"] = typeof(Math),
        ["console"] = typeof(Console),
        ["convert"] = typeof(Convert),
        ["enum"] = typeof(Enum),
        ["void"] = typeof(void),
        ["scriptblock"] = typeof(ScriptBlock),
        ["pscustomobject"] = typeof(System.Collections.Specialized.OrderedDictionary),
        ["errorrecord"] = typeof(ErrorRecord),
        ["pwshexception"] = typeof(Errors.PwshException),
    };
}
