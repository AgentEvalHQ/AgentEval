// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.MissionControl.Services;
using AgentEval.Results.Adapters.Reading;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace AgentEval.MissionControl.Rest;

internal static partial class BinaryEndpoints
{
    private static void MapRunDownloads(this WebApplication app)
    {
        app.MapGet("/api/v1/runs/{runId}/trace", (string runId, string? sourceId,
            IWorkspaceRunSource source, CancellationToken ct) => Download(runId, sourceId, source, ct, run =>
        {
            // Native transcripts have producer-defined shapes. Only the captured
            // StoreV1 trace is the legacy event document understood by this viewer.
            var descriptor = run.Evidence.Evidence.SingleOrDefault(e => e.GetProperty("evidenceId").GetString() == "E-trace");
            if (descriptor.ValueKind == JsonValueKind.Undefined || !WorkspaceArtifactProjector.IsLegacyTrace(run, descriptor)
                || run.Evidence.CapturedContent("E-trace") is not { } bytes)
            {
                var capturedTranscript = run.Evidence.Evidence.Any(e => e.GetProperty("kind").GetString() == "transcript" &&
                    run.Evidence.CapturedContent(e.GetProperty("evidenceId").GetString()!) is not null);
                return HttpResults.NotFound(new { code = capturedTranscript ? "trace_viewer_unsupported" : "trace_not_captured" });
            }
            var json = WorkspaceArtifactProjector.ReadLegacyTrace(run, bytes);
            return HttpResults.File(json.ToArray(), "application/json", "agent-trace.json");
        }));

        app.MapGet("/api/v1/runs/{runId}/reports/{format}", (string runId, string format, string? sourceId,
            IWorkspaceRunSource source, CancellationToken ct) =>
        {
            if (!IsAllowedReportFormat(format))
                return Task.FromResult<IResult>(HttpResults.BadRequest($"Unsupported report format '{format}'. Allowed: {string.Join(", ", AllowedReportFormats)}."));
            return Download(runId, sourceId, source, ct, run =>
            {
                // Prefer the exact spelling. md/markdown are aliases only when
                // that exact file was absent, so two recorded files stay distinct.
                var formats = format switch { "md" => new[] { "md", "markdown" }, "markdown" => ["markdown", "md"], _ => [format] };
                foreach (var candidate in formats)
                {
                    var artifacts = run.Evidence.Evidence.Where(e => WorkspaceArtifactProjector.ReportFormat(e) == candidate).ToArray();
                    if (artifacts.Length > 1) throw new InvalidDataException("The selected report format is ambiguous.");
                    if (artifacts.Length == 0) continue;
                    var artifact = artifacts[0];
                    return run.Evidence.CapturedContent(artifact.GetProperty("evidenceId").GetString()!) is { } bytes
                        ? HttpResults.File(bytes.ToArray(), ContentTypeFor(candidate), candidate is "md" or "markdown" ? null : "report." + candidate)
                        : HttpResults.NotFound();
                }
                return HttpResults.NotFound();
            });
        });

        // Any captured artifact can be downloaded without assuming a native
        // producer used the StoreV1 trace or report shape. Never dereference URIs.
        app.MapGet("/api/v1/runs/{runId}/artifacts/{evidenceId}", (string runId, string evidenceId, string? sourceId,
            IWorkspaceRunSource source, CancellationToken ct) => Download(runId, sourceId, source, ct, run =>
                run.Evidence.CapturedContent(evidenceId) is { } bytes
                    ? HttpResults.File(bytes.ToArray(), "application/octet-stream", "captured-evidence.bin")
                    : HttpResults.NotFound()));
    }

    private static async Task<IResult> Download(string runId, string? sourceId, IWorkspaceRunSource source,
        CancellationToken ct, Func<AefWorkspaceRun, IResult> render)
    {
        // Identifiers are catalog data, never filesystem path segments. In
        // particular ':' is a valid AEF run-ID character on Windows too.
        if (runId.Length is < 1 or > 128 || runId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or ':' or '-')))
            return HttpResults.BadRequest("Invalid runId.");
        IReadOnlyList<AefWorkspaceRun> runs;
        try { runs = await source.ReadAsync(ct); }
        catch (Exception error) when (error is InvalidDataException or IOException or JsonException or UnauthorizedAccessException)
        { return InvalidEvidence(); }
        AefWorkspaceRun? selected;
        try { selected = WorkspaceRunSource.Find(runs, runId, sourceId); }
        catch (InvalidDataException) { return HttpResults.Conflict("The run ID is ambiguous; select its sourceId."); }
        if (selected is null) return HttpResults.NotFound(new { code = "run_not_found" });
        try { return render(selected); }
        catch (Exception error) when (error is InvalidDataException or JsonException)
        { return InvalidEvidence(); }
    }

    private static IResult InvalidEvidence() => HttpResults.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
        title: "Evidence is invalid or unavailable", detail: "The selected evidence could not be verified. No fallback source was used.");
}
