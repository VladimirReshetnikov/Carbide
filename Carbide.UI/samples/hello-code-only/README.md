# Sample: hello-code-only

**Proposal reference:** [§16.1](../../../Carbide/docs/proposals/carbide-ui-avalonia-integration-proposal__2026-04-18__22-04-08-231875__2bc4122b7f3f.md).

**Ambition tier:** minimum-viable. The smallest thing that proves `@carbide-ui/*` works end-to-end. Pure code; no XAML anywhere.

**What it demonstrates:**

- Authoring an `Avalonia.Application`-derived type in a single C# file.
- Setting a `FluentTheme` at `Initialize()` time.
- Installing a `MainView` via `ISingleViewApplicationLifetime.MainView` at `OnFrameworkInitializationCompleted`.

**What it does *not* yet demonstrate:**

- XAML (runtime string → `hello-runtime-xaml-string`; runtime file → `hello-runtime-xaml-axaml-file` when landed).
- Event handling (`counter` is the smallest interactive sample).
- Compile-time XAML (gated on UI-M7, deferred).

**Launch contract:**

```ts
const handle = await launchInIframe(build, iframe, { appClass: "HelloCodeOnly.App" });
```

## License

This sample is licensed under the repository's [Apache License 2.0](../../../LICENSE).
