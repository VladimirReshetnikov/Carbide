namespace CarbidePwsh.Runtime;

public sealed class RuntimeEnum
{
    public string Name { get; }
    public IReadOnlyDictionary<string, long> Members { get; }

    public RuntimeEnum(string name, IReadOnlyDictionary<string, long> members)
    {
        Name = name;
        Members = members;
    }

    public EnumValue? FromName(string memberName)
    {
        if (!Members.TryGetValue(memberName, out var v)) return null;
        return new EnumValue(this, memberName, v);
    }

    public EnumValue? FromValue(long value)
    {
        foreach (var kv in Members)
        {
            if (kv.Value == value) return new EnumValue(this, kv.Key, kv.Value);
        }
        // Allow out-of-range value (matches .NET behavior for flags enums).
        return new EnumValue(this, value.ToString(System.Globalization.CultureInfo.InvariantCulture), value);
    }
}

public sealed class EnumValue : IComparable, IComparable<EnumValue>, IEquatable<EnumValue>
{
    public RuntimeEnum EnumType { get; }
    public string MemberName { get; }
    public long Value { get; }

    public EnumValue(RuntimeEnum enumType, string memberName, long value)
    {
        EnumType = enumType;
        MemberName = memberName;
        Value = value;
    }

    public override string ToString() => MemberName;

    public bool Equals(EnumValue? other)
    {
        if (other is null) return false;
        return EnumType.Name == other.EnumType.Name && Value == other.Value;
    }

    public override bool Equals(object? obj) => obj is EnumValue v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(EnumType.Name, Value);

    public int CompareTo(EnumValue? other)
    {
        if (other is null) return 1;
        return Value.CompareTo(other.Value);
    }

    public int CompareTo(object? obj) => obj switch
    {
        null => 1,
        EnumValue v => CompareTo(v),
        _ => Value.CompareTo(Convert.ToInt64(obj, System.Globalization.CultureInfo.InvariantCulture)),
    };
}
