// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace AgentEval.MissionControl.Rest;

/// <summary>
/// REST endpoints that serve binary or large-text streams (trace JSON, reports,
/// compliance PDFs, history JSONL). These cannot live in the GraphQL surface
/// (per plan-07 §3 Challenge 1: GraphQL doesn't do streams cleanly) so they
/// stay REST. Plan-08 MC1.3.2 / MC1.3.3 / MC1.3.4 / MC1.3.5 / MC1.3.6.
/// </summary>
internal static partial class BinaryEndpoints
{
    public static void MapBinaryEndpoints(this WebApplication app)
    {
        app.MapRunDownloads();
        app.MapHistoryDownload();
        app.MapCompliancePdfDownload();

        // ─── MC1.3.5 — compliance evidence JSON schema ───────────────────────
        // Returns the JSON schema for a regulation's evidence so a generic SPA can
        // adaptively render unknown regulations. Phase-8 T2.7: per-regulation
        // dispatch — gdpr → gdpr-evidence.schema.json; eu-ai-act → eu-ai-act-evidence.schema.json;
        // unknown → 404. The wrapper schemas are embedded into MC via csproj <EmbeddedResource Link>
        // so MC stays decoupled from the compliance projects.
        app.MapGet("/api/v1/compliance/{regulation}/schema", (string regulation, CancellationToken ct) =>
        {
            if (!FileSystemLayout.IsSafePathSegment(regulation)) return HttpResults.BadRequest("Invalid regulation.");

            var resourceName = regulation.ToLowerInvariant() switch
            {
                "gdpr"      => "AgentEval.MissionControl.Schemas.gdpr-evidence.schema.json",
                "eu-ai-act" => "AgentEval.MissionControl.Schemas.eu-ai-act-evidence.schema.json",
                _           => null,
            };

            if (resourceName is null)
                return HttpResults.NotFound($"Unknown regulation: {regulation}. Known regulations: gdpr, eu-ai-act.");

            var asm = typeof(BinaryEndpoints).Assembly;
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream is null) return HttpResults.NotFound($"Schema resource missing for regulation '{regulation}'.");
            using var reader = new StreamReader(stream);
            var schema = reader.ReadToEnd();
            return HttpResults.Content(schema, "application/schema+json");
        });

    }

    // Single source of truth for accepted report formats — the rejection message is derived from
    // this so it can never drift from the allow-list (MNT-13).
    private static readonly string[] AllowedReportFormats = AgentEval.MissionControl.Services.WorkspaceArtifactProjector.ReportFormats;

    private static bool IsAllowedReportFormat(string format) => Array.IndexOf(AllowedReportFormats, format) >= 0;

    private static string ContentTypeFor(string format) => format switch
    {
        "markdown" or "md" => "text/markdown",
        "html"             => "text/html",
        "junit" or "xml"   => "application/xml",
        "sarif"            => "application/sarif+json",
        _                  => "application/octet-stream",
    };
}
