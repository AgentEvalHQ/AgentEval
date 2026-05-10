import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import path from "node:path";

// Plan-08 MC1.6.1: Vite 6 scaffolding for the Mission Control SPA.
// The dev server proxies /graphql + /api/v1 to the dotnet backend so the SPA
// runs on its own port (5173) while talking to the backend on http://localhost:5000.
//
// Why proxy instead of CORS? We control both ends; the proxy is one config line
// and gives us a same-origin developer experience (no preflight, cookies just work).
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "src"),
    },
  },
  // Plan-08 MC1.8.1: build into the dotnet project's wwwroot/ so a single
  // `dotnet run --project ../AgentEval.MissionControl` serves both the SPA
  // and the GraphQL/REST endpoints from the same port. emptyOutDir=true
  // means stale assets get cleared on each build.
  build: {
    outDir: path.resolve(__dirname, "../AgentEval.MissionControl/wwwroot"),
    emptyOutDir: true,
    chunkSizeWarningLimit: 800,
  },
  server: {
    port: 5173,
    proxy: {
      "/graphql": {
        target: "http://localhost:5000",
        changeOrigin: true,
      },
      "/api/v1": {
        target: "http://localhost:5000",
        changeOrigin: true,
      },
    },
  },
});
