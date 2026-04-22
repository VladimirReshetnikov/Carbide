using System.Collections;
using System.Globalization;
using CarbidePwsh.Errors;

namespace CarbidePwsh.Runtime;

/// <summary>
/// PowerShell-flavored type coercion for the Phase 1 surface. Intentionally small: the table
/// covers numeric widening, string round-trips, bool truthiness, null handling, and enum parse
/// from string. Anything it can't convert throws <see cref="PwshCoercionException"/>.
/// </summary>
public static class Coercion
{
    public static T To<T>(object? value) => (T)To(value, typeof(T))!;

    public static object? To(object? value, Type target)
    {
        if (target == typeof(object)) return value;

        // Nullable<T> -> strip to underlying.
        var under = Nullable.GetUnderlyingType(target);
        if (under != null)
        {
            if (value == null) return null;
            return To(value, under);
        }

        if (value == null)
        {
            if (target == typeof(string)) return "";          // PowerShell: [string]$null => ""
            if (!target.IsValueType) return null;
            if (target == typeof(bool)) return false;
            if (IsNumeric(target)) return Convert.ChangeType(0, target, CultureInfo.InvariantCulture);
            if (target.IsEnum) return Enum.ToObject(target, 0);
            return Activator.CreateInstance(target);
        }

        var sourceType = value.GetType();
        if (target.IsAssignableFrom(sourceType)) return value;

        // Common primitive fast paths.
        if (target == typeof(string)) return FormatAsString(value);
        if (target == typeof(bool)) return CoerceToBool(value);

        if (target.IsEnum)
        {
            if (value is string s)
            {
                return Enum.Parse(target, s, ignoreCase: true);
            }
            return Enum.ToObject(target, ToInt64(value));
        }

        if (IsNumeric(target))
        {
            var dbl = ToDouble(value);
            try
            {
                return Convert.ChangeType(dbl, target, CultureInfo.InvariantCulture);
            }
            catch (OverflowException ex)
            {
                throw new PwshCoercionException(
                    $"Value '{value}' is out of range for {target.FullName}.", SourceLocation.None)
                { };
            }
        }

        if (target == typeof(char) && value is string cs && cs.Length == 1) return cs[0];

        // Arrays — allow object[] from IEnumerable.
        if (target.IsArray && value is IEnumerable en)
        {
            var elementType = target.GetElementType()!;
            var list = new List<object?>();
            foreach (var item in en) list.Add(To(item, elementType));
            var arr = Array.CreateInstance(elementType, list.Count);
            for (int i = 0; i < list.Count; i++) arr.SetValue(list[i], i);
            return arr;
        }

        // Last resort: try Convert.ChangeType; let it throw on failure.
        try
        {
            return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            throw new PwshCoercionException(
                $"Cannot convert value of type [{sourceType.FullName}] to [{target.FullName}]: {ex.Message}",
                SourceLocation.None);
        }
    }

    public static bool CoerceToBool(object? value)
    {
        if (value == null) return false;
        if (value is bool b) return b;
        if (value is string s) return IsTruthyString(s);
        if (value is Array a) return a.Length > 0;
        if (value is IEnumerable e && value is not string)
        {
            foreach (var _ in e) return true;
            return false;
        }
        if (IsNumeric(value.GetType()))
        {
            return ToDouble(value) != 0.0;
        }
        return true;
    }

    private static bool IsTruthyString(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (s.Equals("0", StringComparison.Ordinal)) return false;
        if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public static string FormatAsString(object? value)
    {
        if (value == null) return "";
        if (value is bool b) return b ? "True" : "False";
        // Match real pwsh's numeric rendering: double/float use 15 significant digits
        // (G15) instead of .NET's default G17 round-trip format. `10 / 3` prints as
        // `3.33333333333333`, not `3.3333333333333335`.
        if (value is double d) return d.ToString("G15", CultureInfo.InvariantCulture);
        if (value is float f32) return f32.ToString("G7", CultureInfo.InvariantCulture);
        if (value is IFormattable f) return f.ToString(null, CultureInfo.InvariantCulture);
        return value.ToString() ?? "";
    }

    public static double ToDouble(object? v) => v switch
    {
        null => 0.0,
        bool b => b ? 1.0 : 0.0,
        double d => d,
        float f => f,
        decimal m => (double)m,
        int i => i,
        long l => l,
        short sh => sh,
        sbyte sb => sb,
        byte by => by,
        ushort us => us,
        uint ui => ui,
        ulong ul => ul,
        char c => c,
        string s => ParseDouble(s),
        _ => Convert.ToDouble(v, CultureInfo.InvariantCulture),
    };

    public static long ToInt64(object? v) => v switch
    {
        null => 0L,
        bool b => b ? 1L : 0L,
        double d => (long)d,
        float f => (long)f,
        decimal m => (long)m,
        int i => i,
        long l => l,
        short sh => sh,
        sbyte sb => sb,
        byte by => by,
        ushort us => us,
        uint ui => ui,
        ulong ul => (long)ul,
        char c => c,
        string s => ParseLong(s),
        _ => Convert.ToInt64(v, CultureInfo.InvariantCulture),
    };

    private static double ParseDouble(string s)
    {
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        throw new PwshCoercionException($"Cannot convert string '{s}' to a number.");
    }

    private static long ParseLong(string s)
    {
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return (long)d;
        throw new PwshCoercionException($"Cannot convert string '{s}' to a number.");
    }

    public static bool IsNumeric(Type t)
    {
        if (t.IsEnum) return false;
        switch (Type.GetTypeCode(t))
        {
            case TypeCode.SByte:
            case TypeCode.Byte:
            case TypeCode.Int16:
            case TypeCode.UInt16:
            case TypeCode.Int32:
            case TypeCode.UInt32:
            case TypeCode.Int64:
            case TypeCode.UInt64:
            case TypeCode.Single:
            case TypeCode.Double:
            case TypeCode.Decimal:
                return true;
            default:
                return false;
        }
    }

    public static bool IsIntegral(Type t)
    {
        switch (Type.GetTypeCode(t))
        {
            case TypeCode.SByte:
            case TypeCode.Byte:
            case TypeCode.Int16:
            case TypeCode.UInt16:
            case TypeCode.Int32:
            case TypeCode.UInt32:
            case TypeCode.Int64:
            case TypeCode.UInt64:
                return true;
            default:
                return false;
        }
    }
}
