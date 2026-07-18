# @carbide-ui/avalonia-runner

Status: **deferred / consolidated at UI-M3.** The iframe-embeddable runner shell is served directly from [`@carbide-ui/avalonia-runtime-bundle`](../runtime-bundle/README.md) — that package's root contains `index.html`, `main.js`, `runner-bridge.js`, and `_framework/`. The separate `@carbide-ui/avalonia-runner` package was originally scoped as an HTML/JS wrapper over the bundle (plan §7.5) but collapsed into the bundle in UI-M3 after the shell files materialised at the bundle's root during `dotnet publish`.

Point your iframe at `@carbide-ui/avalonia-runtime-bundle/index.html`; the [`@carbide-ui/launcher`](../launcher/README.md) does this by default.

This package name is reserved so a future revision can reintroduce it — e.g. if the bundle gets forked per deployment profile (CDN vs. iframe embed) and one path needs a distinct HTML shell. For UI-M3 it has no files beyond this README and its stub `index.html`.

## License

`@carbide-ui/avalonia-runner` is licensed under [Apache-2.0](LICENSE), with copyright held collectively by Carbide Contributors.
