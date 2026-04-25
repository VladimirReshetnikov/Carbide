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
    "src/Host/PromptCompletionService.cs",
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
    "src/MultishellVirtualExecutableHandler.Python.cs",
    "src/MultishellVirtualExecutableHandler.Perl.cs",
    "src/MultishellVirtualExecutableHandler.CScript.cs",
    "src/MultishellVirtualExecutableHandler.Dotnet.cs",
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

export function installDotnetFacade(session) {
    const carbide = (globalThis.Carbide ??= {});
    const facade = carbide.DotnetFacade = {
        lastResponse: "",
        takeLastResponse() {
            const response = facade.lastResponse ?? "";
            facade.lastResponse = "";
            return response;
        },
        executeCallback(requestJson, callback) {
            void executeDotnetFacade(session, requestJson)
                .then((response) => {
                    facade.lastResponse = JSON.stringify(response);
                    callback();
                })
                .catch((err) => {
                    facade.lastResponse = JSON.stringify({
                        exitCode: 7,
                        stdOut: "",
                        stdErr: `dotnet: Carbide host bridge failed: ${String(err?.stack ?? err)}\n`,
                    });
                    callback();
                });
        },
    };
}

async function executeDotnetFacade(session, requestJson) {
    const request = JSON.parse(requestJson);
    const vfs = makeVfsView(request);
    const args = request.args ?? [];
    if (args.length === 0) return dotnetHelp();

    const first = args[0].toLowerCase();
    if (first === "build") return dotnetBuild(session, vfs, request, args.slice(1));
    if (first === "run") return dotnetRun(session, vfs, request, args.slice(1));
    if (first === "exec") return dotnetExec(session, vfs, request, args.slice(1));
    if (first === "clean") return dotnetClean(vfs, request, args.slice(1));
    if (first === "restore") return dotnetRestore(vfs, request, args.slice(1));
    if (first.endsWith(".dll")) return dotnetExec(session, vfs, request, args);

    return {
        exitCode: 3,
        stdOut: "",
        stdErr: `dotnet: Carbide facade does not support '${args[0]}'.\nRun 'dotnet --help' for supported commands.\n`,
    };
}

function dotnetHelp() {
    return {
        exitCode: 0,
        stdOut:
            "Carbide dotnet facade\n" +
            "usage: dotnet build [project.csproj | source.cs] [-o output]\n" +
            "       dotnet run [--project project.csproj | source.cs] [-- args]\n" +
            "       dotnet exec app.dll [args]\n" +
            "       dotnet app.dll [args]\n" +
            "       dotnet clean [project.csproj | source.cs]\n",
        stdErr: "",
    };
}

async function dotnetBuild(session, vfs, request, rawArgs) {
    const parsed = parseBuildLikeArgs(vfs, request.cwd, rawArgs);
    if (parsed.error) return usageError(parsed.error);
    const configured = configureProject(session, vfs, request.cwd, parsed);
    if (configured.error) return usageError(configured.error);

    const build = await configured.project.build();
    if (!build.success) {
        return {
            exitCode: 1,
            stdOut: "",
            stdErr: renderDiagnostics(build.diagnostics),
        };
    }

    const outDir = normalisePath(parsed.outDir ?? defaultOutputDir(configured.projectDir), request.cwd);
    const dllPath = `${outDir}/${configured.assemblyName}.dll`;
    const pdbPath = `${outDir}/${configured.assemblyName}.pdb`;
    const writeFiles = [];
    if (build.pe) writeFiles.push(binaryWrite(dllPath, build.pe));
    if (build.pdb) writeFiles.push(binaryWrite(pdbPath, build.pdb));
    writeFiles.push(textWrite(`${outDir}/.carbide-dotnet-build.json`, JSON.stringify({
        schemaVersion: 1,
        assemblyName: configured.assemblyName,
        rootAssembly: dllPath,
        pdb: build.pdb ? pdbPath : null,
        targetFramework: "net10.0",
        sources: configured.sourcePaths,
        project: configured.projectPath,
    }, null, 2)));

    return {
        exitCode: 0,
        stdOut: "",
        stdErr: `built ${dllPath}\n`,
        writeFiles,
    };
}

async function dotnetRun(session, vfs, request, rawArgs) {
    const split = splitProgramArgs(rawArgs);
    const parsed = parseBuildLikeArgs(vfs, request.cwd, split.commandArgs);
    if (parsed.error) return usageError(parsed.error);
    const configured = configureProject(session, vfs, request.cwd, parsed);
    if (configured.error) return usageError(configured.error);

    const run = await configured.project.run({
        args: split.programArgs,
        stdin: request.stdin ?? null,
    });
    return {
        exitCode: run.success ? (run.exitCode ?? 0) : (run.exitCode ?? 1),
        stdOut: run.stdOut ?? "",
        stdErr: run.diagnostics?.length ? renderDiagnostics(run.diagnostics) : (run.stdErr ?? ""),
    };
}

async function dotnetExec(session, vfs, request, rawArgs) {
    if (rawArgs.length === 0) return usageError("dotnet exec requires an assembly path.");
    const assemblyPath = normalisePath(rawArgs[0], request.cwd);
    const pe = vfs.readBytes(assemblyPath);
    if (!pe) return usageError(`assembly '${assemblyPath}' was not found in the VFS.`);
    const dir = dirname(assemblyPath);
    const references = vfs.filesInDirectory(dir)
        .filter((path) => path.toLowerCase().endsWith(".dll") && path !== assemblyPath)
        .map((path) => vfs.readBytes(path))
        .filter(Boolean);
    const run = await session.runAssembly({
        pe,
        references,
        args: rawArgs.slice(1),
        stdin: request.stdin ?? null,
    });
    return {
        exitCode: run.success ? (run.exitCode ?? 0) : (run.exitCode ?? 1),
        stdOut: run.stdOut ?? "",
        stdErr: run.diagnostics?.length ? renderDiagnostics(run.diagnostics) : (run.stdErr ?? ""),
    };
}

function dotnetClean(vfs, request, rawArgs) {
    const parsed = parseBuildLikeArgs(vfs, request.cwd, rawArgs);
    if (parsed.error) return usageError(parsed.error);
    const projectDir = parsed.projectPath ? dirname(parsed.projectPath) : request.cwd;
    const bin = normalisePath("bin", projectDir);
    const obj = normalisePath("obj", projectDir);
    return {
        exitCode: 0,
        stdOut: "",
        stdErr: `cleaned ${bin}\ncleaned ${obj}\n`,
        deletePaths: [bin, obj],
    };
}

function dotnetRestore(vfs, request, rawArgs) {
    const parsed = parseBuildLikeArgs(vfs, request.cwd, rawArgs);
    if (parsed.error) return usageError(parsed.error);
    const project = parsed.projectPath ? vfs.readText(parsed.projectPath) : null;
    if (project && /<PackageReference\b/i.test(project)) {
        return {
            exitCode: 3,
            stdOut: "",
            stdErr: "dotnet restore: PackageReference restore is not wired into the browser facade yet.\n",
        };
    }
    return {
        exitCode: 0,
        stdOut: "All projects are up-to-date for Carbide restore.\n",
        stdErr: "",
    };
}

function parseBuildLikeArgs(vfs, cwd, rawArgs) {
    const result = { sources: [], projectPath: null, outDir: null };
    for (let i = 0; i < rawArgs.length; i++) {
        const arg = rawArgs[i];
        if (arg === "-o" || arg === "--output") {
            if (i + 1 >= rawArgs.length) return { error: `${arg} requires a directory.` };
            result.outDir = rawArgs[++i];
            continue;
        }
        if (arg === "--project" || arg === "-p") {
            if (i + 1 >= rawArgs.length) return { error: `${arg} requires a project path.` };
            result.projectPath = normalisePath(rawArgs[++i], cwd);
            continue;
        }
        if (arg.startsWith("-")) {
            return { error: `unsupported dotnet facade option '${arg}'.` };
        }
        const abs = normalisePath(arg, cwd);
        if (abs.toLowerCase().endsWith(".csproj")) result.projectPath = abs;
        else if (abs.toLowerCase().endsWith(".cs")) result.sources.push(abs);
        else return { error: `unsupported input '${arg}'. Expected .csproj or .cs.` };
    }

    if (!result.projectPath && result.sources.length === 0) {
        const projects = vfs.filesInDirectory(cwd).filter((p) => p.toLowerCase().endsWith(".csproj"));
        if (projects.length > 1) return { error: "multiple project files found; pass --project." };
        if (projects.length === 1) result.projectPath = projects[0];
        else result.sources = vfs.filesInDirectory(cwd).filter((p) => p.toLowerCase().endsWith(".cs"));
    }

    if (!result.projectPath && result.sources.length === 0) {
        return { error: "no project or C# source file was found." };
    }
    return result;
}

function configureProject(session, vfs, cwd, parsed) {
    let assemblyName;
    let projectDir = cwd;
    let projectPath = null;
    let sourcePaths = [];
    let options = {};

    if (parsed.projectPath) {
        projectPath = parsed.projectPath;
        const projectText = vfs.readText(projectPath);
        if (projectText == null) return { error: `project '${projectPath}' was not found in the VFS.` };
        projectDir = dirname(projectPath);
        const props = parseProjectProperties(projectText);
        assemblyName = props.assemblyName || basename(projectPath).replace(/\.csproj$/i, "");
        options = {
            assemblyName,
            rootNamespace: props.rootNamespace || assemblyName,
            nullable: props.nullable === "enable" ? true : null,
            implicitUsings: props.implicitUsings === "disable" ? false : true,
        };
        sourcePaths = parseCompileIncludes(projectText, projectDir)
            .filter((path) => vfs.readText(path) != null);
        if (sourcePaths.length === 0) {
            sourcePaths = vfs.filesUnder(projectDir)
                .filter((path) => path.toLowerCase().endsWith(".cs"))
                .filter((path) => !path.includes("/bin/") && !path.includes("/obj/"));
        }
    } else {
        sourcePaths = parsed.sources;
        assemblyName = basename(sourcePaths[0]).replace(/\.cs$/i, "") || "CarbideApp";
        projectDir = dirname(sourcePaths[0]);
        options = { assemblyName, implicitUsings: true, nullable: true };
    }

    const project = session.createProject(options);
    for (const path of sourcePaths) {
        const text = vfs.readText(path);
        if (text == null) return { error: `source '${path}' was not found in the VFS.` };
        project.addSource(relativePath(projectDir, path), text);
    }

    return { project, assemblyName, projectDir, projectPath, sourcePaths };
}

function splitProgramArgs(args) {
    const marker = args.indexOf("--");
    if (marker < 0) return { commandArgs: args, programArgs: [] };
    return {
        commandArgs: args.slice(0, marker),
        programArgs: args.slice(marker + 1),
    };
}

function parseProjectProperties(text) {
    const get = (name) => {
        const match = text.match(new RegExp(`<${name}>\\s*([^<]+?)\\s*</${name}>`, "i"));
        return match?.[1]?.trim() ?? null;
    };
    return {
        assemblyName: get("AssemblyName"),
        rootNamespace: get("RootNamespace"),
        nullable: get("Nullable")?.toLowerCase() ?? null,
        implicitUsings: get("ImplicitUsings")?.toLowerCase() ?? null,
    };
}

function parseCompileIncludes(text, projectDir) {
    const paths = [];
    const rx = /<Compile\s+Include\s*=\s*"([^"]+)"/gi;
    for (let match; (match = rx.exec(text));) {
        const include = match[1];
        if (include.includes("*")) continue;
        paths.push(normalisePath(include, projectDir));
    }
    return paths;
}

function makeVfsView(request) {
    const entries = new Map();
    for (const file of request.files ?? []) {
        entries.set(normalisePath(file.path, "/"), {
            bytes: base64ToBytes(file.base64 ?? ""),
            encoding: file.encoding ?? "utf-8",
        });
    }

    return {
        readBytes(path) {
            return entries.get(normalisePath(path, request.cwd ?? "/"))?.bytes ?? null;
        },
        readText(path) {
            const entry = entries.get(normalisePath(path, request.cwd ?? "/"));
            return entry ? new TextDecoder("utf-8").decode(entry.bytes) : null;
        },
        filesInDirectory(dir) {
            const prefix = ensureTrailingSlash(normalisePath(dir, request.cwd ?? "/"));
            const result = [];
            for (const path of entries.keys()) {
                if (!path.startsWith(prefix)) continue;
                const rest = path.slice(prefix.length);
                if (!rest.includes("/")) result.push(path);
            }
            return result.sort();
        },
        filesUnder(dir) {
            const prefix = ensureTrailingSlash(normalisePath(dir, request.cwd ?? "/"));
            return [...entries.keys()].filter((path) => path.startsWith(prefix)).sort();
        },
    };
}

function renderDiagnostics(diagnostics = []) {
    return diagnostics.map((d) => {
        const where = d.path ? `${d.path}:${(d.lineStart ?? 0) + 1}:${(d.columnStart ?? 0) + 1}: ` : "";
        return `${where}${d.severity ?? "error"} ${d.id}: ${d.message}`;
    }).join("\n") + (diagnostics.length ? "\n" : "");
}

function usageError(message) {
    return { exitCode: 2, stdOut: "", stdErr: `dotnet: ${message}\n` };
}

function binaryWrite(path, bytes) {
    return { path, base64: bytesToBase64(bytes), encoding: "binary" };
}

function textWrite(path, text) {
    return { path, base64: bytesToBase64(new TextEncoder().encode(text)), encoding: "utf-8" };
}

function defaultOutputDir(projectDir) {
    return `${projectDir}/bin/Debug/net10.0`;
}

function basename(path) {
    const normal = normalisePath(path, "/");
    return normal.slice(normal.lastIndexOf("/") + 1);
}

function dirname(path) {
    const normal = normalisePath(path, "/");
    const idx = normal.lastIndexOf("/");
    return idx <= 0 ? "/" : normal.slice(0, idx);
}

function relativePath(base, path) {
    const root = ensureTrailingSlash(normalisePath(base, "/"));
    const normal = normalisePath(path, "/");
    return normal.startsWith(root) ? normal.slice(root.length) : basename(normal);
}

function ensureTrailingSlash(path) {
    return path.endsWith("/") ? path : `${path}/`;
}

function normalisePath(path, cwd = "/") {
    let value = String(path ?? "");
    value = value.replace(/\\/g, "/");
    value = value.replace(/^[A-Za-z]:/, "");
    if (!value.startsWith("/")) value = `${cwd || "/"}/${value}`;
    const parts = [];
    for (const part of value.split("/")) {
        if (!part || part === ".") continue;
        if (part === "..") parts.pop();
        else parts.push(part);
    }
    return "/" + parts.join("/");
}

function bytesToBase64(bytes) {
    const nodeBuffer = globalThis.Buffer;
    if (typeof nodeBuffer?.from === "function") return nodeBuffer.from(bytes).toString("base64");
    let binary = "";
    const chunkSize = 0x8000;
    for (let i = 0; i < bytes.length; i += chunkSize) {
        binary += String.fromCharCode(...bytes.subarray(i, Math.min(i + chunkSize, bytes.length)));
    }
    return btoa(binary);
}

function base64ToBytes(value) {
    const nodeBuffer = globalThis.Buffer;
    if (typeof nodeBuffer?.from === "function") return new Uint8Array(nodeBuffer.from(value, "base64"));
    const binary = atob(value);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    return bytes;
}
