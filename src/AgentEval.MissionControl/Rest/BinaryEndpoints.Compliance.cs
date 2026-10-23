// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.MissionControl.Services;
using AgentEval.Output;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace AgentEval.MissionControl.Rest;

internal static partial class BinaryEndpoints
{
    private static void MapCompliancePdfDownload(this WebApplication app) =>
        app.MapGet("/api/v1/compliance/{regulation}/{subjectName}/{ts}/report.pdf", async (
            HttpContext context, string regulation, string subjectName, string ts, string? kind, string? sourceId,
            IOutputStoreReader store, IWorkspaceRunSource source, CancellationToken ct) =>
        {
            if (!FileSystemLayout.IsSafePathSegment(regulation)) return HttpResults.BadRequest("Invalid regulation.");
            if (!FileSystemLayout.IsSafePathSegment(subjectName)) return HttpResults.BadRequest("Invalid subjectName.");
            if (!FileSystemLayout.IsSafePathSegment(ts)) return HttpResults.BadRequest("Invalid timestamp.");
            var normalizedKind = kind?.ToLowerInvariant();
            if (normalizedKind is not (null or "agent" or "workflow")) return HttpResults.BadRequest("Invalid subject kind.");
            try
            {
                var reports = (await new WorkspaceComplianceSource(store, source).ReadAsync(ct)).Where(r =>
                    string.Equals(r.Evidence.Regulation, regulation, StringComparison.OrdinalIgnoreCase)
                    && r.Evidence.Subject.Name == subjectName && r.Timestamp == ts
                    && (normalizedKind is null || r.Evidence.Subject.Kind.ToString().ToLowerInvariant() == normalizedKind)
                    && (sourceId is null || r.SourceId == sourceId)).ToArray();
                if (reports.Length > 1) return HttpResults.Conflict("The report selection is ambiguous; select its kind and sourceId.");
                if (reports.SingleOrDefault()?.Pdf is not { } bytes)
                    return HttpResults.NotFound(new { code = "pdf_not_captured" });
                context.Response.Headers.CacheControl = "no-store";
                return HttpResults.File(bytes.ToArray(), "application/pdf");
            }
            catch (Exception error) when (error is InvalidDataException or IOException or JsonException or UnauthorizedAccessException)
            { return InvalidEvidence(); }
        });
}
