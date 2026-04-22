using System.Reflection;
using CarbidePwsh.Cmdlets;
using CarbidePwsh.Cmdlets.App;
using CarbidePwsh.Cmdlets.Fs;
using CarbidePwsh.Cmdlets.Json;
using CarbidePwsh.Cmdlets.Output;
using CarbidePwsh.Cmdlets.Shape;
using CarbidePwsh.Cmdlets.Sys;
using CarbidePwsh.Cmdlets.Shell;
using CarbidePwsh.Errors;
using CarbidePwsh.Parser.Ast;
using CarbidePwsh.Runtime;
using CarbideShellCore.Apps;
using CarbideShellCore.Dispatch;
using CarbideShellCore.Env;
using CarbideShellCore.Vfs;
using PwshParser = CarbidePwsh.Parser.Parser;

namespace CarbidePwsh.Host;

public sealed class ShellHost
{
    public Interpreter Interpreter { get; }
    public VirtualFileSystem Vfs { get; }
    public EnvVarStore Env { get; }
    public CmdletRegistry Registry { get; }
    public FunctionRegistry Functions { get; }
    public ClassRegistry Classes { get; }
    public AppRegistry Apps { get; }
    public ShellDispatcher Dispatcher { get; }
    public bool Verbose { get; set; }

    public ShellHost(
        VirtualFileSystem? vfs = null,
        EnvVarStore? env = null,
        AppRegistry? apps = null,
        ShellDispatcher? dispatcher = null)
    {
        bool ownsVfs = vfs is null;
        Vfs = vfs ?? new VirtualFileSystem();
        Env = env ?? new EnvVarStore();
        Apps = apps ?? new AppRegistry();
        Dispatcher = dispatcher ?? new ShellDispatcher();
        if (ownsVfs)
        {
            Vfs.CreateDirectory("/tmp");
            Vfs.CreateDirectory("/home/user");
            Vfs.CurrentLocation = "/home/user";
        }

        Registry = new CmdletRegistry();
        RegisterBuiltinCmdlets(Registry);

        Functions = new FunctionRegistry();
        Classes = new ClassRegistry();

        Interpreter = new Interpreter
        {
            Vfs = Vfs,
            Registry = Registry,
            Functions = Functions,
            Classes = Classes,
            Apps = Apps,
            Dispatcher = Dispatcher,
            Env = Env,
            PipelineOutput = Console.Out,
            PipelineError = Console.Error,
        };
        Interpreter.RunScriptFile = RunScriptFileFromVfs;
        Interpreter.RunApp = RunAppFromVfs;

        Interpreter.Scope.Set("global", "PSVersionTable", BuildVersionTable());
        Interpreter.Scope.Set("global", "HOME", VfsPath.HomePath);
        Interpreter.Scope.Set("global", "?", true);
        Interpreter.Scope.Set("global", "ErrorActionPreference", "Continue");
        Interpreter.Scope.Set("global", "Error", new List<ErrorRecord>());

        Kernel = new PwshKernel(this);
        Dispatcher.Register(Kernel);
        CarbideShellCore.Apps.StubInstaller.Install(Vfs, Dispatcher, Kernel, new[]
        {
            "/usr/bin/pwsh",
            "/usr/bin/pwsh.exe",
            "/usr/bin/powershell",
            "/usr/bin/powershell.exe",
            "/bin/pwsh",
            "/Windows/System32/WindowsPowerShell/v1.0/powershell.exe",
        });
    }

    /// <summary>The pwsh <see cref="CarbideShellCore.Dispatch.IShellKernel"/> exposed to the
    /// shared dispatcher. cmd and bash sessions invoke <c>pwsh</c> / <c>powershell</c>
    /// through this kernel.</summary>
    public PwshKernel Kernel { get; private set; } = null!;

    public string BuildPrompt()
    {
        var pwd = Vfs.CurrentLocation;
        return $"PS {pwd}> ";
    }

    public string ContinuationPrompt() => ">> ";

    public object? Submit(string source)
    {
        Interpreter.PipelineOutput = Console.Out;
        Interpreter.PipelineError = Console.Error;
        var script = PwshParser.ParseString(source);
        try
        {
            return Interpreter.Evaluate(script);
        }
        catch (Exception ex) when (ex is not PwshIncompleteInputException
                                 && ex is not CarbideShellCore.Errors.RequestSubShellException)
        {
            var record = ex is PwshTerminatingException pt ? pt.Error : new ErrorRecord(ex);
            if (Interpreter.Scope.Get("global", "Error") is List<ErrorRecord> errors)
            {
                errors.Insert(0, record);
                if (errors.Count > 256) errors.RemoveRange(256, errors.Count - 256);
            }
            Interpreter.Scope.Set("global", "?", false);
            throw;
        }
    }

    public void SubmitAndRender(string source, TextWriter output)
    {
        var result = Submit(source);
        RenderResult(result, output);
    }

    public static void RenderResult(object? result, TextWriter output)
    {
        if (result == null) return;
        var text = OutputFormatter.Format(result);
        if (text.Length == 0) return;
        output.WriteLine(text);
    }

    public void RenderError(Exception ex, TextWriter errorOutput)
    {
        var message = ex is PwshException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
        errorOutput.Write("\x1b[31merror:\x1b[0m ");
        errorOutput.WriteLine(message);
        if (Verbose) errorOutput.WriteLine(ex.StackTrace);
    }

    // ---------- Script loader ----------

    private object? RunScriptFileFromVfs(string path, bool dotSource, IReadOnlyList<object?> args)
    {
        var abs = Vfs.Normalize(path);
        var file = Vfs.Resolve(abs) as VfsFile
            ?? throw new PwshRuntimeException($"Script '{abs}' not found in the VFS.");
        var source = file.ReadText();
        var script = PwshParser.ParseString(source);
        var stringArgs = args.Select(a => a).ToArray();

        if (dotSource)
        {
            // Evaluate in the caller's scope; `$args` is still set to reflect arguments.
            var savedArgs = Interpreter.Scope.Get(null, "args");
            Interpreter.Scope.Set(null, "args", stringArgs);
            try
            {
                return Interpreter.Evaluate(script);
            }
            finally
            {
                Interpreter.Scope.Set(null, "args", savedArgs);
            }
        }

        using (Interpreter.Scope.Push(ScopeKind.Script))
        {
            Interpreter.Scope.Set(null, "args", stringArgs);
            var parent = VfsPath.SplitLeaf(abs).Parent;
            Interpreter.Scope.Set(null, "PSScriptRoot", parent);
            Interpreter.Scope.Set(null, "PSCommandPath", abs);
            try
            {
                return Interpreter.Evaluate(script);
            }
            catch (PwshReturnException ret)
            {
                return ret.Value;
            }
        }
    }

    // ---------- App invocation ----------

    private int RunAppFromVfs(string path, IReadOnlyList<object?> args)
    {
        var abs = Vfs.Normalize(path);
        var file = Vfs.Resolve(abs) as VfsFile
            ?? throw new PwshRuntimeException($"App '{abs}' not found in the VFS.");
        Assembly asm;
        try { asm = Assembly.Load(file.Content); }
        catch (Exception ex)
        {
            throw new PwshRuntimeException($"Cannot load '{abs}' as a .NET assembly: {ex.Message}", SourceLocation.None, ex);
        }
        var entry = asm.EntryPoint
            ?? throw new PwshRuntimeException($"No entry point in '{abs}'.");

        var stringArgs = args.Select(a => Runtime.Coercion.FormatAsString(a)).ToArray();
        object? result;
        try
        {
            var parameters = entry.GetParameters();
            result = parameters.Length switch
            {
                0 => entry.Invoke(null, null),
                1 when parameters[0].ParameterType == typeof(string[]) => entry.Invoke(null, new object?[] { stringArgs }),
                _ => throw new PwshRuntimeException("Unsupported entry-point signature."),
            };
        }
        catch (TargetInvocationException tie)
        {
            throw new PwshRuntimeException(tie.InnerException?.Message ?? tie.Message, SourceLocation.None, tie.InnerException);
        }
        int code;
        switch (result)
        {
            case int i: code = i; break;
            case Task<int> ti: code = ti.GetAwaiter().GetResult(); break;
            case Task t: t.GetAwaiter().GetResult(); code = 0; break;
            default: code = 0; break;
        }
        Interpreter.Scope.Set("global", "LASTEXITCODE", code);
        return code;
    }

    // ---------- Boot helpers ----------

    private static global::System.Collections.Specialized.OrderedDictionary BuildVersionTable()
    {
        var dict = new global::System.Collections.Specialized.OrderedDictionary();
        dict["PSVersion"] = "7.5-carbide-subset";
        dict["Edition"] = "CarbidePwsh";
        dict["Phase"] = 3;
        return dict;
    }

    private static void RegisterBuiltinCmdlets(CmdletRegistry r)
    {
        // Output.
        r.Register(() => new WriteOutputCommand());
        r.Register(() => new WriteHostCommand());
        r.Register(() => new WriteErrorCommand());
        r.Register(() => new OutStringCommand());
        r.Register(() => new ReadHostCommand());

        // Shape.
        r.Register(() => new WhereObjectCommand());
        r.Register(() => new ForEachObjectCommand());
        r.Register(() => new SelectObjectCommand());
        r.Register(() => new SortObjectCommand());
        r.Register(() => new GroupObjectCommand());
        r.Register(() => new MeasureObjectCommand());

        // JSON.
        r.Register(() => new ConvertToJsonCommand());
        r.Register(() => new ConvertFromJsonCommand());

        // FS.
        r.Register(() => new GetChildItemCommand());
        r.Register(() => new GetContentCommand());
        r.Register(() => new SetContentCommand());
        r.Register(() => new AddContentCommand());
        r.Register(() => new NewItemCommand());
        r.Register(() => new RemoveItemCommand());
        r.Register(() => new TestPathCommand());
        r.Register(() => new SetLocationCommand());
        r.Register(() => new GetLocationCommand());
        r.Register(() => new ResolvePathCommand());
        r.Register(() => new JoinPathCommand());
        r.Register(() => new CopyItemCommand());
        r.Register(() => new MoveItemCommand());

        // System.
        r.Register(() => new ClearHostCommand());
        r.Register(() => new StartSleepCommand());
        r.Register(() => new GetDateCommand());
        r.Register(() => new GetRandomCommand());
        r.Register(() => new NewGuidCommand());
        r.Register(() => new InvokeExpressionCommand());

        // App.
        r.Register(() => new RegisterCarbideAppCommand());
        r.Register(() => new UnregisterCarbideAppCommand());
        r.Register(() => new GetCarbideAppCommand());

        // Cross-shell launchers.
        r.Register(() => new InvokeCmdCommand());
        r.Register(() => new InvokeBashCommand());
    }
}
