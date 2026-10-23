// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
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
    private static readonly JsonSerializerOptions s_traceJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static void MapBinaryEndpoints(this WebApplication app)
    {
        app.MapRunDownloads();
        app.MapHistoryDownload();

        // ─── MC1.3.4 — compliance PDF stream ─────────────────────────────────
        app.MapGet("/api/v1/compliance/{regulation}/{subjectName}/{ts}/report.pdf", async (
            string regulation,
            string subjectName,
            string ts,
            IOutputStoreReader store,
            CancellationToken ct) =>
        {
            if (!FileSystemLayout.IsSafePathSegment(regulation)) return HttpResults.BadRequest("Invalid regulation.");
            if (!FileSystemLayout.IsSafePathSegment(subjectName)) return HttpResults.BadRequest("Invalid subjectName.");
            if (!FileSystemLayout.IsSafePathSegment(ts)) return HttpResults.BadRequest("Invalid timestamp.");
            if (string.IsNullOrEmpty(store.WorkspaceRoot)) return HttpResults.NotFound();

            // Compliance evidence on disk is keyed by subject NAME only — the kind
            // doesn't appear in the path. Try both kinds; whichever resolves wins.
            var layout = new FileSystemLayout(store.WorkspaceRoot);
            foreach (var kind in new[] { SubjectKind.Agent, SubjectKind.Workflow })
            {
                var pdfPath = layout.ComplianceReportPdf(regulation, new SubjectIdentity(kind, subjectName), ts);
                if (File.Exists(pdfPath))
                    return HttpResults.File(File.OpenRead(pdfPath), "application/pdf");
            }
            await Task.CompletedTask;  // placate analyser; nothing to await on the not-found path
            return HttpResults.NotFound();
        });

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
