using System.Globalization;
using CarbideBash.Runtime;

namespace CarbideBash.Builtins;

/// <summary>
/// Evaluates <c>test</c> / <c>[</c> / <c>[[</c> expression argv. Supports the common unary
/// file tests (<c>-e</c>, <c>-f</c>, <c>-d</c>, <c>-s</c>, <c>-r</c>, <c>-w</c>, <c>-x</c>,
/// <c>-z</c>, <c>-n</c>), numeric comparison (<c>-eq</c>, <c>-ne</c>, <c>-lt</c>, <c>-le</c>,
/// <c>-gt</c>, <c>-ge</c>), string comparison (<c>=</c>, <c>==</c>, <c>!=</c>, <c>&lt;</c>,
/// <c>&gt;</c>), logical negation (<c>!</c>), and logical combinators (<c>-a</c> / <c>-o</c>).
/// </summary>
internal static class BashTest
{
    public static bool Evaluate(IReadOnlyList<string> args, Interpreter interp)
    {
        if (args.Count == 0) return false;
        int position = 0;
        return EvaluateOr(args, interp, ref position);
    }

    private static bool EvaluateOr(IReadOnlyList<string> args, Interpreter interp, ref int position)
    {
        var left = EvaluateAnd(args, interp, ref position);
        while (position < args.Count && args[position] == "-o")
        {
            position++;
            var right = EvaluateAnd(args, interp, ref position);
            left = left || right;
        }
        return left;
    }

    private static bool EvaluateAnd(IReadOnlyList<string> args, Interpreter interp, ref int position)
    {
        var left = EvaluatePrimary(args, interp, ref position);
        while (position < args.Count && args[position] == "-a")
        {
            position++;
            var right = EvaluatePrimary(args, interp, ref position);
            left = left && right;
        }
        return left;
    }

    private static bool EvaluatePrimary(IReadOnlyList<string> args, Interpreter interp, ref int position)
    {
        if (position >= args.Count) return false;

        if (args[position] == "!")
        {
            position++;
            return !EvaluatePrimary(args, interp, ref position);
        }
        if (args[position] == "(")
        {
            position++;
            var inner = EvaluateOr(args, interp, ref position);
            if (position < args.Count && args[position] == ")") position++;
            return inner;
        }

        // Unary prefix op?
        var a = args[position];
        if (a.StartsWith('-') && a.Length == 2 && position + 1 < args.Count && !IsBinaryOp(args[position + 1]))
        {
            var target = args[position + 1];
            position += 2;
            return EvalUnary(a, target, interp);
        }

        // Binary infix: left op right.
        if (position + 2 < args.Count)
        {
            var op = args[position + 1];
            if (IsBinaryOp(op))
            {
                var left = args[position];
                var right = args[position + 2];
                position += 3;
                return EvalBinary(left, op, right);
            }
        }

        if (position + 1 >= args.Count || IsLogicalOp(args[position + 1]) || args[position + 1] == ")")
        {
            // Single-token truthiness: non-empty string is true.
            var val = args[position];
            position++;
            return val.Length > 0;
        }

        // Fallback: grab current, advance, return non-empty.
        var v2 = args[position];
        position++;
        return v2.Length > 0;
    }

    private static bool IsBinaryOp(string s)
        => s is "=" or "==" or "!=" or "<" or ">"
            or "-eq" or "-ne" or "-lt" or "-le" or "-gt" or "-ge";

    private static bool IsLogicalOp(string s)
        => s is "-a" or "-o";

    private static bool EvalUnary(string op, string target, Interpreter interp)
    {
        var vfs = interp.Context.Vfs;
        switch (op)
        {
            case "-e": return vfs.Exists(target);
            case "-f": return vfs.IsFile(target);
            case "-d": return vfs.IsDirectory(target);
            case "-s":
            {
                if (vfs.Resolve(target) is CarbideShellCore.Vfs.VfsFile f) return f.Length > 0;
                return false;
            }
            case "-r": return vfs.Exists(target);
            case "-w": return vfs.Exists(target);
            case "-x": return vfs.Exists(target);
            case "-z": return target.Length == 0;
            case "-n": return target.Length > 0;
            default: return false;
        }
    }

    private static bool EvalBinary(string left, string op, string right)
    {
        switch (op)
        {
            case "=":
            case "==":
                return string.Equals(left, right, StringComparison.Ordinal);
            case "!=":
                return !string.Equals(left, right, StringComparison.Ordinal);
            case "<":
                return string.CompareOrdinal(left, right) < 0;
            case ">":
                return string.CompareOrdinal(left, right) > 0;
            case "-eq":
            case "-ne":
            case "-lt":
            case "-le":
            case "-gt":
            case "-ge":
            {
                if (!long.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return false;
                if (!long.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r)) return false;
                return op switch
                {
                    "-eq" => l == r,
                    "-ne" => l != r,
                    "-lt" => l < r,
                    "-le" => l <= r,
                    "-gt" => l > r,
                    "-ge" => l >= r,
                    _ => false,
                };
            }
            default: return false;
        }
    }
}
