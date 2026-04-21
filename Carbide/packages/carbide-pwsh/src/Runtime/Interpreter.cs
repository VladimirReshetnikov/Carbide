using System.Collections;
using System.Collections.Specialized;
using System.Text;
using CarbidePwsh.Errors;
using CarbidePwsh.Lexer;
using CarbidePwsh.Parser.Ast;
using PwshParser = CarbidePwsh.Parser.Parser;

namespace CarbidePwsh.Runtime;

/// <summary>
/// Tree-walking evaluator. One <see cref="Interpreter"/> wraps one <see cref="Scope"/> and one
/// <see cref="TypeBridge"/>; a REPL keeps the interpreter alive across submissions so
/// variables persist.
/// </summary>
public sealed class Interpreter
{
    public Scope Scope { get; }
    public TypeBridge Types { get; }

    public Interpreter(Scope? scope = null, TypeBridge? types = null)
    {
        Scope = scope ?? new Scope();
        Types = types ?? new TypeBridge();
    }

    public object? Evaluate(ScriptAst script)
    {
        object? last = null;
        foreach (var s in script.Statements)
            last = EvaluateStatement(s);
        return last;
    }

    public object? EvaluateStatement(StatementAst statement) => statement switch
    {
        ExpressionStatementAst e => Eval(e.Expression),
        AssignmentStatementAst a => ExecuteAssignment(a),
        _ => throw new PwshRuntimeException($"Unsupported statement node: {statement.GetType().Name}", statement.Location),
    };

    private object? ExecuteAssignment(AssignmentStatementAst a)
    {
        var rhs = Eval(a.Value);
        if (a.Op != AssignmentOp.Assign)
        {
            var current = Eval(a.Target);
            rhs = a.Op switch
            {
                AssignmentOp.AddAssign => Operators.Binary(BinaryOp.Add, current, rhs),
                AssignmentOp.SubtractAssign => Operators.Binary(BinaryOp.Subtract, current, rhs),
                AssignmentOp.MultiplyAssign => Operators.Binary(BinaryOp.Multiply, current, rhs),
                AssignmentOp.DivideAssign => Operators.Binary(BinaryOp.Divide, current, rhs),
                AssignmentOp.ModuloAssign => Operators.Binary(BinaryOp.Modulo, current, rhs),
                _ => rhs,
            };
        }
        AssignTo(a.Target, rhs);
        return null;
    }

    private void AssignTo(ExpressionAst target, object? value)
    {
        switch (target)
        {
            case VariableAst v:
                Scope.Set(v.Scope, v.Name, value);
                return;
            case MemberAccessAst m when !m.IsInvocation:
            {
                var receiver = Eval(m.Target);
                if (m.IsStatic)
                {
                    if (receiver is not Type t)
                        throw new PwshRuntimeException(
                            "Left side of '::' assignment is not a type literal.", m.Location);
                    Types.SetStaticMember(t, m.MemberName, value, m.Location);
                    return;
                }
                if (receiver == null)
                    throw new PwshRuntimeException("Cannot assign to a member on a null reference.", m.Location);
                Types.SetInstanceMember(receiver, m.MemberName, value, m.Location);
                return;
            }
            default:
                throw new PwshRuntimeException(
                    $"Assignment target {target.GetType().Name} is not supported in Phase 1.", target.Location);
        }
    }

    public object? Eval(ExpressionAst expr) => expr switch
    {
        NumberLiteralAst n => n.Value,
        StringLiteralAst s => EvalString(s),
        BooleanLiteralAst b => b.Value,
        NullLiteralAst => null,
        VariableAst v => Scope.Get(v.Scope, v.Name),
        ArrayExpressionAst a => EvalArray(a),
        HashtableExpressionAst h => EvalHashtable(h),
        BinaryExpressionAst b => EvalBinary(b),
        UnaryExpressionAst u => EvalUnary(u),
        RangeExpressionAst r => EvalRange(r),
        ParenExpressionAst p => Eval(p.Inner),
        SubExpressionAst se => Evaluate(se.Body),
        TypeLiteralAst tl => Types.ResolveType(tl.TypeName, tl.Location),
        CastExpressionAst c => EvalCast(c),
        MemberAccessAst m => EvalMember(m),
        IndexerAst ix => EvalIndex(ix),
        _ => throw new PwshRuntimeException($"Unsupported expression node: {expr.GetType().Name}", expr.Location),
    };

    private object EvalString(StringLiteralAst s)
    {
        if (s.IsSingleQuoted)
        {
            var sb = new StringBuilder();
            foreach (var part in s.Parts)
            {
                if (part is LiteralPart lp) sb.Append(lp.Text);
            }
            return sb.ToString();
        }
        var result = new StringBuilder();
        foreach (var part in s.Parts)
        {
            switch (part)
            {
                case LiteralPart lp:
                    result.Append(lp.Text);
                    break;
                case VariablePart vp:
                    result.Append(Coercion.FormatAsString(Scope.Get(vp.Scope, vp.Name)));
                    break;
                case ExpressionPart ep:
                {
                    var inner = PwshParser.ParseString(ep.Source);
                    var val = Evaluate(inner);
                    result.Append(FormatForInterpolation(val));
                    break;
                }
            }
        }
        return result.ToString();
    }

    private static string FormatForInterpolation(object? value)
    {
        if (value == null) return "";
        if (value is string s) return s;
        if (value is Array arr)
        {
            var parts = new List<string>();
            foreach (var item in arr) parts.Add(Coercion.FormatAsString(item));
            return string.Join(" ", parts);
        }
        return Coercion.FormatAsString(value);
    }

    private object[] EvalArray(ArrayExpressionAst a)
    {
        var list = new List<object?>();
        foreach (var e in a.Elements)
        {
            var v = Eval(e);
            if (v is object[] inner) list.AddRange(inner);
            else list.Add(v);
        }
        return list.ToArray()!;
    }

    private object EvalHashtable(HashtableExpressionAst h)
    {
        var dict = new OrderedDictionary();
        foreach (var (keyExpr, valExpr) in h.Entries)
        {
            var key = Eval(keyExpr) ?? throw new PwshRuntimeException("Hashtable key cannot be null.", keyExpr.Location);
            var value = Eval(valExpr);
            dict[key] = value;
        }
        return dict;
    }

    private object? EvalBinary(BinaryExpressionAst b)
    {
        // Short-circuit for -and / -or.
        if (b.Op == BinaryOp.And)
        {
            var l = Eval(b.Left);
            if (!Coercion.CoerceToBool(l)) return false;
            return Coercion.CoerceToBool(Eval(b.Right));
        }
        if (b.Op == BinaryOp.Or)
        {
            var l = Eval(b.Left);
            if (Coercion.CoerceToBool(l)) return true;
            return Coercion.CoerceToBool(Eval(b.Right));
        }
        return Operators.Binary(b.Op, Eval(b.Left), Eval(b.Right));
    }

    private object? EvalUnary(UnaryExpressionAst u) => Operators.Unary(u.Op, Eval(u.Operand));

    private object EvalRange(RangeExpressionAst r)
    {
        var startVal = Eval(r.Start);
        var endVal = Eval(r.End);
        if (startVal is char ca && endVal is char cb)
            return BuildCharRange(ca, cb);
        var a = (int)Coercion.ToInt64(startVal);
        var b = (int)Coercion.ToInt64(endVal);
        return BuildIntRange(a, b);
    }

    private static object[] BuildIntRange(int start, int end)
    {
        var count = Math.Abs(end - start) + 1;
        var arr = new object[count];
        if (start <= end)
            for (int i = 0; i < count; i++) arr[i] = start + i;
        else
            for (int i = 0; i < count; i++) arr[i] = start - i;
        return arr;
    }

    private static object[] BuildCharRange(char start, char end)
    {
        var count = Math.Abs(end - start) + 1;
        var arr = new object[count];
        if (start <= end)
            for (int i = 0; i < count; i++) arr[i] = (char)(start + i);
        else
            for (int i = 0; i < count; i++) arr[i] = (char)(start - i);
        return arr;
    }

    private object? EvalCast(CastExpressionAst c)
    {
        var target = Types.ResolveType(c.TargetType.TypeName, c.TargetType.Location);
        var value = Eval(c.Value);
        return Coercion.To(value, target);
    }

    private object? EvalMember(MemberAccessAst m)
    {
        var receiver = Eval(m.Target);

        // Static dispatch: receiver is a Type (from a type literal).
        if (m.IsStatic)
        {
            if (receiver is not Type t)
                throw new PwshRuntimeException("Left side of '::' is not a type.", m.Location);
            if (m.IsInvocation)
            {
                var args = m.Arguments!.Select(Eval).ToArray();
                return Types.InvokeStaticMethod(t, m.MemberName, args, m.Location);
            }
            return Types.GetStaticMember(t, m.MemberName, m.Location);
        }

        if (receiver == null)
            throw new PwshRuntimeException("You cannot call a method on a null-valued expression.", m.Location);

        if (m.IsInvocation)
        {
            var args = m.Arguments!.Select(Eval).ToArray();
            if (receiver is ConstructorInvoker ctor)
                return Types.InvokeStaticMethod(ctor.Type, "new", args, m.Location);
            return Types.InvokeInstanceMethod(receiver, m.MemberName, args, m.Location);
        }
        return Types.GetInstanceMember(receiver, m.MemberName, m.Location);
    }

    private object? EvalIndex(IndexerAst ix)
    {
        var target = Eval(ix.Target);
        if (target == null)
            throw new PwshRuntimeException("Cannot index into a null value.", ix.Location);
        var index = Eval(ix.Index);
        return Types.GetIndex(target, index, ix.Location);
    }
}
