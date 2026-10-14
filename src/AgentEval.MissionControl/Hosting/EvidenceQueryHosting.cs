// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Indexing;
using AgentEval.MissionControl.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentEval.MissionControl.Hosting;

internal static class EvidenceQueryHosting
{
    internal static void Configure(WebApplicationBuilder builder)
    {
        if (!builder.Configuration.GetValue<bool>("AgentEval:Queries:Enabled"))
        {
            builder.Services.TryAddSingleton<IEvidenceQueryService>(_ => new EvidenceQueryService());
            return;
        }
        var path = builder.Configuration["AgentEval:Queries:IndexPath"];
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("Expected: an absolute AgentEval:Queries:IndexPath. Actual: missing or relative path. Suggestions: configure the derived index explicitly. Because: read queries must not infer storage.");
        var options = new EvidenceIndexOptions { DatabasePath = System.IO.Path.GetFullPath(path) };
        builder.Services.TryAddSingleton<IEvidenceSourceReader>(_ => new SqliteEvidenceSourceReader(options));
        builder.Services.TryAddSingleton<IEvidenceDetailReader>(_ => new SqliteEvidenceDetailReader(options));
        builder.Services.TryAddSingleton<IEvidenceQueryService, EvidenceQueryService>();
    }
}
