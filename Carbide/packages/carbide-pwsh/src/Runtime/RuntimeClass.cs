using CarbidePwsh.Errors;
using CarbidePwsh.Parser.Ast;

namespace CarbidePwsh.Runtime;

public sealed class RuntimeClass
{
    public string Name { get; }
    public IReadOnlyList<ClassPropertyAst> Properties { get; }
    public ClassMethodAst? Constructor { get; }
    public IReadOnlyDictionary<string, ClassMethodAst> InstanceMethods { get; }
    public IReadOnlyDictionary<string, ClassMethodAst> StaticMethods { get; }

    public RuntimeClass(ClassDefinitionAst ast)
    {
        Name = ast.Name;
        Properties = ast.Properties;
        Constructor = ast.Methods.FirstOrDefault(m => m.IsConstructor);
        var instanceMethods = new Dictionary<string, ClassMethodAst>(StringComparer.OrdinalIgnoreCase);
        var staticMethods = new Dictionary<string, ClassMethodAst>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in ast.Methods.Where(m => !m.IsConstructor))
        {
            if (m.IsStatic)
                staticMethods[m.Name] = m;
            else
                instanceMethods[m.Name] = m;
        }
        InstanceMethods = instanceMethods;
        StaticMethods = staticMethods;
    }
}

public sealed class RuntimeInstance
{
    public RuntimeClass Class { get; }
    public Dictionary<string, object?> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);

    public RuntimeInstance(RuntimeClass cls) { Class = cls; }

    public override string ToString() => $"[{Class.Name}]";
}

public sealed class ClassRegistry
{
    private readonly Dictionary<string, RuntimeClass> _classes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RuntimeEnum> _enums = new(StringComparer.OrdinalIgnoreCase);

    public void Register(RuntimeClass cls) => _classes[cls.Name] = cls;
    public void Register(RuntimeEnum e) => _enums[e.Name] = e;

    public bool TryGetClass(string name, out RuntimeClass? cls)
    {
        if (_classes.TryGetValue(name, out var c)) { cls = c; return true; }
        cls = null; return false;
    }

    public bool TryGetEnum(string name, out RuntimeEnum? e)
    {
        if (_enums.TryGetValue(name, out var v)) { e = v; return true; }
        e = null; return false;
    }

    public IReadOnlyCollection<string> ClassNames => _classes.Keys;
    public IReadOnlyCollection<string> EnumNames => _enums.Keys;
}
