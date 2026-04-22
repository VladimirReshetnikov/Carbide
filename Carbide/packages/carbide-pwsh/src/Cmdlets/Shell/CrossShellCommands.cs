using CarbidePwsh.Errors;
using CarbidePwsh.Runtime;
using CarbideShellCore.Apps;
using CarbideShellCore.Dispatch;
using CarbideShellCore.Env;

namespace CarbidePwsh.Cmdlets.Shell;

/// <summary>
/// <c>Invoke-Cmd</c> — run a cmd.exe subset command from inside pwsh. Accepts either a
/// <c>-Command</c> inline string or a <c>-File</c> VFS path, with optional positional
/// arguments forwarded as <c>%1</c>-<c>%9</c>.
/// <para>
/// Example: <c>Invoke-Cmd -Command 'ECHO hello &amp;&amp; DIR /b'</c>.
/// </para>
/// </summary>
public sealed class InvokeCmdCommand : Cmdlet
{
    public override string Name => "Invoke-Cmd";
    public override IEnumerable<string> Aliases => new[] { "icmd", "cmd" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var (kernel, dispatcher) = RequireDispatcher(context, "cmd")
            ?? throw new PwshRuntimeException("Invoke-Cmd: the session has no cmd kernel registered.");
        var inlineSource = binding.GetValue<string>("Command", 0, null);
        var file = binding.GetOrDefault<string>("File", null);
        var args = binding.Positional.Skip(string.IsNullOrEmpty(file) && string.IsNullOrEmpty(inlineSource) ? 0 : 1)
            .Select(static v => Coercion.FormatAsString(v)).ToList();

        var (stdin, stdout, stderr) = StreamsFor(input, context);
        var ctx = BuildContext(context, args, stdin, stdout, stderr);

        if (!string.IsNullOrEmpty(inlineSource))
        {
            var code = dispatcher.ExecuteInline(kernel, inlineSource!, ctx);
            context.Interpreter.Scope.Set("global", "LASTEXITCODE", code);
        }
        else if (!string.IsNullOrEmpty(file))
        {
            var abs = context.Vfs.Normalize(file!);
            var code = dispatcher.ExecuteScript(abs, kernel, ctx);
            context.Interpreter.Scope.Set("global", "LASTEXITCODE", code);
        }
        else
        {
            throw new PwshRuntimeException("Invoke-Cmd requires -Command or -File.");
        }
        yield break;
    }

    internal static (IShellKernel Kernel, ShellDispatcher Dispatcher)? RequireDispatcher(CmdletContext context, string shellName)
    {
        var dispatcher = context.Interpreter.Dispatcher;
        if (dispatcher is null) return null;
        if (!dispatcher.TryResolveShellByName(shellName, out var kernel) || kernel is null) return null;
        return (kernel, dispatcher);
    }

    internal static (TextReader Stdin, TextWriter Stdout, TextWriter Stderr) StreamsFor(IEnumerable<object?>? input, CmdletContext context)
    {
        // If upstream produced input, forward it as stdin. Otherwise pass a null reader.
        var stdout = context.Output;
        var stderr = context.Error;
        if (input is null) return (TextReader.Null, stdout, stderr);
        var materialized = string.Join('\n', input.Select(static v => Coercion.FormatAsString(v)));
        return (new StringReader(materialized), stdout, stderr);
    }

    internal static ShellExecutionContext BuildContext(
        CmdletContext context,
        IReadOnlyList<string> args,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr)
    {
        var dispatcher = context.Interpreter.Dispatcher
            ?? throw new PwshRuntimeException("Dispatcher not wired up.");
        return new ShellExecutionContext
        {
            Args = args,
            Input = stdin,
            Output = stdout,
            Error = stderr,
            Vfs = context.Vfs,
            Env = context.Interpreter.Env ?? new EnvVarStore(),
            Apps = context.Interpreter.Apps ?? new AppRegistry(),
            Dispatcher = dispatcher,
        };
    }
}

/// <summary>
/// <c>Invoke-Bash</c> — run a bash subset command from inside pwsh. Accepts either a
/// <c>-Command</c> inline string or a <c>-File</c> VFS path.
/// </summary>
public sealed class InvokeBashCommand : Cmdlet
{
    public override string Name => "Invoke-Bash";
    public override IEnumerable<string> Aliases => new[] { "ibash", "bash" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var (kernel, dispatcher) = InvokeCmdCommand.RequireDispatcher(context, "bash")
            ?? throw new PwshRuntimeException("Invoke-Bash: the session has no bash kernel registered.");
        var inlineSource = binding.GetValue<string>("Command", 0, null);
        var file = binding.GetOrDefault<string>("File", null);
        var args = binding.Positional.Skip(string.IsNullOrEmpty(file) && string.IsNullOrEmpty(inlineSource) ? 0 : 1)
            .Select(static v => Coercion.FormatAsString(v)).ToList();

        var (stdin, stdout, stderr) = InvokeCmdCommand.StreamsFor(input, context);
        var ctx = InvokeCmdCommand.BuildContext(context, args, stdin, stdout, stderr);

        if (!string.IsNullOrEmpty(inlineSource))
        {
            var code = dispatcher.ExecuteInline(kernel, inlineSource!, ctx);
            context.Interpreter.Scope.Set("global", "LASTEXITCODE", code);
        }
        else if (!string.IsNullOrEmpty(file))
        {
            var abs = context.Vfs.Normalize(file!);
            var code = dispatcher.ExecuteScript(abs, kernel, ctx);
            context.Interpreter.Scope.Set("global", "LASTEXITCODE", code);
        }
        else
        {
            throw new PwshRuntimeException("Invoke-Bash requires -Command or -File.");
        }
        yield break;
    }
}
