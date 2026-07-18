# Sample: counter

**Proposal reference:** [§16.2](../../../Carbide/docs/proposals/carbide-ui-avalonia-integration-proposal__2026-04-18__22-04-08-231875__2bc4122b7f3f.md).

**Ambition tier:** minimum-interactive. Button click handling, mutable state, stack-panel layout. Still no XAML.

**What it demonstrates:**

- Subclassing `StackPanel` to compose a view declaratively.
- Wiring a `Button.Click` handler that mutates view state.
- Reflecting view-state changes through `TextBlock.Text`.

**What it does *not* yet demonstrate:**

- Data binding / `INotifyPropertyChanged` (see `hello-runtime-xaml-axaml-file` once the MVVM sample lands).
- Async work inside event handlers — Mono-WASM browser runs single-threaded; see proposal §12 Q.3.

**Launch contract:**

```ts
const handle = await launchInIframe(build, iframe, { appClass: "CarbideCounter.App" });
```

## License

This sample is licensed under the repository's [Apache License 2.0](../../../LICENSE).
