// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Output;

namespace AgentEval.MissionControl.Rest;

/// <summary>
/// REST endpoints that serve binary or large-text streams (trace JSON, reports,
/// compliance PDFs, history JSONL). These cannot live in the GraphQL surface
/// (per plan-07 §3 Challenge 1: GraphQL doesn't do streams cleanly) so they
/// stay REST. Plan-08 MC1.3.2 / MC1.3.3 / MC1.3.4 / MC1.3.5 / MC1.3.6.
/// </summary>
internal static class BinaryEndpoints
{
    private static readonly JsonSerializerOptions s_traceJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static void MapBinaryEndpoints(this WebApplication app)
    {
        // ─── MC1.3.2 — trace stream ──────────────────────────────────────────
        // Returns the agent-trace.json for a given run as application/json.
        // 404 when the run is unknown OR has no trace artifact.
        app.MapGet("/api/v1/runs/{runId}/trace", async (
            string runId,
            IOutputStoreReader store,
            CancellationToken ct) =>
        {
            if (!FileSystemLayout.IsSafePathSegment(runId)) return Results.BadRequest("Invalid runId.");
            if (!store.IsAvailable) return Results.NotFound();
            var trace = await store.GetTraceAsync(runId, ct);
            if (trace is null) return Results.NotFound();

            // Serialize on the fly; trace files are typically <1 MB JSON. For
            // large traces (>5 MB compressed), Phase 2's blob-store-backed
            // S3 streaming will replace this in-memory path.
            var json = JsonSerializer.Serialize(trace, s_traceJson);
            return Results.Content(json, "application/json");
        });

        // ─── MC1.3.3 — report stream (markdown / html / junit / sarif) ───────
        app.MapGet("/api/v1/runs/{runId}/reports/{format}", async (
            string runId,
            string format,
            IOutputStoreReader store,
            CancellationToken ct) =>
        {
            if (!FileSystemLayout.IsSafePathSegment(runId)) return Results.BadRequest("Invalid runId.");
            // `format` is also a path segment (`report.{format}`) — a `..`-laced
            // value would escape the reports dir even though IsAllowedReportFormat
            // already rejects it. Defence-in-depth.
            if (!FileSystemLayout.IsSafePathSegment(format)) return Results.BadRequest("Invalid format.");
            if (!IsAllowedReportFormat(format))
                return Results.BadRequest($"Unsupported report format '{format}'. Allowed: markdown, html, junit, sarif.");

            // Mode A: locate via filesystem path. We need the subject (kind + name)
            // to build the path; resolve via the run manifest first.
            if (string.IsNullOrEmpty(store.WorkspaceRoot))
                return Results.NotFound();

            var manifest = await store.GetRunManifestAsync(runId, ct);
            if (manifest is null) return Results.NotFound();

            var layout = new FileSystemLayout(store.WorkspaceRoot);
            var subject = manifest.Subject.ToIdentity();
            var path = layout.ReportFile(subject, runId, format);

            if (!File.Exists(path)) return Results.NotFound();

            var contentType = ContentTypeFor(format);
            return Results.File(File.OpenRead(path), contentType);
        });

        // ─── MC1.3.4 — compliance PDF stream ─────────────────────────────────
        app.MapGet("/api/v1/compliance/{regulation}/{subjectName}/{ts}/report.pdf", async (
            string regulation,
            string subjectName,
            string ts,
            IOutputStoreReader store,
            CancellationToken ct) =>
        {
            if (!FileSystemLayout.IsSafePathSegment(regulation)) return Results.BadRequest("Invalid regulation.");
            if (!FileSystemLayout.IsSafePathSegment(subjectName)) return Results.BadRequest("Invalid subjectName.");
            if (!FileSystemLayout.IsSafePathSegment(ts)) return Results.BadRequest("Invalid timestamp.");
            if (string.IsNullOrEmpty(store.WorkspaceRoot)) return Results.NotFound();

            // Compliance evidence on disk is keyed by subject NAME only — the kind
            // doesn't appear in the path. Try both kinds; whichever resolves wins.
            var layout = new FileSystemLayout(store.WorkspaceRoot);
            foreach (var kind in new[] { SubjectKind.Agent, SubjectKind.Workflow })
            {
                var pdfPath = layout.ComplianceReportPdf(regulation, new SubjectIdentity(kind, subjectName), ts);
                if (File.Exists(pdfPath))
                    return Results.File(File.OpenRead(pdfPath), "application/pdf");
            }
            await Task.CompletedTask;  // placate analyser; nothing to await on the not-found path
            return Results.NotFound();
        });

        // ─── MC1.3.5 — compliance evidence JSON schema ───────────────────────
        // Returns the JSON schema for a regulation's evidence so a generic SPA can
        // adaptively render unknown regulations. Currently serves only the BASE
        // evidence.schema.json; regulation-specific wrapper schemas (gdpr-evidence,
        // eu-ai-act-evidence) live in sample projects and are deferred until a
        // discovery path is wired up.
        app.MapGet("/api/v1/compliance/{regulation}/schema", (string regulation, CancellationToken ct) =>
        {
            if (!FileSystemLayout.IsSafePathSegment(regulation)) return Results.BadRequest("Invalid regulation.");
            // Discover the embedded schema in DataLoaders.
            var asm = typeof(FileSystemLayout).Assembly;
            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("evidence.schema.json", StringComparison.OrdinalIgnoreCase));
            if (resourceName is null) return Results.NotFound();

            using var stream = asm.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            var schema = reader.ReadToEnd();
            return Results.Content(schema, "application/schema+json");
        });

        // ─── MC1.3.6 — subject history JSONL stream ──────────────────────────
        app.MapGet("/api/v1/subjects/{kind}/{name}/history", async (
            HttpContext httpContext,
            string kind,
            string name,
            IOutputStoreReader store,
            CancellationToken ct) =>
        {
            // Accept "agent"/"workflow" in any case so the URL stays user-friendly.
            if (!Enum.TryParse<SubjectKind>(kind, ignoreCase: true, out var kindEnum))
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsync($"Unknown subject kind '{kind}'.", ct);
                return;
            }
            if (!FileSystemLayout.IsSafePathSegment(name))
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsync("Invalid subject name.", ct);
                return;
            }
            if (!store.IsAvailable)
            {
                httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var subject = new SubjectIdentity(kindEnum, name);
            httpContext.Response.ContentType = "application/x-ndjson";
            httpContext.Response.Headers["Cache-Control"] = "no-store";

            await foreach (var entry in store.GetHistoryAsync(subject, range: null, ct))
            {
                var line = JsonSerializer.Serialize(entry, s_traceJson);
                await httpContext.Response.WriteAsync(line, ct);
                await httpContext.Response.WriteAsync("\n", ct);
            }
        });
    }

    private static bool IsAllowedReportFormat(string format) => format switch
    {
        "markdown" or "md" or "html" or "junit" or "sarif" or "xml" => true,
        _ => false,
    };

    private static string ContentTypeFor(string format) => format switch
    {
        "markdown" or "md" => "text/markdown",
        "html"             => "text/html",
        "junit" or "xml"   => "application/xml",
        "sarif"            => "application/sarif+json",
        _                  => "application/octet-stream",
    };
}
