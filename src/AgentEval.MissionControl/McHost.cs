// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MissionControl.GraphQL;
using AgentEval.MissionControl.Rest;
using AgentEval.MissionControl.Services;
using AgentEval.Output;

namespace AgentEval.MissionControl;

/// <summary>
/// Hosts the Mission Control web app's service-registration and middleware
/// pipeline. Two callers share this surface:
/// <list type="bullet">
///   <item><c>Program.cs</c> top-level statements (the
///         <c>dotnet run --project src/AgentEval.MissionControl</c> path).</item>
///   <item><c>agenteval mc serve</c> (plan-08 MC1.7.1) — spawns this assembly's
///         executable as a subprocess, so its top-level <c>Program</c> remains
///         the entry point and <c>Microsoft.NET.Sdk.Web</c>'s static-asset pipeline
///         resolves correctly. Both paths call <see cref="ConfigureServices"/>
///         and <see cref="ConfigurePipeline"/>.</item>
/// </list>
/// </summary>
public static class McHost
{
    /// <summary>
    /// Registers Mission Control's services on a
    /// <see cref="WebApplicationBuilder"/>: <see cref="EvaluatorCardRegistry"/>,
    /// <see cref="IOutputStoreReader"/> (FileSystemOutputStore against the
    /// resolved root), <see cref="ComplianceMatrixService"/>, and Hot Chocolate
    /// GraphQL.
    /// </summary>
    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        // Evaluator-card registry: loads all EvaluatorCard JSON files at startup
        // from AgentEval.Evals.Agentic's embedded resources. Drives
        // Query.evaluators(...). Plan-08 MC1.5.3.
        builder.Services.AddSingleton<EvaluatorCardRegistry>();

        // IOutputStoreReader: read-only access to the local .agenteval/ folder.
        // Configured root is `<AgentEval:Root>/.agenteval` if set, else
        // `<cwd>/.agenteval`. Plan-08 MC1.2.2. Tests inject their own reader
        // via WithWebHostBuilder.
        builder.Services.AddSingleton<IOutputStoreReader>(sp =>
        {
            var configuredRoot = builder.Configuration["AgentEval:Root"];
            var root = !string.IsNullOrWhiteSpace(configuredRoot)
                ? configuredRoot
                : Directory.GetCurrentDirectory();
            return new FileSystemOutputStore(System.IO.Path.Combine(root, ".agenteval"));
        });

        // ComplianceMatrixService: builds Query.complianceMatrix from evidence +
        // subjects. Scoped because it depends on IOutputStoreReader. Plan-08 MC1.4.4.
        builder.Services.AddScoped<ComplianceMatrixService>();

        // GraphQL (Hot Chocolate 16, ChilliCream — primary read surface).
        // Plan-07 §3 Challenge 1: hybrid REST + GraphQL split.
        //   Reads (dashboard, compliance matrix, recursive EvalResult tree,
        //   evaluator registry) → GraphQL. Writes / binary streams / version → REST.
        // Plan-08 MC1.4.0/.2/.3/.4/.6 + MC1.5.3 + MC1.6.9: full read surface.
        builder.Services
            .AddGraphQLServer()
            .AddQueryType<Query>()
            // Plan-07 §8.1 + plan-08 MC1.4.6 + Wave 8 raise to 10: each
            // composite-tree level costs 2 query depth (`subResults` + its inner
            // `details`), so the SPA's 3-level drill-down (root → pillar →
            // article → judges) needs depth 9. Depth 10 leaves a unit of headroom
            // while still rejecting attack queries that try to fan out unbounded
            // subtrees (the 12-level deep-query test asserts ~24 depth is rejected).
            //
            // Phase-8 Task 8.8 — Introspection-in-production decision:
            // The default Hot Chocolate configuration leaves GraphQL introspection
            // enabled. In a Phase-1 local-only deployment (loopback bind, no auth)
            // that's intentional: introspection enables the embedded Nitro UI,
            // schema-tooling for SPA development, and `__type` queries that the
            // Phase-5 5.2 regression tests rely on. There is no risk surface
            // because the entire endpoint is loopback-only.
            // Phase 2 of Mission Control (multi-user / auth gates) should:
            //   1. Gate introspection behind an environment flag
            //      (e.g. AGENTEVAL_MC_DISABLE_INTROSPECTION=1).
            //   2. Default to disabled in cloud / multi-tenant deployments.
            //   3. Keep enabled in the local Mode-A path.
            // The 5.2 introspection-based regression tests will need an
            // alternative pin (e.g. schema dump snapshot) in that world.
            .AddMaxExecutionDepthRule(10, skipIntrospectionFields: true)
            .ModifyRequestOptions(opts => opts.ExecutionTimeout = TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Wires up the request pipeline on a built <see cref="WebApplication"/>:
    /// GraphQL endpoint at <c>/graphql</c>, REST <c>/api/v1/*</c>, and the SPA
    /// served from <c>wwwroot/</c> with a fallback to <c>index.html</c> so
    /// client-side react-router routes resolve.
    /// </summary>
    public static void ConfigurePipeline(WebApplication app)
    {
        // Defense-in-depth response headers. Phase 1 has no auth and the bench
        // pipeline lets users write arbitrary text into report.html / report.xml,
        // so cap script execution and MIME-sniffing at the response level.
        // The frame-ancestors directive prevents click-jacking against operators
        // who run the portal next to other local web tools.
        app.Use(async (ctx, next) =>
        {
            var headers = ctx.Response.Headers;
            if (!headers.ContainsKey("X-Content-Type-Options"))
                headers["X-Content-Type-Options"] = "nosniff";
            if (!headers.ContainsKey("X-Frame-Options"))
                headers["X-Frame-Options"] = "DENY";
            if (!headers.ContainsKey("Referrer-Policy"))
                headers["Referrer-Policy"] = "no-referrer";
            if (!headers.ContainsKey("Permissions-Policy"))
                // Defense-in-depth: the portal never uses geolocation, mic, camera,
                // payment, or USB device access. Lock these down at the header level
                // so an operator who follows the Dockerfile LAN-expose example or
                // accidentally proxies the portal through a permissive front-end
                // doesn't get a free attack surface from a future XSS bug.
                headers["Permissions-Policy"] =
                    "geolocation=(), microphone=(), camera=(), payment=(), usb=(), " +
                    "midi=(), magnetometer=(), gyroscope=(), accelerometer=()";
            if (!headers.ContainsKey("Content-Security-Policy"))
                // SPA bundle is self-hosted; Nitro UI loads its own assets — allow
                // 'unsafe-inline' for Nitro's inline styles + scripts (Hot Chocolate
                // 16 ships those without a nonce). Tighten when Nitro grows nonce
                // support or when introspection / Nitro is gated by environment.
                //
                // Phase-8 Task 8.7 — `'unsafe-inline'` carve-out trade-off:
                // The CSP includes `'unsafe-inline'` for script-src + style-src
                // SOLELY to support Hot Chocolate 16's bundled Nitro UI, which
                // ships inline `<script>` / `<style>` blocks WITHOUT nonces or
                // hashes. Tightening to `'strict-dynamic'` or nonce-only would
                // break the embedded /graphql Nitro UI.
                // Risk surface in Phase 1 is low because:
                //   1. The portal is loopback-only (127.0.0.1, no LAN expose).
                //   2. There is no auth or multi-tenancy; no third-party content
                //      reaches the response body.
                //   3. Browser-rendered REPORT outputs (report.html / xml / sarif)
                //      are forced to attachment Content-Disposition (Task 7.13)
                //      so they cannot execute as same-origin script.
                // Phase 2 should tighten when Nitro grows nonce support, OR
                // gate `unsafe-inline` behind a development-only env flag.
                headers["Content-Security-Policy"] =
                    "default-src 'self'; " +
                    "script-src 'self' 'unsafe-inline'; " +
                    "style-src 'self' 'unsafe-inline'; " +
                    "img-src 'self' data:; " +
                    "connect-src 'self'; " +
                    "frame-ancestors 'none'";
            await next();
        });

        // GraphQL endpoint at /graphql. The embedded "Nitro" UI (Hot Chocolate's
        // successor to BananaCakePop) is reachable at /graphql in dev mode.
        app.MapGraphQL("/graphql");

        // REST: minimal binary + version surface (plan-07 §8.2).
        app.MapGet("/api/v1/version", () =>
        {
            return Results.Json(new
            {
                mode = "local",
                agentEvalVersion = typeof(AgentEval.Output.IOutputStoreReader).Assembly
                    .GetName().Version?.ToString() ?? "0.0.0",
                graphqlEndpoint = "/graphql",
            });
        });

        // REST binary + streaming endpoints (plan-07 §8.2; plan-08 MC1.3.2-6).
        // Reports, traces, PDFs are byte streams — REST not GraphQL.
        app.MapBinaryEndpoints();

        // Plan-08 MC1.8.1: serve the React SPA from wwwroot/ so a single
        // `dotnet run --project src/AgentEval.MissionControl` boots the whole
        // portal (GraphQL + REST + UI) on one port.
        //
        // Order matters: UseDefaultFiles must come BEFORE MapStaticAssets, else
        // requests for "/" are 404'd. MapFallbackToFile handles SPA routes
        // (e.g. /subjects/agent/Foo) by serving index.html so client-side
        // react-router takes over.
        app.UseDefaultFiles();
        app.MapStaticAssets();
        app.MapFallbackToFile("index.html");
    }

    // Note: an in-process `RunAsync(port, workspaceRoot)` was prototyped for
    // Wave 8b but pulled because Microsoft.NET.Sdk.Web's static-asset pipeline
    // (MapStaticAssets, MapFallbackToFile) expects the entry assembly to BE
    // the web project. The CLI's `mc serve` therefore spawns the MC executable
    // as a subprocess instead — see McServeCommand.cs in AgentEval.Cli.
}
