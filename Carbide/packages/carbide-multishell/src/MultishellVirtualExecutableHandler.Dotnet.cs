using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Text.Json.Serialization;
using CarbideShellCore.Dispatch;
using CarbideShellCore.Vfs;

#if CARBIDE_PWSH_EMBEDDED_MULTISHELL
namespace CarbidePwsh.SharedMultishell;
#else
namespace CarbideMultishell;
#endif

internal sealed partial class MultishellVirtualExecutableHandler
{
    private static readonly Lazy<JSFunctionBinding> DotnetFacadeExecuteCallbackBinding = new(
        static () => JSFunctionBinding.BindJSFunction(
            "globalThis.Carbide.DotnetFacade.executeCallback",
            null!,
            [
                JSMarshalerType.Void,
                JSMarshalerType.String,
                JSMarshalerType.Action(),
            ]));

    private static readonly JsonSerializerOptions DotnetJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private async ValueTask<int> ExecuteDotnetAsync(
        VirtualExecutableInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (TryHandleDotnetLocalCommand(invocation, out var localCode))
        {
            return localCode;
        }

        var request = new DotnetFacadeRequest
        {
            SchemaVersion = 1,
            Args = invocation.Args.ToArray(),
            Cwd = invocation.Vfs.CurrentLocation,
            Stdin = TryReadNonInteractiveStdin(invocation.Input),
            Files = SnapshotFiles(invocation.Vfs).ToArray(),
        };

        string responseJson;
        try
        {
            responseJson = await InvokeDotnetFacadeAsync(
                JsonSerializer.Serialize(request, DotnetJsonOptions),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            invocation.Error.WriteLine(
                "dotnet: Carbide host compiler bridge is not available or failed before execution.");
            invocation.Error.WriteLine(ex.Message);
            return 7;
        }

        DotnetFacadeResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<DotnetFacadeResponse>(
                responseJson,
                DotnetJsonOptions);
        }
        catch (JsonException ex)
        {
            invocation.Error.WriteLine($"dotnet: invalid host bridge response: {ex.Message}");
            return 7;
        }

        if (response is null)
        {
            invocation.Error.WriteLine("dotnet: host bridge returned an empty response.");
            return 7;
        }

        foreach (var deletePath in response.DeletePaths ?? Array.Empty<string>())
        {
            invocation.Vfs.Delete(deletePath, recursive: true, force: true);
        }

        foreach (var file in response.WriteFiles ?? Array.Empty<DotnetFacadeFileWrite>())
        {
            if (string.IsNullOrWhiteSpace(file.Path)) continue;
            var bytes = string.IsNullOrEmpty(file.Base64)
                ? Array.Empty<byte>()
                : Convert.FromBase64String(file.Base64);
            invocation.Vfs.CreateFile(
                file.Path,
                bytes,
                overwrite: true,
                encoding: file.Encoding ?? "utf-8");
        }

        if (!string.IsNullOrEmpty(response.StdOut))
        {
            invocation.Output.Write(response.StdOut);
        }
        if (!string.IsNullOrEmpty(response.StdErr))
        {
            invocation.Error.Write(response.StdErr);
        }

        return response.ExitCode;
    }

    private static bool TryHandleDotnetLocalCommand(
        VirtualExecutableInvocation invocation,
        out int exitCode)
    {
        exitCode = 0;
        if (invocation.Args.Count == 0
            || invocation.Args.Any(static a => a is "-h" or "--help" or "/?"))
        {
            invocation.Output.WriteLine("Carbide dotnet facade");
            invocation.Output.WriteLine("usage: dotnet [--info|--version|--list-sdks|--list-runtimes]");
            invocation.Output.WriteLine("       dotnet build [project.csproj | source.cs] [-o output]");
            invocation.Output.WriteLine("       dotnet run [--project project.csproj | source.cs] [-- args]");
            invocation.Output.WriteLine("       dotnet exec app.dll [args]");
            invocation.Output.WriteLine("       dotnet app.dll [args]");
            invocation.Output.WriteLine("       dotnet clean [project.csproj | source.cs]");
            invocation.Output.WriteLine();
            invocation.Output.WriteLine("This is a browser/VFS facade over Carbide, not a native SDK process.");
            return true;
        }

        if (invocation.Args.Count == 1
            && invocation.Args[0].Equals("--version", StringComparison.OrdinalIgnoreCase))
        {
            invocation.Output.WriteLine("Carbide dotnet facade 0.1");
            return true;
        }

        if (invocation.Args.Count == 1
            && invocation.Args[0].Equals("--info", StringComparison.OrdinalIgnoreCase))
        {
            invocation.Output.WriteLine("Carbide dotnet facade 0.1");
            invocation.Output.WriteLine(".NET target surface: net10.0");
            invocation.Output.WriteLine("Host: Mono-WASM / Carbide VFS");
            invocation.Output.WriteLine("Process spawning: not supported");
            return true;
        }

        if (invocation.Args.Count == 1
            && invocation.Args[0].Equals("--list-sdks", StringComparison.OrdinalIgnoreCase))
        {
            invocation.Output.WriteLine("10.0.0-carbide [/carbide/sdk]");
            return true;
        }

        if (invocation.Args.Count == 1
            && invocation.Args[0].Equals("--list-runtimes", StringComparison.OrdinalIgnoreCase))
        {
            invocation.Output.WriteLine("Microsoft.NETCore.App 10.0.0-carbide [/carbide/shared/Microsoft.NETCore.App]");
            return true;
        }

        return false;
    }

    private static string? TryReadNonInteractiveStdin(TextReader input)
    {
        if (ReferenceEquals(input, TextReader.Null))
        {
            return null;
        }
        if (input is StringReader)
        {
            return input.ReadToEnd();
        }
        return null;
    }

    private static IEnumerable<DotnetFacadeFileSnapshot> SnapshotFiles(VirtualFileSystem vfs)
    {
        foreach (var node in vfs.List("/", recursive: true, filter: null, filesOnly: true))
        {
            if (node is not VfsFile file) continue;
            if (IsCarbideStub(file)) continue;
            yield return new DotnetFacadeFileSnapshot
            {
                Path = file.AbsolutePath,
                Base64 = Convert.ToBase64String(file.Content),
                Encoding = file.Encoding,
            };
        }
    }

    private static bool IsCarbideStub(VfsFile file)
    {
        if (file.Content.Length == 0) return false;
        if (file.Content.Length > 256) return false;
        try
        {
            return file.ReadText().StartsWith("#!carbide:", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static async ValueTask<string> InvokeDotnetFacadeAsync(
        string requestJson,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<string>();
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<string>)state!).TrySetCanceled(),
            tcs);
        Action callback = () =>
        {
            try
            {
                tcs.TrySetResult(TakeDotnetFacadeResponse());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        };

        // JSFunctionBinding reserves slot 0 for JS exceptions and slot 1 for the result.
        JSMarshalerArgument[] args = new JSMarshalerArgument[4];
        for (int i = 0; i < args.Length; i++)
        {
            args[i].Initialize();
        }
        args[2].ToJS(requestJson);
        args[3].ToJS(callback);
        JSFunctionBinding.InvokeJS(DotnetFacadeExecuteCallbackBinding.Value, args.AsSpan());
        return await tcs.Task.ConfigureAwait(false);
    }

    private static string TakeDotnetFacadeResponse()
    {
        var carbide = JSHost.GlobalThis.GetPropertyAsJSObject("Carbide")
            ?? throw new InvalidOperationException("globalThis.Carbide is not available.");
        var facade = carbide.GetPropertyAsJSObject("DotnetFacade")
            ?? throw new InvalidOperationException("globalThis.Carbide.DotnetFacade is not available.");
        var response = facade.GetPropertyAsString("lastResponse") ?? "";
        facade.SetProperty("lastResponse", "");
        return response;
    }

    private sealed class DotnetFacadeRequest
    {
        public int SchemaVersion { get; set; }
        public string[] Args { get; set; } = [];
        public string Cwd { get; set; } = "/";
        public string? Stdin { get; set; }
        public DotnetFacadeFileSnapshot[] Files { get; set; } = [];
    }

    private sealed class DotnetFacadeFileSnapshot
    {
        public string Path { get; set; } = "";
        public string Base64 { get; set; } = "";
        public string Encoding { get; set; } = "utf-8";
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed class DotnetFacadeResponse
    {
        public int ExitCode { get; set; }
        public string? StdOut { get; set; }
        public string? StdErr { get; set; }
        public string[]? DeletePaths { get; set; }
        public DotnetFacadeFileWrite[]? WriteFiles { get; set; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "Instantiated by System.Text.Json deserialization.")]
    private sealed class DotnetFacadeFileWrite
    {
        public string Path { get; set; } = "";
        public string Base64 { get; set; } = "";
        public string? Encoding { get; set; }
    }
}
