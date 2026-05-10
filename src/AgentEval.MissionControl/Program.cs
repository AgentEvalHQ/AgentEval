// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MissionControl.GraphQL;
using AgentEval.MissionControl.Rest;
using AgentEval.MissionControl.Services;
using AgentEval.Output;

var builder = WebApplication.CreateBuilder(args);

// ─── In-memory services ─────────────────────────────────────────────────────

// Evaluator-card registry: loads all EvaluatorCard JSON files at startup from
// AgentEval.Evals.Agentic's embedded resources. Drives Query.evaluators(...).
// Plan-08 MC1.5.3.
builder.Services.AddSingleton<EvaluatorCardRegistry>();

// IOutputStoreReader: read-only access to the local .agenteval/ folder. Mode A.
// Configured root is `<AgentEval:Root>/.agenteval` if set, else `<cwd>/.agenteval`.
// Plan-08 MC1.2.2. Tests inject their own IOutputStoreReader via WithWebHostBuilder.
builder.Services.AddSingleton<IOutputStoreReader>(sp =>
{
    var configuredRoot = builder.Configuration["AgentEval:Root"];
    var root = !string.IsNullOrWhiteSpace(configuredRoot)
        ? configuredRoot
        : Directory.GetCurrentDirectory();
    return new FileSystemOutputStore(System.IO.Path.Combine(root, ".agenteval"));
});

// ComplianceMatrixService: builds Query.complianceMatrix from evidence + subjects.
// Scoped because it depends on IOutputStoreReader. Plan-08 MC1.4.4.
builder.Services.AddScoped<ComplianceMatrixService>();

// ─── GraphQL (Hot Chocolate 16, ChilliCream — primary read surface) ─────────
//
// Plan-07 §3 Challenge 1: hybrid REST + GraphQL split.
//   Reads (dashboard, compliance matrix, recursive EvalResult tree, evaluator
//   registry) → GraphQL. Writes / binary streams / version → REST below.
// Plan-08 MC1.4.0: this is the baseline scaffolding. MC1.5.3 adds the first
// real resolvers (Query.evaluators / Query.evaluator). MC1.4.2-7 follow.
builder.Services
    .AddGraphQLServer()
    .AddQueryType<Query>()
    // Plan-07 §8.1 + plan-08 MC1.4.6: cap recursion at depth 10. Each
    // composite-tree level costs 2 query depth (one each for `subResults` and
    // its inner `details`), so the SPA's 3-level drill-down (root → pillar →
    // article → judges) needs depth 9. Depth 10 leaves a unit of headroom
    // while still rejecting attack queries that try to fan out unbounded
    // subtrees (the 12-level deep-query test asserts ~24 depth is rejected).
    .AddMaxExecutionDepthRule(10, skipIntrospectionFields: true)
    .ModifyRequestOptions(opts => opts.ExecutionTimeout = TimeSpan.FromSeconds(30));

var app = builder.Build();

// GraphQL endpoint at /graphql. The embedded "Nitro" UI (Hot Chocolate's
// successor to BananaCakePop) is reachable at /graphql in dev mode.
app.MapGraphQL("/graphql");

// ─── REST: minimal binary + version surface (plan-07 §8.2) ──────────────────

app.MapGet("/api/v1/version", () =>
{
    return Results.Json(new
    {
        mode = "local",
        agentEvalVersion = typeof(AgentEval.Output.IOutputStoreReader).Assembly
            .GetName().Version?.ToString() ?? "0.0.0",
        graphqlEndpoint = "/graphql",
        // Future plan-08 work: schemaVersions, available features.
    });
});

// REST binary + streaming endpoints (plan-07 §8.2; plan-08 MC1.3.2-6).
// Reports, traces, PDFs are byte streams — REST not GraphQL.
app.MapBinaryEndpoints();

// Plan-08 MC1.8.1: serve the React SPA from wwwroot/ so a single
// `dotnet run --project src/AgentEval.MissionControl` boots the whole portal
// (GraphQL + REST + UI) on one port. The SPA is built via
// `npm run build` in src/AgentEval.MissionControl.Spa/ which writes its
// `dist/` output directly into this project's wwwroot/ (see vite.config.ts).
//
// Order matters: UseDefaultFiles must come BEFORE MapStaticAssets, else
// requests for "/" are 404'd. MapFallbackToFile handles SPA routes
// (e.g. /subjects/agent/Foo) by serving index.html so client-side
// react-router takes over.
app.UseDefaultFiles();
app.MapStaticAssets();
app.MapFallbackToFile("index.html");

app.Run();

// Expose Program to WebApplicationFactory<Program> in AgentEval.Tests.
// Required for end-to-end smoke tests of the GraphQL endpoint.
public partial class Program { }
