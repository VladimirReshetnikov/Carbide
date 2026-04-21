using System.Collections;
using CarbidePwsh.Errors;
using CarbidePwsh.Parser.Ast;

namespace CarbidePwsh.Runtime;

/// <summary>
/// PowerShell-flavored binary and unary operator implementations. Comparison operators follow
/// PowerShell semantics: element-wise filtering when the LHS is a collection; case-insensitive
/// by default with <c>-c*</c> variants for ordinal/case-sensitive comparison.
/// </summary>
public static class Operators
{
    // ---------------- Binary ----------------

    public static object? Binary(BinaryOp op, object? left, object? right)
    {
        // Collection-LHS filtering for comparison operators matches PowerShell.
        if (IsComparison(op) && IsCollection(left))
        {
            return FilterCollection(op, left!, right);
        }

        return op switch
        {
            BinaryOp.Add => Add(left, right),
            BinaryOp.Subtract => ArithDouble(left, right, (a, b) => a - b, (a, b) => a - b),
            BinaryOp.Multiply => Multiply(left, right),
            BinaryOp.Divide => Divide(left, right),
            BinaryOp.Modulo => ArithDouble(left, right, (a, b) => a % b, (a, b) => a % b),

            BinaryOp.Equal => EqualCase(left, right, ignoreCase: true),
            BinaryOp.NotEqual => !EqualCase(left, right, ignoreCase: true),
            BinaryOp.LessThan => Compare(left, right, ignoreCase: true) < 0,
            BinaryOp.LessOrEqual => Compare(left, right, ignoreCase: true) <= 0,
            BinaryOp.GreaterThan => Compare(left, right, ignoreCase: true) > 0,
            BinaryOp.GreaterOrEqual => Compare(left, right, ignoreCase: true) >= 0,

            BinaryOp.IEqual => EqualCase(left, right, ignoreCase: true),
            BinaryOp.INotEqual => !EqualCase(left, right, ignoreCase: true),
            BinaryOp.ILessThan => Compare(left, right, ignoreCase: true) < 0,
            BinaryOp.ILessOrEqual => Compare(left, right, ignoreCase: true) <= 0,
            BinaryOp.IGreaterThan => Compare(left, right, ignoreCase: true) > 0,
            BinaryOp.IGreaterOrEqual => Compare(left, right, ignoreCase: true) >= 0,

            BinaryOp.CEqual => EqualCase(left, right, ignoreCase: false),
            BinaryOp.CNotEqual => !EqualCase(left, right, ignoreCase: false),
            BinaryOp.CLessThan => Compare(left, right, ignoreCase: false) < 0,
            BinaryOp.CLessOrEqual => Compare(left, right, ignoreCase: false) <= 0,
            BinaryOp.CGreaterThan => Compare(left, right, ignoreCase: false) > 0,
            BinaryOp.CGreaterOrEqual => Compare(left, right, ignoreCase: false) >= 0,

            BinaryOp.And => Coercion.CoerceToBool(left) && Coercion.CoerceToBool(right),
            BinaryOp.Or => Coercion.CoerceToBool(left) || Coercion.CoerceToBool(right),
            BinaryOp.Xor => Coercion.CoerceToBool(left) ^ Coercion.CoerceToBool(right),

            BinaryOp.BAnd => Coercion.ToInt64(left) & Coercion.ToInt64(right),
            BinaryOp.BOr => Coercion.ToInt64(left) | Coercion.ToInt64(right),
            BinaryOp.BXor => Coercion.ToInt64(left) ^ Coercion.ToInt64(right),

            BinaryOp.Is => IsOp(left, right),
            BinaryOp.IsNot => !IsOp(left, right),
            BinaryOp.As => AsOp(left, right),

            _ => throw new PwshRuntimeException($"Unsupported binary operator {op}.")
        };
    }

    private static bool IsComparison(BinaryOp op) => op switch
    {
        BinaryOp.Equal or BinaryOp.NotEqual or
        BinaryOp.LessThan or BinaryOp.LessOrEqual or
        BinaryOp.GreaterThan or BinaryOp.GreaterOrEqual or
        BinaryOp.IEqual or BinaryOp.INotEqual or
        BinaryOp.ILessThan or BinaryOp.ILessOrEqual or
        BinaryOp.IGreaterThan or BinaryOp.IGreaterOrEqual or
        BinaryOp.CEqual or BinaryOp.CNotEqual or
        BinaryOp.CLessThan or BinaryOp.CLessOrEqual or
        BinaryOp.CGreaterThan or BinaryOp.CGreaterOrEqual => true,
        _ => false,
    };

    private static bool IsCollection(object? v) => v is Array or IList && v is not string;

    private static object FilterCollection(BinaryOp op, object left, object? right)
    {
        var result = new List<object?>();
        foreach (var item in (IEnumerable)left)
        {
            var keep = (bool)Binary(op, item, right)!;
            if (keep) result.Add(item);
        }
        return result.ToArray();
    }

    // ---- Arithmetic ----

    private static object Add(object? l, object? r)
    {
        if (l is string sl) return sl + Coercion.FormatAsString(r);
        if (l is Array arr)
        {
            var list = new List<object?>();
            foreach (var e in arr) list.Add(e);
            if (r is IEnumerable re && r is not string) foreach (var e in re) list.Add(e);
            else list.Add(r);
            return list.ToArray();
        }
        return ArithDouble(l, r, (a, b) => a + b, (a, b) => a + b);
    }

    private static object Multiply(object? l, object? r)
    {
        if (l is string s && IsIntegral(r))
        {
            var n = (int)Coercion.ToInt64(r);
            return string.Concat(Enumerable.Repeat(s, Math.Max(0, n)));
        }
        if (l is Array arr && IsIntegral(r))
        {
            var n = (int)Coercion.ToInt64(r);
            var list = new List<object?>();
            for (int i = 0; i < n; i++)
                foreach (var e in arr) list.Add(e);
            return list.ToArray();
        }
        return ArithDouble(l, r, (a, b) => a * b, (a, b) => a * b);
    }

    private static object Divide(object? l, object? r)
    {
        if (IsIntegralValue(l) && IsIntegralValue(r))
        {
            var a = Coercion.ToInt64(l);
            var b = Coercion.ToInt64(r);
            if (b == 0) throw new PwshRuntimeException("Attempted to divide by zero.");
            if (a % b == 0) return FitInteger(a / b);
            return (double)a / b;
        }
        var da = Coercion.ToDouble(l);
        var db = Coercion.ToDouble(r);
        if (db == 0.0) throw new PwshRuntimeException("Attempted to divide by zero.");
        return da / db;
    }

    private static bool IsIntegral(object? v) => v != null && Coercion.IsIntegral(v.GetType());
    private static bool IsIntegralValue(object? v) => IsIntegral(v);

    private static object ArithDouble(object? l, object? r, Func<long, long, long> intOp, Func<double, double, double> dblOp)
    {
        if (IsIntegralValue(l) && IsIntegralValue(r))
        {
            try
            {
                checked
                {
                    return FitInteger(intOp(Coercion.ToInt64(l), Coercion.ToInt64(r)));
                }
            }
            catch (OverflowException)
            {
                return dblOp(Coercion.ToDouble(l), Coercion.ToDouble(r));
            }
        }
        return dblOp(Coercion.ToDouble(l), Coercion.ToDouble(r));
    }

    private static object FitInteger(long v)
        => v is >= int.MinValue and <= int.MaxValue ? (object)(int)v : v;

    // ---- Equality / comparison ----

    private static bool EqualCase(object? l, object? r, bool ignoreCase)
    {
        if (l == null && r == null) return true;
        if (l == null || r == null) return false;
        if (l is string ls && r is string rs)
        {
            return string.Equals(ls, rs, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        if (l is string || r is string)
        {
            // PowerShell coerces right to type of left if left is string.
            var rs2 = Coercion.FormatAsString(r);
            var ls2 = Coercion.FormatAsString(l);
            return string.Equals(ls2, rs2, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        if (Coercion.IsNumeric(l.GetType()) && Coercion.IsNumeric(r.GetType()))
        {
            return Coercion.ToDouble(l) == Coercion.ToDouble(r);
        }
        if (l is bool lb || r is bool rb)
        {
            return Coercion.CoerceToBool(l) == Coercion.CoerceToBool(r);
        }
        return Equals(l, r);
    }

    private static int Compare(object? l, object? r, bool ignoreCase)
    {
        if (l == null && r == null) return 0;
        if (l == null) return -1;
        if (r == null) return 1;
        if (l is string ls && r is string rs)
            return string.Compare(ls, rs, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (l is string || r is string)
        {
            // If left is string, coerce right to string per PowerShell. Otherwise to number.
            if (l is string)
                return string.Compare((string)l, Coercion.FormatAsString(r),
                    ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            return Coercion.ToDouble(l).CompareTo(Coercion.ToDouble(r));
        }
        if (Coercion.IsNumeric(l.GetType()) && Coercion.IsNumeric(r.GetType()))
        {
            return Coercion.ToDouble(l).CompareTo(Coercion.ToDouble(r));
        }
        if (l is IComparable c) return c.CompareTo(Coercion.To(r, l.GetType()));
        throw new PwshRuntimeException($"Values of type [{l.GetType()}] and [{r.GetType()}] are not comparable.");
    }

    private static bool IsOp(object? value, object? typeValue)
    {
        if (typeValue is not Type t)
            throw new PwshRuntimeException("The right operand of -is must be a type.");
        return value != null && t.IsInstanceOfType(value);
    }

    private static object? AsOp(object? value, object? typeValue)
    {
        if (typeValue is not Type t)
            throw new PwshRuntimeException("The right operand of -as must be a type.");
        try { return Coercion.To(value, t); }
        catch { return null; }
    }

    // ---------------- Unary ----------------

    public static object? Unary(UnaryOp op, object? operand) => op switch
    {
        UnaryOp.Plus => operand switch
        {
            null => 0,
            _ when Coercion.IsNumeric(operand.GetType()) => operand,
            _ => Coercion.ToDouble(operand),
        },
        UnaryOp.Negate => operand switch
        {
            null => 0,
            int i => -i,
            long l => -l,
            double d => -d,
            float f => -f,
            decimal m => -m,
            _ => -Coercion.ToDouble(operand),
        },
        UnaryOp.Not => !Coercion.CoerceToBool(operand),
        UnaryOp.BNot => ~Coercion.ToInt64(operand),
        _ => throw new PwshRuntimeException($"Unsupported unary operator {op}."),
    };
}
