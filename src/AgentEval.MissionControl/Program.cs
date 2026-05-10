// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MissionControl;
using AgentEval.MissionControl.GraphQL;
using AgentEval.MissionControl.Rest;
using AgentEval.MissionControl.Services;
using AgentEval.Output;

// Plan-08 MC1.7.1: when the CLI invokes `agenteval mc serve`, it shells over
// to McHost.BuildAsync directly — no `dotnet run` needed. The top-level
// statements below are the equivalent path for `dotnet run --project
// src/AgentEval.MissionControl` (still useful for development).
var builder = WebApplication.CreateBuilder(args);

// Configure shared services + middleware via the McHost helper. The same
// configuration is used by `agenteval mc serve` (CLI path).
McHost.ConfigureServices(builder);
var app = builder.Build();
McHost.ConfigurePipeline(app);
app.Run();

// Expose Program to WebApplicationFactory<Program> in AgentEval.Tests.
// Required for end-to-end smoke tests of the GraphQL endpoint.
public partial class Program { }
