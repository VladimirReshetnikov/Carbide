# Carbide.UI samples

Proposal §16 commits to a curated set of sample programs the `@carbide-ui/*` family makes run end-to-end. This directory holds the source of each sample plus a README describing the ambition tier it exercises.

## Ambition tiers (proposal §16)

| Tier | Demonstrates | Sample |
|---|---|---|
| Minimum-viable | pure C# UI, no XAML | [`hello-code-only/`](hello-code-only/) |
| Minimum-interactive | click handling + mutable state | [`counter/`](counter/) |
| XAML-as-literal | `AvaloniaRuntimeXamlLoader.Load(string)` | [`hello-runtime-xaml-string/`](hello-runtime-xaml-string/) |
| XAML-as-document | `project.addSource("*.axaml", xaml)` + companion | (covered structurally by [UI-M4's unit tests](../../Carbide/packages/core/test/node/axaml-companion.test.mjs); a first-class sample lands when UI-M4's MVVM §16.4 fixture is scaffolded) |
| Multi-preview | N iframes driven from one `CarbideSession` | [`../packages/launcher/test/browser/multi-preview.html`](../packages/launcher/test/browser/) (Playwright fixture) |

## How samples are consumed

Each sample is a single `App.cs` (or `App.cs` + `.axaml`) — not a full `.csproj`. Carbide compiles them from strings at session time. The multi-preview fixture fetches each sample's source via its static server and feeds it into `project.addSource`.

To try a sample by hand:

```ts
import { CarbideSession, BrowserHostAdapter } from "@carbide/core";
import { launchInIframe } from "@carbide-ui/launcher";

const session = await CarbideSession.initializeAsync({
    hostAdapter: new BrowserHostAdapter({
        frameworkAssetsBaseUrl: "...",
        sideloadBaseUrl: "/node_modules",
    }),
    sideload: ["@carbide-ui/refs-avalonia"],
});

const project = session.createProject({ assemblyName: "HelloCodeOnly" });
project.addSource("App.cs", /* contents of hello-code-only/App.cs */);

const build = await project.build();
const iframe = document.createElement("iframe");
document.body.append(iframe);
await launchInIframe(build, iframe, { appClass: "HelloCodeOnly.App" });
```

See each sample's own README for the exact `appClass` to pass.
