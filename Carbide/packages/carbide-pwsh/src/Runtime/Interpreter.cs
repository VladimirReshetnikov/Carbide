using System.Collections;
using System.Collections.Specialized;
using System.Text;
using CarbidePwsh.Cmdlets;
using CarbidePwsh.Cmdlets.Discovery;
using CarbidePwsh.Errors;
using CarbidePwsh.Lexer;
using CarbidePwsh.Parser.Ast;
using CarbideShellCore.Apps;
using CarbideShellCore.Vfs;
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

    // Pipeline infrastructure — optional. When null, pipeline statements throw a helpful
    // error. When wired up by the host, pipelines dispatch to the registered cmdlets.
    public VirtualFileSystem? Vfs { get; set; }
    public CmdletRegistry? Registry { get; set; }
    public TextWriter? PipelineOutput { get; set; }
    public TextWriter? PipelineError { get; set; }

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

    public FunctionRegistry? Functions { get; set; }
    public ClassRegistry? Classes { get; set; }
    public AppRegistry? Apps { get; set; }
    public CarbideShellCore.Dispatch.ShellDispatcher? Dispatcher { get; set; }
    public CarbideShellCore.Env.EnvVarStore? Env { get; set; }

    /// <summary>
    /// Active pwsh drive — <c>FileSystem</c> by default, or one of <c>Env</c>, <c>Alias</c>,
    /// <c>Function</c>, <c>Variable</c> when the user <c>cd</c>'d into a provider. Read by
    /// the prompt builder and by the item cmdlets when no drive qualifier is present on
    /// the supplied path. <c>Set-Location X:</c> is the only thing that mutates it.
    /// </summary>
    public PwshDriveKind CurrentDrive { get; set; } = PwshDriveKind.FileSystem;

    /// <summary>Backs <c>Push-Location</c> / <c>Pop-Location</c>.</summary>
    public Stack<(PwshDriveKind Drive, string Path)> LocationStack { get; } = new();

    /// <summary>Minimal builtin-module state used by <c>Get-Module</c> / <c>Import-Module</c>.</summary>
    public HashSet<string> ImportedModules { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised to run a script file path when the interpreter encounters it as a
    /// command name. Set by <see cref="Host.ShellHost"/>; returns the result of the script.</summary>
    public Func<string, bool, IReadOnlyList<object?>, object?>? RunScriptFile { get; set; }

    /// <summary>Raised to invoke a .NET entry-point DLL by VFS path. Returns the exit code.</summary>
    public Func<string, IReadOnlyList<object?>, int>? RunApp { get; set; }

    public object? EvaluateStatement(StatementAst statement) => statement switch
    {
        ExpressionStatementAst e => Eval(e.Expression),
        AssignmentStatementAst a => ExecuteAssignment(a),
        PipelineAst p => ExecutePipeline(p),
        IfStatementAst i => ExecuteIf(i),
        WhileStatementAst w => ExecuteWhile(w),
        DoWhileStatementAst dw => ExecuteDoWhile(dw),
        ForStatementAst f => ExecuteFor(f),
        ForEachStatementAst fe => ExecuteForEach(fe),
        SwitchStatementAst sw => ExecuteSwitch(sw),
        BreakStatementAst br => throw new PwshBreakException(br.Label),
        ContinueStatementAst co => throw new PwshContinueException(co.Label),
        ReturnStatementAst r => throw new PwshReturnException(r.Value != null ? Eval(r.Value) : null),
        ThrowStatementAst th => ExecuteThrow(th),
        TryStatementAst tr => ExecuteTry(tr),
        FunctionDefinitionAst fd => ExecuteFunctionDefinition(fd),
        ClassDefinitionAst cd => ExecuteClassDefinition(cd),
        EnumDefinitionAst ed => ExecuteEnumDefinition(ed),
        BlockStatementAst bs => ExecuteBlock(bs),
        _ => throw new PwshRuntimeException($"Unsupported statement node: {statement.GetType().Name}", statement.Location),
    };

    private object? ExecuteBlock(BlockStatementAst block)
    {
        object? last = null;
        foreach (var s in block.Statements) last = EvaluateStatement(s);
        return last;
    }

    // ---------- Control flow ----------

    private object? ExecuteIf(IfStatementAst ast)
    {
        foreach (var (cond, body) in ast.Branches)
        {
            if (Coercion.CoerceToBool(Eval(cond)))
                return Evaluate(body);
        }
        if (ast.ElseBody != null) return Evaluate(ast.ElseBody);
        return null;
    }

    private object? ExecuteWhile(WhileStatementAst ast)
    {
        var results = new List<object?>();
        while (Coercion.CoerceToBool(Eval(ast.Condition)))
        {
            try { var v = Evaluate(ast.Body); if (v != null) results.Add(v); }
            catch (PwshBreakException) { break; }
            catch (PwshContinueException) { continue; }
        }
        return CollectResults(results);
    }

    private object? ExecuteDoWhile(DoWhileStatementAst ast)
    {
        var results = new List<object?>();
        do
        {
            try { var v = Evaluate(ast.Body); if (v != null) results.Add(v); }
            catch (PwshBreakException) { break; }
            catch (PwshContinueException) { continue; }
        } while (ast.IsUntil
            ? !Coercion.CoerceToBool(Eval(ast.Condition))
            : Coercion.CoerceToBool(Eval(ast.Condition)));
        return CollectResults(results);
    }

    private object? ExecuteFor(ForStatementAst ast)
    {
        var results = new List<object?>();
        if (ast.Init != null) EvaluateStatement(ast.Init);
        while (true)
        {
            if (ast.Condition != null && !Coercion.CoerceToBool(Eval(ast.Condition))) break;
            try { var v = Evaluate(ast.Body); if (v != null) results.Add(v); }
            catch (PwshBreakException) { break; }
            catch (PwshContinueException) { /* fall through to update */ }
            if (ast.Update != null) EvaluateStatement(ast.Update);
        }
        return CollectResults(results);
    }

    private object? ExecuteForEach(ForEachStatementAst ast)
    {
        var collection = Eval(ast.Collection);
        var results = new List<object?>();
        IEnumerable<object?> items = collection switch
        {
            null => Array.Empty<object?>(),
            string s => new object?[] { s },
            System.Collections.IDictionary d => new object?[] { d },
            System.Collections.IEnumerable en => en.Cast<object?>(),
            _ => new[] { collection },
        };
        foreach (var item in items)
        {
            Scope.Set(null, ast.VariableName, item);
            try { var v = Evaluate(ast.Body); if (v != null) results.Add(v); }
            catch (PwshBreakException) { break; }
            catch (PwshContinueException) { continue; }
        }
        return CollectResults(results);
    }

    private object? ExecuteSwitch(SwitchStatementAst ast)
    {
        var value = Eval(ast.Condition);
        foreach (var (pattern, body) in ast.Cases)
        {
            var patternValue = Eval(pattern);
            var op = ast.IsWildcard ? BinaryOp.Like : BinaryOp.Equal;
            if ((bool)Operators.Binary(op, value, patternValue)!)
            {
                try { return Evaluate(body); }
                catch (PwshBreakException) { return null; }
            }
        }
        if (ast.DefaultBody != null)
        {
            try { return Evaluate(ast.DefaultBody); }
            catch (PwshBreakException) { return null; }
        }
        return null;
    }

    private static object? CollectResults(List<object?> results)
    {
        return results.Count switch
        {
            0 => null,
            1 => results[0],
            _ => results.ToArray(),
        };
    }

    // ---------- Error handling ----------

    private object? ExecuteThrow(ThrowStatementAst ast)
    {
        var value = ast.Value != null ? Eval(ast.Value) : null;
        ErrorRecord record;
        if (value is ErrorRecord er) record = er;
        else if (value is Exception ex) record = new ErrorRecord(ex);
        else
        {
            var message = Coercion.FormatAsString(value);
            if (string.IsNullOrEmpty(message)) message = "ScriptHalted";
            record = new ErrorRecord(new PwshRuntimeException(message, ast.Location), targetObject: value);
        }
        throw new PwshTerminatingException(record);
    }

    private object? ExecuteTry(TryStatementAst ast)
    {
        try
        {
            return Evaluate(ast.TryBody);
        }
        catch (PwshBreakException) { throw; }
        catch (PwshContinueException) { throw; }
        catch (PwshReturnException) { throw; }
        catch (Exception ex)
        {
            var record = ex is PwshTerminatingException pt ? pt.Error : new ErrorRecord(ex);
            foreach (var clause in ast.CatchClauses)
            {
                if (!ClauseMatches(clause, record.Exception)) continue;
                var savedUnderscore = Scope.Get(null, "_");
                Scope.Set(null, "_", record);
                try
                {
                    return Evaluate(clause.Body);
                }
                finally
                {
                    Scope.Set(null, "_", savedUnderscore);
                }
            }
            throw;
        }
        finally
        {
            if (ast.FinallyBody != null)
            {
                try { Evaluate(ast.FinallyBody); }
                catch (PwshBreakException) { throw; }
                catch (PwshContinueException) { throw; }
            }
        }
    }

    private bool ClauseMatches(CatchClauseAst clause, Exception ex)
    {
        if (clause.TypeFilters.Count == 0) return true;
        foreach (var filter in clause.TypeFilters)
        {
            try
            {
                var t = Types.ResolveType(filter.TypeName, filter.Location);
                if (t.IsInstanceOfType(ex)) return true;
            }
            catch { /* try next filter */ }
        }
        return false;
    }

    // ---------- Function / class / enum definitions ----------

    private object? ExecuteFunctionDefinition(FunctionDefinitionAst ast)
    {
        if (Functions == null) throw new PwshRuntimeException("Function registry not wired up.", ast.Location);
        Functions.Register(new ScriptFunction(ast));
        return null;
    }

    private object? ExecuteClassDefinition(ClassDefinitionAst ast)
    {
        if (Classes == null) throw new PwshRuntimeException("Class registry not wired up.", ast.Location);
        Classes.Register(new RuntimeClass(ast));
        return null;
    }

    private object? ExecuteEnumDefinition(EnumDefinitionAst ast)
    {
        if (Classes == null) throw new PwshRuntimeException("Class registry not wired up.", ast.Location);
        var members = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long next = 0;
        foreach (var m in ast.Members)
        {
            var value = m.Value ?? next;
            members[m.Name] = value;
            next = value + 1;
        }
        Classes.Register(new RuntimeEnum(ast.Name, members));
        return null;
    }

    private object? ExecutePipeline(PipelineAst pipeline)
    {
        if (pipeline.Stages.Count == 1 && pipeline.Stages[0] is ExpressionAst expr)
        {
            return Eval(expr);
        }
        if (Registry == null || Vfs == null || PipelineOutput == null || PipelineError == null)
            throw new PwshRuntimeException(
                "Pipeline infrastructure is not wired up (ShellHost should install it).",
                pipeline.Location);
        var ctx = new CmdletContext(this, Vfs, PipelineOutput, PipelineError);
        return Cmdlets.Pipeline.Run(pipeline, ctx, Registry);
    }

    private object? ExecuteAssignment(AssignmentStatementAst a)
    {
        ExecuteAssignmentCore(a.Target, a.Op, a.Value, out _);
        return null;
    }

    private void ExecuteAssignmentCore(ExpressionAst target, AssignmentOp op, ExpressionAst valueExpression, out object? assignedValue)
    {
        var rhs = Eval(valueExpression);
        if (op != AssignmentOp.Assign)
        {
            var current = Eval(target);
            rhs = op switch
            {
                AssignmentOp.AddAssign => Operators.Binary(BinaryOp.Add, current, rhs),
                AssignmentOp.SubtractAssign => Operators.Binary(BinaryOp.Subtract, current, rhs),
                AssignmentOp.MultiplyAssign => Operators.Binary(BinaryOp.Multiply, current, rhs),
                AssignmentOp.DivideAssign => Operators.Binary(BinaryOp.Divide, current, rhs),
                AssignmentOp.ModuloAssign => Operators.Binary(BinaryOp.Modulo, current, rhs),
                _ => rhs,
            };
        }
        AssignTo(target, rhs);
        assignedValue = rhs;
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
                if (receiver is RuntimeInstance inst)
                {
                    // Coerce to declared type when possible.
                    var prop = inst.Class.Properties.FirstOrDefault(p =>
                        string.Equals(p.Name, m.MemberName, StringComparison.OrdinalIgnoreCase));
                    if (prop?.TypeConstraint != null && value != null)
                    {
                        try
                        {
                            var t = Types.ResolveType(prop.TypeConstraint.TypeName, prop.TypeConstraint.Location);
                            value = Coercion.To(value, t);
                        }
                        catch { /* leave */ }
                    }
                    inst.Fields[m.MemberName] = value;
                    return;
                }
                Types.SetInstanceMember(receiver, m.MemberName, value, m.Location);
                return;
            }
            case DynamicMemberAccessAst m when !m.IsInvocation:
            {
                var memberName = Coercion.FormatAsString(Eval(m.MemberNameExpression));
                var receiver = Eval(m.Target);
                if (m.IsStatic)
                {
                    if (receiver is not Type t)
                        throw new PwshRuntimeException(
                            "Left side of '::' assignment is not a type literal.", m.Location);
                    Types.SetStaticMember(t, memberName, value, m.Location);
                    return;
                }
                if (receiver == null)
                    throw new PwshRuntimeException("Cannot assign to a member on a null reference.", m.Location);
                Types.SetInstanceMember(receiver, memberName, value, m.Location);
                return;
            }
            case IndexerAst ix:
            {
                var receiver = Eval(ix.Target);
                var idx = Eval(ix.Index);
                if (receiver is System.Collections.IDictionary d)
                {
                    d[idx!] = value; return;
                }
                if (receiver is Array arr)
                {
                    var iint = (int)Coercion.ToInt64(idx);
                    if (iint < 0) iint += arr.Length;
                    arr.SetValue(value, iint);
                    return;
                }
                if (receiver is System.Collections.IList list)
                {
                    var iint = (int)Coercion.ToInt64(idx);
                    if (iint < 0) iint += list.Count;
                    list[iint] = value;
                    return;
                }
                throw new PwshRuntimeException("Target is not indexable for assignment.", target.Location);
            }
            case ArrayExpressionAst arrLhs:
            {
                // Destructuring: `$a, $b = 1, 2` assigns 1 to $a and 2 to $b. When the RHS
                // has more elements than the LHS, pwsh puts the remainder as an array into
                // the last target. When it has fewer, extra LHS targets receive $null.
                var items = value switch
                {
                    null => Array.Empty<object?>(),
                    string s => new object?[] { s },
                    System.Collections.IEnumerable e when value is not System.Collections.IDictionary
                        => e.Cast<object?>().ToArray(),
                    _ => new object?[] { value },
                };
                int n = arrLhs.Elements.Count;
                for (int i = 0; i < n; i++)
                {
                    object? v;
                    if (i == n - 1 && items.Length > n)
                    {
                        var rest = new object?[items.Length - n + 1];
                        Array.Copy(items, i, rest, 0, rest.Length);
                        v = rest;
                    }
                    else
                    {
                        v = i < items.Length ? items[i] : null;
                    }
                    AssignTo(arrLhs.Elements[i], v);
                }
                return;
            }
            default:
                throw new PwshRuntimeException(
                    $"Assignment target {target.GetType().Name} is not supported.", target.Location);
        }
    }

    public object? Eval(ExpressionAst expr) => expr switch
    {
        NumberLiteralAst n => n.Value,
        StringLiteralAst s => EvalString(s),
        BooleanLiteralAst b => b.Value,
        NullLiteralAst => null,
        VariableAst v => EvalVariable(v),
        ArrayExpressionAst a => EvalArray(a),
        HashtableExpressionAst h => EvalHashtable(h),
        AssignmentExpressionAst a => EvalAssignmentExpression(a),
        BinaryExpressionAst b => EvalBinary(b),
        ConditionalExpressionAst c => EvalConditional(c),
        UnaryExpressionAst u => EvalUnary(u),
        RangeExpressionAst r => EvalRange(r),
        ParenExpressionAst p => Eval(p.Inner),
        SubExpressionAst se => Evaluate(se.Body),
        TypeLiteralAst tl => ResolveTypeExpression(tl),
        CastExpressionAst c => EvalCast(c),
        MemberAccessAst m => EvalMember(m),
        DynamicMemberAccessAst m => EvalDynamicMember(m),
        IndexerAst ix => EvalIndex(ix),
        ScriptBlockAst sb => new ScriptBlock(sb.Body, this),
        _ => throw new PwshRuntimeException($"Unsupported expression node: {expr.GetType().Name}", expr.Location),
    };

    private object ResolveTypeExpression(TypeLiteralAst tl)
    {
        // User-defined class / enum (no generics / arrays at this layer).
        if (tl.GenericArguments.Count == 0 && tl.ArrayRank == 0 && Classes != null)
        {
            if (Classes.TryGetClass(tl.TypeName, out var cls) && cls != null) return cls;
            if (Classes.TryGetEnum(tl.TypeName, out var en) && en != null) return en;
        }

        Type baseType;
        if (tl.GenericArguments.Count > 0)
        {
            // `[HashSet[string]]` → resolve the arity-suffixed open definition `HashSet`1`
            // directly; bypass the non-generic name (it doesn't exist in the BCL).
            var arity = tl.GenericArguments.Count;
            baseType = Types.ResolveType($"{tl.TypeName}`{arity}", tl.Location);
            var typeArgs = tl.GenericArguments
                .Select(ga => (Type)ResolveTypeExpression(ga))
                .ToArray();
            baseType = baseType.MakeGenericType(typeArgs);
        }
        else
        {
            baseType = Types.ResolveType(tl.TypeName, tl.Location);
        }

        if (tl.ArrayRank > 0)
        {
            for (int i = 0; i < tl.ArrayRank; i++) baseType = baseType.MakeArrayType();
        }
        return baseType;
    }

    private object? EvalVariable(VariableAst v)
    {
        // $PWD: special binding that reflects the VFS's current location if wired up.
        if (v.Scope == null && v.Name.Equals("PWD", StringComparison.OrdinalIgnoreCase) && Vfs != null)
            return Vfs.CurrentLocation;
        if (v.Scope == null && v.Name.Equals("Matches", StringComparison.OrdinalIgnoreCase))
            return Operators.LastMatches;
        return Scope.Get(v.Scope, v.Name);
    }

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
        // pwsh's `@(...)` / comma-list semantics:
        //  - Explicit comma-separated list (>= 2 elements) — each element is a slot in the
        //    result, nested arrays are preserved: `@(@(1,2), @(3,4))` is a 2-element array
        //    whose entries are the two inner arrays.
        //  - Single-expression `@(expr)` — if `expr` yields an enumerable, flatten it to a
        //    materialized array; if it yields a scalar, wrap to a one-element array. This
        //    is the canonical `@(Get-Process)` idiom.
        if (a.Elements.Count == 1)
        {
            var single = Eval(a.Elements[0]);
            return single switch
            {
                null => Array.Empty<object>(),
                object[] arr => arr,
                string s => new object[] { s },
                System.Collections.IDictionary d => new object[] { d },
                System.Collections.IEnumerable en => en.Cast<object>().ToArray(),
                _ => new object[] { single },
            };
        }
        var list = new List<object?>(a.Elements.Count);
        foreach (var e in a.Elements) list.Add(Eval(e));
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

    private object? EvalConditional(ConditionalExpressionAst c)
        => Coercion.CoerceToBool(Eval(c.Condition)) ? Eval(c.WhenTrue) : Eval(c.WhenFalse);

    private object? EvalAssignmentExpression(AssignmentExpressionAst a)
    {
        ExecuteAssignmentCore(a.Target, a.Op, a.Value, out var assignedValue);
        return assignedValue;
    }

    private object? EvalUnary(UnaryExpressionAst u)
    {
        // Pre/post-increment/decrement mutate the operand variable.
        if (u.Op is UnaryOp.PreIncrement or UnaryOp.PreDecrement
                or UnaryOp.PostIncrement or UnaryOp.PostDecrement)
        {
            var current = Eval(u.Operand);
            var asNum = current is null ? 0 : Coercion.ToInt64(current);
            var delta = u.Op is UnaryOp.PreIncrement or UnaryOp.PostIncrement ? 1 : -1;
            var updated = asNum + delta;
            object updatedBoxed = updated >= int.MinValue && updated <= int.MaxValue ? (object)(int)updated : updated;
            AssignTo(u.Operand, updatedBoxed);
            return u.Op is UnaryOp.PreIncrement or UnaryOp.PreDecrement ? updatedBoxed : (current ?? 0);
        }
        return Operators.Unary(u.Op, Eval(u.Operand));
    }

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
        // Runtime class cast: accept hashtable or (single) positional value.
        if (Classes != null)
        {
            if (Classes.TryGetClass(c.TargetType.TypeName, out var cls) && cls != null)
            {
                var value = Eval(c.Value);
                if (value is RuntimeInstance ri && ri.Class == cls) return ri;
                var args = value is System.Collections.IDictionary ? new List<object?> { value } :
                           value is Array arr ? arr.Cast<object?>().ToList() :
                           new List<object?> { value };
                return ConstructInstance(cls, args, c.Location);
            }
            if (Classes.TryGetEnum(c.TargetType.TypeName, out var en) && en != null)
            {
                var value = Eval(c.Value);
                return value switch
                {
                    EnumValue ev when ev.EnumType.Name == en.Name => ev,
                    string s => en.FromName(s) ?? throw new PwshRuntimeException(
                        $"Enum [{en.Name}] has no member '{s}'.", c.Location),
                    _ => en.FromValue(Coercion.ToInt64(value))!,
                };
            }
        }
        var target = Types.ResolveType(c.TargetType.TypeName, c.TargetType.Location);
        var val = Eval(c.Value);
        return Coercion.To(val, target);
    }

    private object? EvalMember(MemberAccessAst m)
    {
        var receiver = Eval(m.Target);
        return EvalMemberCore(receiver, m.MemberName, m.IsStatic, m.IsInvocation, m.Arguments, m.Location);
    }

    private object? EvalDynamicMember(DynamicMemberAccessAst m)
    {
        var receiver = Eval(m.Target);
        var memberName = Coercion.FormatAsString(Eval(m.MemberNameExpression));
        return EvalMemberCore(receiver, memberName, m.IsStatic, m.IsInvocation, m.Arguments, m.Location);
    }

    private object? EvalMemberCore(
        object? receiver,
        string memberName,
        bool isStatic,
        bool isInvocation,
        IReadOnlyList<ExpressionAst>? arguments,
        SourceLocation location)
    {
        // Static dispatch: receiver is a Type, RuntimeClass, or RuntimeEnum.
        if (isStatic)
        {
            if (receiver is RuntimeClass cls)
            {
                if (isInvocation && memberName.Equals("new", StringComparison.OrdinalIgnoreCase))
                {
                    var args = arguments!.Select(Eval).ToList();
                    return ConstructInstance(cls, args, location);
                }
                throw new PwshRuntimeException(
                    $"Static '{memberName}' is not supported on user-defined class [{cls.Name}] in Phase 3.",
                    location);
            }
            if (receiver is RuntimeEnum en)
            {
                if (isInvocation)
                    throw new PwshRuntimeException($"Cannot invoke '{memberName}' on enum [{en.Name}].", location);
                var v = en.FromName(memberName)
                    ?? throw new PwshRuntimeException($"Enum [{en.Name}] has no member '{memberName}'.", location);
                return v;
            }
            if (receiver is not Type t)
                throw new PwshRuntimeException("Left side of '::' is not a type.", location);
            if (isInvocation)
            {
                var args = arguments!.Select(Eval).ToArray();
                return Types.InvokeStaticMethod(t, memberName, args, location);
            }
            return Types.GetStaticMember(t, memberName, location);
        }

        if (receiver == null)
            throw new PwshRuntimeException("You cannot call a method on a null-valued expression.", location);

        // User-defined class instance?
        if (receiver is RuntimeInstance inst)
        {
            if (isInvocation)
            {
                if (!inst.Class.Methods.TryGetValue(memberName, out var method))
                    throw new PwshRuntimeException(
                        $"Method '{memberName}' not found on class [{inst.Class.Name}].", location);
                var args = arguments!.Select(Eval).ToList();
                return InvokeClassMethod(inst, method, args);
            }
            if (inst.Fields.TryGetValue(memberName, out var fieldValue)) return fieldValue;
            // Missing field: return null (PowerShell convention).
            return null;
        }

        if (isInvocation)
        {
            var args = arguments!.Select(Eval).ToArray();
            if (receiver is ConstructorInvoker ctor)
                return Types.InvokeStaticMethod(ctor.Type, "new", args, location);
            return Types.InvokeInstanceMethod(receiver, memberName, args, location);
        }

        // PowerShell-flavored synthetic members. Real pwsh projects `Count` and `Length`
        // onto any value — scalars answer 1, arrays answer their element count, strings
        // answer their character count (already a native .Length). Real pwsh also makes
        // `Count` an alias for `Length` on arrays. Handle those before falling through to
        // reflected-member lookup so `.Count` on `object[]` doesn't throw. For BCL
        // collections (HashSet<T>, Dictionary<K,V>, List<T>) prefer the real instance
        // `.Count` via reflection so we report the actual population rather than the
        // scalar=1 fallback.
        if (memberName.Equals("Count", StringComparison.OrdinalIgnoreCase)
            || memberName.Equals("Length", StringComparison.OrdinalIgnoreCase))
        {
            if (receiver is Array a) return a.Length;
            if (receiver is System.Collections.ICollection col) return col.Count;
            if (receiver is string s) return s.Length;
            // Types that don't implement non-generic ICollection (most generic collections —
            // HashSet<T>, Dictionary<K,V>) still expose a real `Count` or `Length`
            // instance property. Use it when present so the scalar-fallback doesn't
            // mask the real count.
            var real = receiver.GetType().GetProperty(memberName,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.IgnoreCase);
            if (real is not null) return real.GetValue(receiver);
            // Scalar — real pwsh returns 1 for `.Count` on any non-null scalar.
            if (memberName.Equals("Count", StringComparison.OrdinalIgnoreCase)) return 1;
        }

        return Types.GetInstanceMember(receiver, memberName, location);
    }

    internal RuntimeInstance ConstructInstance(RuntimeClass cls, IReadOnlyList<object?> args, Errors.SourceLocation location)
    {
        var instance = new RuntimeInstance(cls);
        // Initialize field defaults.
        foreach (var p in cls.Properties)
        {
            object? def = p.DefaultValue != null ? Eval(p.DefaultValue) : null;
            if (def == null && p.TypeConstraint != null)
            {
                try
                {
                    var t = Types.ResolveType(p.TypeConstraint.TypeName, p.TypeConstraint.Location);
                    def = Coercion.To(null, t);
                }
                catch { /* leave null */ }
            }
            instance.Fields[p.Name] = def;
        }
        if (cls.Constructor != null)
        {
            RunMethod(instance, cls.Constructor, args);
        }
        else if (args.Count > 0)
        {
            // Allow hashtable-based property init when a single hashtable is passed.
            if (args.Count == 1 && args[0] is System.Collections.IDictionary dict)
            {
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    var key = entry.Key?.ToString() ?? "";
                    if (instance.Fields.ContainsKey(key)) instance.Fields[key] = entry.Value;
                }
            }
            else if (args.Count <= cls.Properties.Count)
            {
                for (int i = 0; i < args.Count; i++)
                {
                    instance.Fields[cls.Properties[i].Name] = args[i];
                }
            }
            else
            {
                throw new PwshRuntimeException(
                    $"Class [{cls.Name}] has no constructor accepting {args.Count} argument(s).", location);
            }
        }
        return instance;
    }

    private object? InvokeClassMethod(RuntimeInstance instance, ClassMethodAst method, IReadOnlyList<object?> args)
    {
        return RunMethod(instance, method, args);
    }

    private object? RunMethod(RuntimeInstance instance, ClassMethodAst method, IReadOnlyList<object?> args)
    {
        using (Scope.Push(ScopeKind.Function))
        {
            Scope.Set(null, "this", instance);
            // Bind parameters positionally.
            int i = 0;
            foreach (var p in method.Parameters)
            {
                object? value = i < args.Count ? args[i] : (p.DefaultValue != null ? Eval(p.DefaultValue) : null);
                if (p.TypeConstraint != null && value != null)
                {
                    try
                    {
                        var t = Types.ResolveType(p.TypeConstraint.TypeName, p.TypeConstraint.Location);
                        value = Coercion.To(value, t);
                    }
                    catch { /* leave */ }
                }
                Scope.Set(null, p.Name, value);
                i++;
            }
            try
            {
                return Evaluate(method.Body);
            }
            catch (PwshReturnException ret)
            {
                return ret.Value;
            }
        }
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
