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
// Until codegen has run, the smoke pages use the untyped graphql-request client
// directly with hand-written response interfaces. Codegen is recommended but
// optional — the SPA builds either way.
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
        fetcher: {
          endpoint: "/graphql",
          fetchParams: {
            headers: {
              "Content-Type": "application/json",
            },
          },
        },
        exposeQueryKeys: true,
        exposeFetcher: true,
        legacyMode: false,
      },
    },
  },
};

export default config;
