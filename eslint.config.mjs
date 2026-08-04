// Repository-wide ESLint flat config. Correctness rules only — formatting is left to
// .editorconfig and the existing code style; no stylistic rules are enabled here.
//
// Scope: TypeScript sources of the shippable packages. Generated output (dist/), vendored
// material (third-party/), fixtures, and the C#/WASM trees are excluded.
import js from "@eslint/js";
import tseslint from "typescript-eslint";

export default tseslint.config(
    {
        ignores: [
            "**/node_modules/",
            "**/dist/",
            // .NET build output (NOT **/bin/** — that would swallow cli/src/bin/).
            "Carbide/packages/core/src/bin/",
            "**/obj/**",
            "**/third-party/",
            "**/test-results/",
            "**/playwright-report/",
            "**/*.d.ts",
            "Carbide/packages/refs-net10.0/ref/",
            "Carbide.UI/packages/refs-avalonia/ref/",
            "Carbide.UI/packages/runtime-bundle/_framework/",
        ],
    },
    {
        files: [
            "Carbide/packages/core/src/ts/**/*.ts",
            "Carbide/packages/cli/src/**/*.ts",
            "Carbide/packages/msbuild-lite/src/**/*.ts",
            "Carbide/packages/nuget/src/**/*.ts",
            "Carbide.UI/packages/launcher/src/**/*.ts",
        ],
        extends: [js.configs.recommended, ...tseslint.configs.recommended],
        rules: {
            // The codebase deliberately uses empty catch bodies for best-effort cleanup
            // paths, always with an explanatory comment. Allow the pattern.
            "no-empty": ["error", { allowEmptyCatch: true }],
            // Forward-reference declarations (`let p; const f = () => use(p); p = init();`)
            // are used for promise handles read by closures defined before the assignment.
            "prefer-const": ["error", { ignoreReadBeforeAssign: true }],
            // Intentional escape hatches exist at the JS-interop boundary; require them
            // to stay visible rather than silently widening.
            "@typescript-eslint/no-explicit-any": "warn",
            // Unused function args prefixed with _ are the local convention.
            "@typescript-eslint/no-unused-vars": [
                "error",
                { argsIgnorePattern: "^_", varsIgnorePattern: "^_" },
            ],
            // Locale-sensitive string operations produce different results on different
            // machines, because they depend on the host's ICU data. Three separate audits
            // found `localeCompare` deciding real outcomes: which package version wins,
            // the ordering inside `carbide.lock.json`, and the order of a `carbide audit`
            // payload. Each would have let two developers derive different results from
            // identical inputs. Sort and compare ordinally instead — for the ASCII-ish
            // identifiers Carbide handles (package ids, TFMs, paths, SemVer labels) ordinal
            // ordering is both correct and reproducible.
            "no-restricted-syntax": [
                "error",
                {
                    selector: "MemberExpression[property.name='localeCompare']",
                    message:
                        "localeCompare depends on host locale data and is not reproducible. Compare " +
                        "ordinally: `a < b ? -1 : a > b ? 1 : 0`, or the compareOrdinal helper.",
                },
                {
                    selector:
                        "MemberExpression[property.name=/^toLocale(LowerCase|UpperCase|DateString|TimeString|String)$/]",
                    message:
                        "toLocale* depends on host locale data and is not reproducible. Use the " +
                        "non-locale form (toLowerCase / toUpperCase / toISOString).",
                },
            ],
        },
    },
    {
        // Repository tooling: the release and provenance gates run on every PR, so they are
        // held to the same correctness bar as shipped code.
        files: ["scripts/**/*.mjs"],
        extends: [js.configs.recommended],
        languageOptions: {
            ecmaVersion: 2023,
            sourceType: "module",
            globals: {
                console: "readonly",
                process: "readonly",
                URL: "readonly",
                TextDecoder: "readonly",
                TextEncoder: "readonly",
            },
        },
        rules: {
            "no-empty": ["error", { allowEmptyCatch: true }],
        },
    },
);
