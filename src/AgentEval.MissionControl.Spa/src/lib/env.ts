// Plan-08 MC1.6.3 (Opus review F14): environment helper.
//
// Single source of truth for the API base path. In dev (`npm run dev`), the
// SPA runs on :5173 and Vite proxies /graphql + /api/v1 to the dotnet backend
// on :5000 (see vite.config.ts). In production, the SPA is served from the
// same origin as the API (single-binary deployment via MapStaticAssets per
// plan-08 MC1.8.1) so the relative path Just Works.
//
// `VITE_API_BASE` overrides this when the SPA is deployed to a different
// origin than the API (e.g. staging / preview environments). Defaults to ""
// which means "same origin as the SPA".

export const env = {
  apiBase: import.meta.env.VITE_API_BASE ?? "",
  graphqlPath: "/graphql",
  restPath: "/api/v1",
} as const;

/** Builds an absolute URL for a REST endpoint. */
export function restUrl(suffix: string): string {
  return env.apiBase + env.restPath + suffix;
}

/**
 * GraphQL endpoint URL (for `new GraphQLClient(url)`), always ABSOLUTE.
 *
 * graphql-request 7 builds its request with `new URL(url)` and no base, so a relative "/graphql" throws
 * "Invalid URL" in the browser and every query fails: the portal rendered blank for any user. Resolving
 * against the page's origin keeps the same-origin default (and an absolute `VITE_API_BASE` still wins,
 * because `new URL(absolute, base)` ignores the base).
 */
export const graphqlUrl = new URL(env.apiBase + env.graphqlPath, globalThis.location?.origin ?? "http://localhost").toString();
