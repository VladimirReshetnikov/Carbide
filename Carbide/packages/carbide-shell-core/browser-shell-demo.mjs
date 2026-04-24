const SHELL_CORE_SOURCES = [
    "src/Errors/ShellException.cs",
    "src/Vfs/VfsPath.cs",
    "src/Vfs/VfsNode.cs",
    "src/Vfs/VirtualFileSystem.cs",
    "src/Vfs/VfsSnapshot.cs",
    "src/Apps/AppRegistry.cs",
    "src/Apps/StubInstaller.cs",
    "src/Env/EnvVarStore.cs",
    "src/Dispatch/IShellKernel.cs",
    "src/Dispatch/ShellExecutionContext.cs",
    "src/Dispatch/ShellDispatcher.cs",
    "src/Dispatch/VirtualExecutable.cs",
    "src/Io/ShellArgTokenizer.cs",
];

const PWSH_SHARED_SOURCES = [
    "src/Errors/SourceLocation.cs",
    "src/Errors/PwshException.cs",
    "src/Lexer/TokenKind.cs",
    "src/Lexer/Token.cs",
    "src/Lexer/Lexer.cs",
    "src/Parser/Ast/AstNode.cs",
    "src/Parser/Ast/AstNodes.cs",
    "src/Parser/Ast/ControlFlowNodes.cs",
    "src/Parser/Ast/FunctionNodes.cs",
    "src/Parser/Ast/ErrorNodes.cs",
    "src/Parser/Ast/ClassEnumNodes.cs",
    "src/Parser/Parser.cs",
    "src/Runtime/Scope.cs",
    "src/Runtime/Coercion.cs",
    "src/Runtime/Operators.cs",
    "src/Runtime/TypeAliases.cs",
    "src/Runtime/TypeBridge.cs",
    "src/Runtime/Interpreter.cs",
    "src/Runtime/ScriptBlock.cs",
    "src/Runtime/LoopControl.cs",
    "src/Runtime/ErrorRecord.cs",
    "src/Runtime/ScriptFunction.cs",
    "src/Runtime/FunctionRegistry.cs",
    "src/Runtime/RuntimeClass.cs",
    "src/Runtime/RuntimeEnum.cs",
    "src/Runtime/Providers.cs",
    "src/Runtime/WildcardPattern.cs",
    "src/Cmdlets/Cmdlet.cs",
    "src/Cmdlets/ParameterBinding.cs",
    "src/Cmdlets/CmdletRegistry.cs",
    "src/Cmdlets/Pipeline.cs",
    "src/Cmdlets/Discovery/BuiltinCommandCatalog.cs",
    "src/Cmdlets/Discovery/CommandDiscoveryCommands.cs",
    "src/Cmdlets/Discovery/DiscoveryModels.cs",
    "src/Cmdlets/Discovery/SessionStateCommands.cs",
    "src/Cmdlets/Output/WriteOutputCommand.cs",
    "src/Cmdlets/Output/WriteHostCommand.cs",
    "src/Cmdlets/Output/WriteErrorCommand.cs",
    "src/Cmdlets/Output/OutStringCommand.cs",
    "src/Cmdlets/Output/OutHostCommands.cs",
    "src/Cmdlets/Output/ReadHostCommand.cs",
    "src/Cmdlets/Shape/WhereObjectCommand.cs",
    "src/Cmdlets/Shape/ForEachObjectCommand.cs",
    "src/Cmdlets/Shape/SelectObjectCommand.cs",
    "src/Cmdlets/Shape/SortObjectCommand.cs",
    "src/Cmdlets/Shape/GroupObjectCommand.cs",
    "src/Cmdlets/Shape/MeasureObjectCommand.cs",
    "src/Cmdlets/Json/ConvertToJsonCommand.cs",
    "src/Cmdlets/Json/ConvertFromJsonCommand.cs",
    "src/Cmdlets/Fs/GetChildItemCommand.cs",
    "src/Cmdlets/Fs/GetContentCommand.cs",
    "src/Cmdlets/Fs/SetContentCommand.cs",
    "src/Cmdlets/Fs/AddContentCommand.cs",
    "src/Cmdlets/Fs/NewItemCommand.cs",
    "src/Cmdlets/Fs/RemoveItemCommand.cs",
    "src/Cmdlets/Fs/TestPathCommand.cs",
    "src/Cmdlets/Fs/LocationCommands.cs",
    "src/Cmdlets/Fs/ItemCommands.cs",
    "src/Cmdlets/Fs/PathAndNewObject.cs",
    "src/Cmdlets/Fs/CopyMoveCommands.cs",
    "src/Cmdlets/Sys/SystemCommands.cs",
    "src/Cmdlets/Sys/StrictMode.cs",
    "src/Cmdlets/App/AppCommands.cs",
    "src/Cmdlets/Shell/CrossShellCommands.cs",
    "src/Host/Banner.cs",
    "src/Host/OutputFormatter.cs",
    "src/Host/PwshPromptEditor.cs",
    "src/Host/PwshKernel.cs",
    "src/Host/ShellHost.cs",
];

const CMD_SOURCES = [
    "src/Errors/CmdException.cs",
    "src/Lexer/TokenKind.cs",
    "src/Lexer/Token.cs",
    "src/Lexer/Lexer.cs",
    "src/Parser/Ast.cs",
    "src/Parser/Parser.cs",
    "src/Runtime/VarExpander.cs",
    "src/Runtime/VfsTextWriter.cs",
    "src/Runtime/CrossShellLauncher.cs",
    "src/Runtime/Interpreter.cs",
    "src/Builtins/BuiltinRegistry.cs",
    "src/Builtins/IntExpression.cs",
    "src/Builtins/Builtins.cs",
    "src/Host/Banner.cs",
    "src/Host/CmdKernel.cs",
    "src/Host/ShellHost.cs",
];

const BASH_SOURCES = [
    "src/Errors/BashException.cs",
    "src/Lexer/TokenKind.cs",
    "src/Lexer/Token.cs",
    "src/Lexer/BashLexer.cs",
    "src/Parser/Ast.cs",
    "src/Parser/BashParser.cs",
    "src/Runtime/ArithmeticEvaluator.cs",
    "src/Runtime/BraceExpansion.cs",
    "src/Runtime/CrossShellLauncher.cs",
    "src/Runtime/Expansion.cs",
    "src/Runtime/Globbing.cs",
    "src/Runtime/VfsTextWriter.cs",
    "src/Runtime/Interpreter.cs",
    "src/Builtins/BashTest.cs",
    "src/Builtins/BuiltinRegistry.cs",
    "src/Builtins/Builtins.cs",
    "src/Host/Banner.cs",
    "src/Host/BashKernel.cs",
    "src/Host/ShellHost.cs",
];

const MULTISHELL_SHARED_SOURCES = [
    "src/MultishellSession.cs",
    "src/VirtualExecutableCatalog.cs",
    "src/MultishellVirtualExecutableHandler.Core.cs",
    "src/MultishellVirtualExecutableHandler.Basic.cs",
    "src/MultishellVirtualExecutableHandler.Advanced.cs",
];

const SHARPCOMPRESS_DLL_CANDIDATES = [
    "/packages/carbide-pwsh/src/bin/Debug/net10.0/SharpCompress.dll",
    "/packages/carbide-pwsh/src/bin/Release/net10.0/SharpCompress.dll",
    "/packages/carbide-multishell/src/bin/Debug/net10.0/SharpCompress.dll",
    "/packages/carbide-multishell/src/bin/Release/net10.0/SharpCompress.dll",
    "/packages/carbide-multishell-tests/bin/Debug/net10.0/SharpCompress.dll",
];

export function createPwshSingleEndpointManifest() {
    return [
        ["carbide-shell-core", SHELL_CORE_SOURCES],
        ["carbide-pwsh", ["src/Program.cs", ...PWSH_SHARED_SOURCES]],
        ["carbide-cmd", CMD_SOURCES],
        ["carbide-bash", BASH_SOURCES],
        ["carbide-multishell", MULTISHELL_SHARED_SOURCES],
    ];
}

export function createMultishellManifest() {
    return [
        ["carbide-shell-core", SHELL_CORE_SOURCES],
        ["carbide-pwsh", PWSH_SHARED_SOURCES],
        ["carbide-cmd", CMD_SOURCES],
        ["carbide-bash", BASH_SOURCES],
        ["carbide-multishell", [...MULTISHELL_SHARED_SOURCES, "src/Program.cs"]],
    ];
}

export async function loadShellDemoSources(manifest) {
    const fetchOne = async (pkg, rel) => {
        const response = await fetch(`/packages/${pkg}/${rel}`);
        if (!response.ok) {
            throw new Error(`failed to fetch ${pkg}/${rel}: HTTP ${response.status}`);
        }

        return [`${pkg}/${rel.replace(/^src\//u, "")}`, await response.text()];
    };

    const jobs = [];
    for (const [pkg, paths] of manifest) {
        for (const rel of paths) {
            jobs.push(fetchOne(pkg, rel));
        }
    }

    return Promise.all(jobs);
}

export async function attachSharedShellReferences(session, project) {
    const { url, bytes } = await loadSharpCompressReference();
    const handle = session.addReference(bytes, "SharpCompress.dll");
    project.addReference(handle);
    return [{ name: "SharpCompress.dll", url }];
}

async function loadSharpCompressReference() {
    const attempted = [];
    for (const url of SHARPCOMPRESS_DLL_CANDIDATES) {
        attempted.push(url);
        const response = await fetch(url, { cache: "no-store" });
        if (!response.ok) {
            continue;
        }

        const buffer = await response.arrayBuffer();
        if (buffer.byteLength === 0) {
            continue;
        }

        return {
            url,
            bytes: new Uint8Array(buffer),
        };
    }

    throw new Error(
        "Unable to locate SharpCompress.dll for the shared shell executable catalog. " +
            "Tried:\n" +
            attempted.join("\n") +
            "\nBuild C:/Tools2/Tools/src/Carbide/packages/carbide-pwsh/src/CarbidePwsh.csproj " +
            "(or the carbide-multishell project) so the browser host can fetch the dependency.",
    );
}
