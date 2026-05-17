import { GraphQLClient } from "graphql-request";

// Plan-08 MC1.6.3: GraphQL transport.
//
// Per plan-07 §6.1 + §3 Challenge 1: we use graphql-request (the minimal transport,
// ~10 KB) rather than Apollo Client. TanStack Query handles caching; this client
// is just the wire. Codegen-emitted hooks (MC1.6.3a) wrap this fetcher.
//
// In dev, /graphql is proxied by Vite to the dotnet backend on port 5000
// (see vite.config.ts). In production, the SPA is served from the same origin
// as the API (single binary, MapStaticAssets) so the relative path works.
export const graphqlClient = new GraphQLClient("/graphql", {
  headers: {
    "Content-Type": "application/json",
  },
});

/**
 * Generic GraphQL fetcher — used by codegen-emitted hooks AND by the
 * pre-codegen smoke pages that hand-write queries until `npm run codegen`
 * has produced typed hooks.
 */
export async function gqlRequest<T>(
  query: string,
  variables?: Record<string, unknown>,
): Promise<T> {
  return graphqlClient.request<T>(query, variables);
}
