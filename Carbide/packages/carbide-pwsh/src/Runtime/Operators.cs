using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
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

            BinaryOp.Match or BinaryOp.IMatch => MatchOp(left, right, ignoreCase: true),
            BinaryOp.CMatch => MatchOp(left, right, ignoreCase: false),
            BinaryOp.NotMatch or BinaryOp.INotMatch => NotMatchOp(left, right, ignoreCase: true),
            BinaryOp.CNotMatch => NotMatchOp(left, right, ignoreCase: false),
            BinaryOp.Replace or BinaryOp.IReplace => ReplaceOp(left, right, ignoreCase: true),
            BinaryOp.CReplace => ReplaceOp(left, right, ignoreCase: false),
            BinaryOp.Like or BinaryOp.ILike => LikeOp(left, right, ignoreCase: true, negate: false),
            BinaryOp.CLike => LikeOp(left, right, ignoreCase: false, negate: false),
            BinaryOp.NotLike or BinaryOp.INotLike => LikeOp(left, right, ignoreCase: true, negate: true),
            BinaryOp.CNotLike => LikeOp(left, right, ignoreCase: false, negate: true),

            BinaryOp.Contains or BinaryOp.ICContains => ContainsOp(left, right, ignoreCase: true),
            BinaryOp.CContains => ContainsOp(left, right, ignoreCase: false),
            BinaryOp.NotContains or BinaryOp.INotContains => !ContainsOp(left, right, ignoreCase: true),
            BinaryOp.CNotContains => !ContainsOp(left, right, ignoreCase: false),
            BinaryOp.In or BinaryOp.IIn => ContainsOp(right, left, ignoreCase: true),
            BinaryOp.CIn => ContainsOp(right, left, ignoreCase: false),
            BinaryOp.NotIn or BinaryOp.INotIn => !ContainsOp(right, left, ignoreCase: true),
            BinaryOp.CNotIn => !ContainsOp(right, left, ignoreCase: false),

            BinaryOp.Format => FormatOp(left, right),
            BinaryOp.Join => JoinOp(left, right),
            BinaryOp.Split => SplitOp(left, right),

            _ => throw new PwshRuntimeException($"Unsupported binary operator {op}.")
        };
    }

    // ---- Regex / glob / containment / format / join / split ----

    private static readonly AsyncLocal<object?[]?> _matchesSink = new();
    public static object?[]? LastMatches
    {
        get => _matchesSink.Value;
        set => _matchesSink.Value = value;
    }

    private static object MatchOp(object? left, object? right, bool ignoreCase)
    {
        var pattern = Coercion.FormatAsString(right);
        var options = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
        if (left is IEnumerable en && left is not string)
        {
            var result = new List<object?>();
            foreach (var item in en)
            {
                var s = Coercion.FormatAsString(item);
                if (Regex.IsMatch(s, pattern, options)) result.Add(item);
            }
            return result.ToArray();
        }
        var input = Coercion.FormatAsString(left);
        var m = Regex.Match(input, pattern, options);
        if (!m.Success) return false;
        LastMatches = BuildMatchesArray(m);
        return true;
    }

    private static object NotMatchOp(object? left, object? right, bool ignoreCase)
    {
        var pattern = Coercion.FormatAsString(right);
        var options = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
        if (left is IEnumerable en && left is not string)
        {
            var result = new List<object?>();
            foreach (var item in en)
            {
                var s = Coercion.FormatAsString(item);
                if (!Regex.IsMatch(s, pattern, options)) result.Add(item);
            }
            return result.ToArray();
        }
        var input = Coercion.FormatAsString(left);
        return !Regex.IsMatch(input, pattern, options);
    }

    private static object?[] BuildMatchesArray(Match m)
    {
        var arr = new object?[m.Groups.Count];
        for (int i = 0; i < m.Groups.Count; i++) arr[i] = m.Groups[i].Value;
        return arr;
    }

    private static object ReplaceOp(object? left, object? right, bool ignoreCase)
    {
        // Right is 1- or 2-element array: pattern [, replacement]. The replacement may be a
        // literal string or a ScriptBlock that receives each match as $_.
        string pattern;
        object? replacement = "";
        if (right is object?[] rarr)
        {
            pattern = Coercion.FormatAsString(rarr.Length > 0 ? rarr[0] : "");
            if (rarr.Length > 1) replacement = rarr[1];
        }
        else if (right is System.Collections.IEnumerable renum && right is not string)
        {
            var list = new List<object?>();
            foreach (var item in renum) list.Add(item);
            pattern = Coercion.FormatAsString(list.Count > 0 ? list[0] : "");
            if (list.Count > 1) replacement = list[1];
        }
        else
        {
            pattern = Coercion.FormatAsString(right);
        }
        var options = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;

        string DoReplace(string source)
        {
            if (replacement is ScriptBlock sb)
            {
                return Regex.Replace(source, pattern,
                    m => Coercion.FormatAsString(sb.InvokeForPipelineItem(m)), options);
            }
            return Regex.Replace(source, pattern, Coercion.FormatAsString(replacement), options);
        }

        if (left is System.Collections.IEnumerable en && left is not string)
        {
            var result = new List<object?>();
            foreach (var item in en) result.Add(DoReplace(Coercion.FormatAsString(item)));
            return result.ToArray();
        }
        return DoReplace(Coercion.FormatAsString(left));
    }

    private static object LikeOp(object? left, object? right, bool ignoreCase, bool negate)
    {
        var pattern = "^" + Regex.Escape(Coercion.FormatAsString(right))
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        var options = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
        if (left is System.Collections.IEnumerable en && left is not string)
        {
            var result = new List<object?>();
            foreach (var item in en)
            {
                var match = Regex.IsMatch(Coercion.FormatAsString(item), pattern, options);
                if (negate ? !match : match) result.Add(item);
            }
            return result.ToArray();
        }
        var ok = Regex.IsMatch(Coercion.FormatAsString(left), pattern, options);
        return negate ? !ok : ok;
    }

    private static bool ContainsOp(object? collection, object? element, bool ignoreCase)
    {
        if (collection is string s)
        {
            return s.Contains(Coercion.FormatAsString(element),
                ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        if (collection is System.Collections.IEnumerable en)
        {
            foreach (var item in en)
            {
                if ((bool)Binary(ignoreCase ? BinaryOp.IEqual : BinaryOp.CEqual, item, element)!)
                    return true;
            }
            return false;
        }
        return false;
    }

    private static string FormatOp(object? left, object? right)
    {
        var format = Coercion.FormatAsString(left);
        object?[] args;
        if (right is object?[] rarr)
        {
            args = rarr;
        }
        else if (right is System.Collections.IEnumerable en && right is not string)
        {
            var list = new List<object?>();
            foreach (var item in en) list.Add(item);
            args = list.ToArray();
        }
        else
        {
            args = new[] { right };
        }
        try { return string.Format(CultureInfo.InvariantCulture, format, args); }
        catch (FormatException ex)
        {
            throw new PwshRuntimeException($"Invalid -f format: {ex.Message}");
        }
    }

    private static object JoinOp(object? left, object? right)
    {
        var separator = Coercion.FormatAsString(right);
        if (left is System.Collections.IEnumerable en && left is not string)
        {
            var parts = new List<string>();
            foreach (var item in en) parts.Add(Coercion.FormatAsString(item));
            return string.Join(separator, parts);
        }
        return Coercion.FormatAsString(left);
    }

    private static object SplitOp(object? left, object? right)
    {
        var pattern = Coercion.FormatAsString(right);
        var source = Coercion.FormatAsString(left);
        return Regex.Split(source, pattern);
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
        if (typeValue is RuntimeClass rc)
            return value is RuntimeInstance ri && ri.Class.Name == rc.Name;
        if (typeValue is RuntimeEnum re)
            return value is EnumValue ev && ev.EnumType.Name == re.Name;
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
