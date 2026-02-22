// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEval.RedTeam.Reporting;

/// <summary>
/// Exports red team results to JSON format.
/// </summary>
public sealed class JsonReportExporter : IReportExporter
{
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
            SchemaVersion = "0.1.0",
            ReportId = Guid.NewGuid().ToString(),
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
                AttackSuccessRate = result.AttackSuccessRate,
                OverallScore = result.OverallScore,
                Verdict = result.Verdict.ToString(),
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
                        Prompt = p.Prompt,
                        Response = p.Response,
                        Technique = p.Technique,
                        Difficulty = p.Difficulty.ToString(),
                        Reason = p.Reason
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

        [JsonPropertyName("attack_success_rate")]
        public double AttackSuccessRate { get; init; }

        [JsonPropertyName("overall_score")]
        public double OverallScore { get; init; }

        public string Verdict { get; init; } = "";

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
    }
}
