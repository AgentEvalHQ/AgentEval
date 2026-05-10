import type { CodegenConfig } from "@graphql-codegen/cli";

// Plan-08 MC1.6.3a: GraphQL Code Generator pipeline.
//
// Run: `npm run codegen` (one-shot) or `npm run codegen:watch`.
// Reads the schema from the running dotnet backend at /graphql; emits typed
// React-Query hooks at src/__generated__/graphql.ts.
//
// Workflow:
//   1. `dotnet run --project ../AgentEval.MissionControl` (in another terminal)
//   2. `npm run codegen`  → produces src/__generated__/graphql.ts
//   3. Import typed hooks: `import { useSubjectsQuery } from "@/__generated__/graphql";`
//
// Until codegen has run, smoke pages use the untyped graphql-request client
// directly with hand-written response interfaces. Codegen is recommended but
// optional — the SPA builds either way.
//
// Fetcher: we reuse the shared `gqlRequest` from `@/lib/graphql-client` so the
// generated hooks share retry/header config with hand-written queries (per the
// Opus review F6 — HardcodedFetch as a separate transport would have created
// two divergent fetch paths).
const config: CodegenConfig = {
  overwrite: true,
  schema: "http://localhost:5000/graphql",
  documents: ["src/**/*.{ts,tsx}", "!src/__generated__/**"],
  generates: {
    "src/__generated__/graphql.ts": {
      plugins: [
        "typescript",
        "typescript-operations",
        "typescript-react-query",
      ],
      config: {
        // Reuse the shared graphql-request client so emitted hooks pick up
        // the same headers / retry config as the hand-written queries.
        fetcher: {
          func: "@/lib/graphql-client#gqlRequest",
          isReactHook: false,
        },
        // Match the runtime react-query major version. The plugin emits v4
        // imports by default; v5 has slightly different hook signatures.
        reactQueryVersion: 5,
        reactQueryImportFrom: "@tanstack/react-query",
        exposeQueryKeys: true,
        exposeFetcher: true,
      },
    },
  },
};

export default config;
