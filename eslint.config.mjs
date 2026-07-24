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
        },
    },
);
