# Sample: hello-runtime-xaml-string

**Proposal reference:** [§16.3](../../../Carbide/docs/proposals/carbide-ui-avalonia-integration-proposal__2026-04-18__22-04-08-231875__2bc4122b7f3f.md).

**Ambition tier:** XAML-as-literal. `AvaloniaRuntimeXamlLoader.Load` against an inline XAML string; no `.axaml` document, no companion generation.

**What it demonstrates:**

- Authoring XAML as a `const string` inside C# — simplest possible XAML path.
- Populating the current `UserControl` (`this`) as the XAML root.
- Passing `typeof(MainView).Assembly` so `clr-namespace:` references in the XAML resolve against the user assembly.

**When to prefer this over `.axaml`-as-document:**

- When the XAML is small enough to read inline alongside the C# that uses it.
- When targeting editors/IDEs that don't understand Carbide's `.axaml` companion convention.

**When to prefer `.axaml`-as-document (UI-M4):**

- When the view is non-trivial and deserves its own file.
- When a future Carbide M12 migration to compile-time XAML (UI-M7) is on the roadmap — `.axaml` sources are the natural migration target.

**Launch contract:**

```ts
const handle = await launchInIframe(build, iframe, { appClass: "HelloRuntimeXaml.App" });
```
