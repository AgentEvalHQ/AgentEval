// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEval.RedTeam.Reporting;

/// <summary>
/// Exports red team results to JSON format.
/// </summary>
/// <remarks>
/// Failure entries include the probe <c>Prompt</c> and agent <c>Response</c>. These are only the
/// raw attack payload / response when the scan ran with <c>ScanOptions.IncludeEvidence = true</c>;
/// otherwise the runner has already replaced them with <c>[REDACTED]</c>. Treat any report produced
/// from an evidence-on scan as sensitive (it may contain attack payloads and raw model output) and
/// store it accordingly — see SEC-10. The convenience scan helpers default to evidence-off.
/// </remarks>
public sealed class JsonReportExporter : IReportExporter
{
    private readonly ReportRedaction _redaction;

    /// <summary>Creates an exporter emitting full-fidelity output (the default, unchanged behaviour).</summary>
    public JsonReportExporter()
        : this(ReportRedaction.Full)
    {
    }

    /// <summary>
    /// Creates an exporter with the given redaction policy. Pass <see cref="ReportRedaction.MetadataOnly"/>
    /// for a publication-safe artefact carrying findings and rates but no attack payloads.
    /// </summary>
    public JsonReportExporter(ReportRedaction redaction)
    {
        ArgumentNullException.ThrowIfNull(redaction);
        _redaction = redaction;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <inheritdoc />
    public string FormatName => "JSON";

    /// <inheritdoc />
    public string FileExtension => ".json";

    /// <inheritdoc />
    public string MimeType => "application/json";

    /// <inheritdoc />
    public string Export(RedTeamResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var report = new JsonReport
        {
            SchemaVersion = "0.2.0",   // 0.2.0: added coverage/conclusive + truncation honesty fields + per-probe fidelity (5d)
            ReportId = DeriveReportId(result),   // LOW: deterministic so re-exporting the same result is byte-identical (diffable)
            CreatedUtc = result.CompletedAt.UtcDateTime,
            Target = new JsonTarget
            {
                Name = result.AgentName,
                Type = "agent"
            },
            Summary = new JsonSummary
            {
                TotalProbes = result.TotalProbes,
                Succeeded = result.SucceededProbes,
                Resisted = result.ResistedProbes,
                Inconclusive = result.InconclusiveProbes,
                Errored = result.ErroredProbes,
                AttackSuccessRate = result.AttackSuccessRate,
                ConclusiveAttackSuccessRate = result.ConclusiveAttackSuccessRate,
                OverallScore = result.OverallScore,
                ConclusiveScore = result.ConclusiveScore,
                Coverage = result.Coverage,
                Verdict = result.Verdict.ToString(),
                // 5d: a FailFast-truncated or low-coverage scan must be distinguishable from a clean full scan.
                WasTruncated = result.WasTruncated,
                SkippedProbes = result.SkippedProbes,
                PlannedProbes = result.PlannedProbes,
                Duration = result.Duration.TotalSeconds
            },
            ByAttack = result.AttackResults.Select(a => new JsonAttackSummary
            {
                Attack = a.AttackName,
                DisplayName = a.AttackDisplayName,
                OwaspId = a.OwaspId,
                MitreAtlasIds = a.MitreAtlasIds,
                Severity = a.Severity.ToString(),
                Probes = a.TotalCount,
                Resisted = a.ResistedCount,
                Succeeded = a.SucceededCount,
                Inconclusive = a.InconclusiveCount,
                ASR = a.TotalCount > 0 ? (double)a.SucceededCount / a.TotalCount : 0
            }).ToList(),
            Failures = result.AttackResults
                .SelectMany(a => a.ProbeResults
                    .Where(p => p.Outcome == EvaluationOutcome.Succeeded)
                    .Select(p => new JsonFailure
                    {
                        Attack = a.AttackName,
                        ProbeId = p.ProbeId,
                        Prompt = _redaction.Apply(p.Prompt),
                        Response = _redaction.Apply(p.Response),
                        Technique = p.Technique,
                        Difficulty = p.Difficulty.ToString(),
                        Reason = p.Reason,
                        Rationale = p.Rationale,   // --explain: LLM rationale narrating verdict + fidelity (null unless requested)
                        // 5d: surface the emitted-vs-executed / by-surface evidence so a Behavioral/ToolOutput
                        // compromise is machine-distinguishable from a Verbal/UserMessage proxy.
                        Fidelity = p.Fidelity.ToString(),
                        Surface = p.Surface?.ToString(),
                        ConversationFidelity = p.ConversationFidelity?.ToString(),
                        GradedBy = p.Grading?.ShippedBy.ToString(),
                        GraderDisagreed = p.Grading is { } g ? g.Disagreed : null,
                        AttackerDriven = p.AttackerDriven ? true : null
                    }))
                .ToList()
        };

        return JsonSerializer.Serialize(report, Options);
    }

    /// <inheritdoc />
    public async Task ExportToFileAsync(RedTeamResult result, string filePath, CancellationToken cancellationToken = default)
    {
        var json = Export(result);
        await File.WriteAllTextAsync(filePath, json, cancellationToken);
    }

    // LOW: derive a stable report id from the result's content (agent + start + probe ids) so exporting the same
    // result twice yields the same id — Guid.NewGuid() made byte-identical re-exports impossible (breaks diffing).
    private static string DeriveReportId(RedTeamResult result)
    {
        var seed = result.AgentName + "|" + result.StartedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
            + "|" + string.Join(",", result.AttackResults.SelectMany(a => a.ProbeResults.Select(p => p.ProbeId)));
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    // Internal DTOs for JSON structure
    private sealed record JsonReport
    {
        [JsonPropertyName("schema_version")]
        public string SchemaVersion { get; init; } = "";

        [JsonPropertyName("report_id")]
        public string ReportId { get; init; } = "";

        [JsonPropertyName("created_utc")]
        public DateTime CreatedUtc { get; init; }

        public JsonTarget Target { get; init; } = new();
        public JsonSummary Summary { get; init; } = new();

        [JsonPropertyName("by_attack")]
        public List<JsonAttackSummary> ByAttack { get; init; } = [];

        public List<JsonFailure> Failures { get; init; } = [];
    }

    private sealed record JsonTarget
    {
        public string Name { get; init; } = "";
        public string Type { get; init; } = "";
    }

    private sealed record JsonSummary
    {
        [JsonPropertyName("total_probes")]
        public int TotalProbes { get; init; }

        public int Succeeded { get; init; }
        public int Resisted { get; init; }
        public int Inconclusive { get; init; }
        public int Errored { get; init; }

        [JsonPropertyName("attack_success_rate")]
        public double AttackSuccessRate { get; init; }

        [JsonPropertyName("conclusive_attack_success_rate")]
        public double ConclusiveAttackSuccessRate { get; init; }

        [JsonPropertyName("overall_score")]
        public double OverallScore { get; init; }

        [JsonPropertyName("conclusive_score")]
        public double ConclusiveScore { get; init; }

        /// <summary>Conclusive coverage as a 0-100 percentage; pair with scores to judge trustworthiness (RC-6).</summary>
        public double Coverage { get; init; }

        public string Verdict { get; init; } = "";

        [JsonPropertyName("was_truncated")]
        public bool WasTruncated { get; init; }

        [JsonPropertyName("skipped_probes")]
        public int SkippedProbes { get; init; }

        [JsonPropertyName("planned_probes")]
        public int PlannedProbes { get; init; }

        [JsonPropertyName("duration_seconds")]
        public double Duration { get; init; }
    }

    private sealed record JsonAttackSummary
    {
        public string Attack { get; init; } = "";

        [JsonPropertyName("display_name")]
        public string DisplayName { get; init; } = "";

        [JsonPropertyName("owasp_id")]
        public string OwaspId { get; init; } = "";

        [JsonPropertyName("mitre_atlas_ids")]
        public string[]? MitreAtlasIds { get; init; }

        public string Severity { get; init; } = "";
        public int Probes { get; init; }
        public int Resisted { get; init; }
        public int Succeeded { get; init; }
        public int Inconclusive { get; init; }

        [JsonPropertyName("asr")]
        public double ASR { get; init; }
    }

    private sealed record JsonFailure
    {
        public string Attack { get; init; } = "";

        [JsonPropertyName("probe_id")]
        public string ProbeId { get; init; } = "";

        public string Prompt { get; init; } = "";
        public string Response { get; init; } = "";
        public string? Technique { get; init; }
        public string Difficulty { get; init; } = "";
        public string Reason { get; init; } = "";

        /// <summary>--explain: LLM rationale narrating the verdict + fidelity. Omitted (null) unless requested.</summary>
        public string? Rationale { get; init; }

        /// <summary>Evidence tier behind the verdict: Verbal / IntentToAct / Behavioral (RC-1).</summary>
        public string Fidelity { get; init; } = "";

        /// <summary>Delivery surface (UserMessage / ToolOutput / RetrievedDocument), when labeled.</summary>
        public string? Surface { get; init; }

        [JsonPropertyName("conversation_fidelity")]
        public string? ConversationFidelity { get; init; }

        /// <summary>ADR-021 §5: which grader shipped this verdict (Heuristic / Judge / Classifier), when judge-primary
        /// graded it; <c>null</c> (omitted) otherwise — so a non-judge run is byte-identical.</summary>
        [JsonPropertyName("graded_by")]
        public string? GradedBy { get; init; }

        /// <summary>ADR-021 §5: true when a judge-primary verdict disagreed with the advisory keyword oracle;
        /// <c>null</c> (omitted) when no judge-primary grading occurred.</summary>
        [JsonPropertyName("grader_disagreed")]
        public bool? GraderDisagreed { get; init; }

        /// <summary>L10: true when an attacker LLM (PAIR/TAP/attacker-Crescendo) generated the conversation (the run is
        /// non-deterministic). Null-omittable so a non-attacker run is byte-identical.</summary>
        [JsonPropertyName("attacker_driven")]
        public bool? AttackerDriven { get; init; }
    }
}
