// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MissionControl.GraphQL;

var builder = WebApplication.CreateBuilder(args);

// ─── GraphQL (Hot Chocolate 14, ChilliCream — primary read surface) ─────────
//
// Plan-07 §3 Challenge 1: hybrid REST + GraphQL split.
//   Reads (dashboard, compliance matrix, recursive EvalResult tree, evaluator
//   registry) → GraphQL. Writes / binary streams / version → REST below.
// Plan-08 MC1.4.0: this is the baseline scaffolding. Resolvers (MC1.4.1-7)
// land in subsequent commits.
builder.Services
    .AddGraphQLServer()
    .AddQueryType<Query>()
    // Hot Chocolate's default execution-depth limit is 8 in our config — guards
    // against unbounded recursion attacks on the EvalResult tree. Plan-07 §8.1.
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

app.Run();

// Expose Program to WebApplicationFactory<Program> in AgentEval.Tests.
// Required for end-to-end smoke tests of the GraphQL endpoint.
public partial class Program { }
