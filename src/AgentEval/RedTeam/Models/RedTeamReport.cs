// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEval.RedTeam;

/// <summary>
/// Serializable report format for red-team results.
/// </summary>
public class RedTeamReport
{
    /// <summary>Schema version for forward compatibility.</summary>
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; init; } = "0.1.0";

    /// <summary>Unique identifier for this report.</summary>
    [JsonPropertyName("report_id")]
    public required string ReportId { get; init; }

    /// <summary>When this report was created.</summary>
    [JsonPropertyName("created_utc")]
    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Information about the tested agent.</summary>
    [JsonPropertyName("target")]
    public required TargetInfo Target { get; init; }

    /// <summary>Summary statistics.</summary>
    [JsonPropertyName("summary")]
    public required SummaryInfo Summary { get; init; }

    /// <summary>Results broken down by attack type.</summary>
    [JsonPropertyName("by_attack")]
    public required IReadOnlyList<AttackSummary> ByAttack { get; init; }

    /// <summary>Details of failed probes.</summary>
    [JsonPropertyName("failures")]
    public required IReadOnlyList<FailureDetail> Failures { get; init; }

    /// <summary>Create report from result.</summary>
    public static RedTeamReport FromResult(RedTeamResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new RedTeamReport
        {
            ReportId = Guid.NewGuid().ToString(),
            CreatedUtc = DateTimeOffset.UtcNow,
            Target = new TargetInfo { Name = result.AgentName, Type = "agent" },
            Summary = new SummaryInfo
            {
                TotalProbes = result.TotalProbes,
                Resisted = result.ResistedProbes,
                Succeeded = result.SucceededProbes,
                Inconclusive = result.InconclusiveProbes,
                OverallScore = result.OverallScore,
                AttackSuccessRate = result.AttackSuccessRate,
                Verdict = result.Verdict.ToString().ToUpperInvariant()
            },
            ByAttack = result.AttackResults.Select(a => new AttackSummary
            {
                Attack = a.AttackName,
                DisplayName = a.AttackDisplayName,
                OwaspId = a.OwaspId,
                MitreAtlasIds = a.MitreAtlasIds,
                TotalProbes = a.TotalCount,
                Resisted = a.ProbeResults.Count(p => p.Outcome == EvaluationOutcome.Resisted),
                Succeeded = a.SucceededCount,
                Asr = a.AttackSuccessRate,
                Severity = a.Severity.ToString().ToLowerInvariant()
            }).ToList(),
            Failures = result.AttackResults
                .SelectMany(a => a.ProbeResults
                    .Where(p => p.Outcome == EvaluationOutcome.Succeeded)
                    .Select(p => new FailureDetail
                    {
                        Attack = a.AttackName,
                        ProbeId = p.ProbeId,
                        Technique = p.Technique,
                        Prompt = p.Prompt,
                        Response = p.Response,
                        Reason = p.Reason,
                        MatchedItems = p.MatchedItems
                    }))
                .ToList()
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Serialize to JSON string.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Serialize to JSON and write to file.</summary>
    public async Task WriteToFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await File.WriteAllTextAsync(filePath, ToJson(), cancellationToken);
    }
}

/// <summary>Information about the test target.</summary>
public record TargetInfo
{
    /// <summary>Name of the agent.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Type of target (always "agent" for now).</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }
}

/// <summary>Summary statistics for the scan.</summary>
public record SummaryInfo
{
    /// <summary>Total number of probes executed.</summary>
    [JsonPropertyName("total_probes")]
    public required int TotalProbes { get; init; }

    /// <summary>Number of probes where agent resisted.</summary>
    [JsonPropertyName("resisted")]
    public required int Resisted { get; init; }

    /// <summary>Number of probes where attack succeeded.</summary>
    [JsonPropertyName("succeeded")]
    public required int Succeeded { get; init; }

    /// <summary>Number of inconclusive probes.</summary>
    [JsonPropertyName("inconclusive")]
    public required int Inconclusive { get; init; }

    /// <summary>Overall security score (0-100).</summary>
    [JsonPropertyName("overall_score")]
    public required double OverallScore { get; init; }

    /// <summary>Attack success rate (0.0-1.0).</summary>
    [JsonPropertyName("attack_success_rate")]
    public required double AttackSuccessRate { get; init; }

    /// <summary>Overall verdict string.</summary>
    [JsonPropertyName("verdict")]
    public required string Verdict { get; init; }
}

/// <summary>Summary for a single attack type.</summary>
public record AttackSummary
{
    /// <summary>Internal name of the attack.</summary>
    [JsonPropertyName("attack")]
    public required string Attack { get; init; }

    /// <summary>Display name of the attack.</summary>
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    /// <summary>OWASP LLM Top 10 ID.</summary>
    [JsonPropertyName("owasp_id")]
    public required string OwaspId { get; init; }

    /// <summary>MITRE ATLAS technique IDs.</summary>
    [JsonPropertyName("mitre_atlas_ids")]
    public string[]? MitreAtlasIds { get; init; }

    /// <summary>Total probes for this attack.</summary>
    [JsonPropertyName("total_probes")]
    public required int TotalProbes { get; init; }

    /// <summary>Probes resisted.</summary>
    [JsonPropertyName("resisted")]
    public required int Resisted { get; init; }

    /// <summary>Probes where attack succeeded.</summary>
    [JsonPropertyName("succeeded")]
    public required int Succeeded { get; init; }

    /// <summary>Attack success rate for this attack type.</summary>
    [JsonPropertyName("asr")]
    public required double Asr { get; init; }

    /// <summary>Severity level.</summary>
    [JsonPropertyName("severity")]
    public required string Severity { get; init; }
}

/// <summary>Details of a failed probe (successful attack).</summary>
public record FailureDetail
{
    /// <summary>Name of the attack type.</summary>
    [JsonPropertyName("attack")]
    public required string Attack { get; init; }

    /// <summary>Probe identifier.</summary>
    [JsonPropertyName("probe_id")]
    public required string ProbeId { get; init; }

    /// <summary>Technique used.</summary>
    [JsonPropertyName("technique")]
    public string? Technique { get; init; }

    /// <summary>The prompt sent.</summary>
    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    /// <summary>The agent's response.</summary>
    [JsonPropertyName("response")]
    public required string Response { get; init; }

    /// <summary>Reason for failure.</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    /// <summary>Matched tokens/patterns.</summary>
    [JsonPropertyName("matched_items")]
    public IReadOnlyList<string>? MatchedItems { get; init; }
}
