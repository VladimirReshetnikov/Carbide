// T1+T2 — public entrypoints for the interactive terminal feature. Consumers typically go
// through `Project.runInteractive`; this module exports the lower-level session helper for
// advanced callers (test harnesses, custom Project subclasses).

export { startInteractiveSession } from "./session.js";
export { installBridge, uninstallBridge, isBridgeInstalled } from "./bridge.js";
export type { TerminalBridgeSink, LineEditorHandle } from "./bridge.js";
export { attachLineEditor } from "./line-editor.js";
export type { LineEditorOptions, LineEditorController } from "./line-editor.js";
