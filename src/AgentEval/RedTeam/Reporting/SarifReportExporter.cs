// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEval.RedTeam.Reporting;

/// <summary>
/// Exports red team results to SARIF format for integration with security tools.
/// SARIF Spec: https://sarifweb.azurewebsites.net/
/// </summary>
public sealed class SarifReportExporter : IReportExporter
{
    private const string SarifVersion = "2.1.0";
    private const string ToolName = "AgentEval RedTeam";
    private const string ToolVersion = "0.2.0";
    private const string ToolUri = "https://github.com/microsoft/agenteval";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <inheritdoc />
    public string FormatName => "SARIF";

    /// <inheritdoc />
    public string FileExtension => ".sarif";

    /// <inheritdoc />
    public string MimeType => "application/sarif+json";

    /// <inheritdoc />
    public string Export(RedTeamResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var sarif = new SarifLog
        {
            Version = SarifVersion,
            Schema = "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json",
            Runs =
            [
                new SarifRun
                {
                    Tool = new SarifTool
                    {
                        Driver = new SarifDriver
                        {
                            Name = ToolName,
                            Version = ToolVersion,
                            InformationUri = ToolUri,
                            Rules = GetRules(result)
                        }
                    },
                    Results = GetResults(result),
                    Invocations =
                    [
                        new SarifInvocation
                        {
                            ExecutionSuccessful = true,
                            StartTimeUtc = result.StartedAt.UtcDateTime,
                            EndTimeUtc = result.CompletedAt.UtcDateTime
                        }
                    ]
                }
            ]
        };

        return JsonSerializer.Serialize(sarif, Options);
    }

    /// <inheritdoc />
    public async Task ExportToFileAsync(RedTeamResult result, string filePath, CancellationToken cancellationToken = default)
    {
        var sarif = Export(result);
        await File.WriteAllTextAsync(filePath, sarif, cancellationToken);
    }

    private static List<SarifRule> GetRules(RedTeamResult result)
    {
        return result.AttackResults.Select(a => new SarifRule
        {
            Id = a.AttackName,
            Name = a.AttackDisplayName,
            ShortDescription = new SarifMessage { Text = $"{a.AttackDisplayName} - {a.OwaspId}" },
            FullDescription = new SarifMessage { Text = $"Tests for {a.AttackDisplayName} vulnerabilities (OWASP {a.OwaspId})" },
            HelpUri = $"https://owasp.org/www-project-top-10-for-large-language-model-applications/",
            DefaultConfiguration = new SarifConfiguration
            {
                Level = SeverityToLevel(a.Severity)
            },
            Properties = new SarifRuleProperties
            {
                OwaspId = a.OwaspId,
                MitreAtlasIds = a.MitreAtlasIds,
                Severity = a.Severity.ToString()
            }
        }).ToList();
    }

    private static List<SarifResult> GetResults(RedTeamResult result)
    {
        var results = new List<SarifResult>();

        foreach (var attack in result.AttackResults)
        {
            foreach (var probe in attack.ProbeResults.Where(p => p.Outcome == EvaluationOutcome.Succeeded))
            {
                results.Add(new SarifResult
                {
                    RuleId = attack.AttackName,
                    RuleIndex = result.AttackResults.ToList().IndexOf(attack),
                    Level = SeverityToLevel(attack.Severity),
                    Message = new SarifMessage
                    {
                        Text = $"[{probe.ProbeId}] {probe.Reason}"
                    },
                    Locations =
                    [
                        new SarifLocation
                        {
                            PhysicalLocation = new SarifPhysicalLocation
                            {
                                ArtifactLocation = new SarifArtifactLocation
                                {
                                    Uri = $"agent://{result.AgentName}",
                                    Description = new SarifMessage { Text = $"Agent: {result.AgentName}" }
                                }
                            }
                        }
                    ],
                    PartialFingerprints = new Dictionary<string, string>
                    {
                        ["probeId"] = probe.ProbeId,
                        ["technique"] = probe.Technique ?? "unknown"
                    },
                    Properties = new SarifResultProperties
                    {
                        Prompt = probe.Prompt,
                        Response = probe.Response,
                        Technique = probe.Technique,
                        Difficulty = probe.Difficulty.ToString()
                    }
                });
            }
        }

        return results;
    }

    private static string SeverityToLevel(Severity severity) => severity switch
    {
        Severity.Critical => "error",
        Severity.High => "error",
        Severity.Medium => "warning",
        Severity.Low => "note",
        Severity.Informational => "none",
        _ => "warning"
    };

    // SARIF DTOs
    private sealed record SarifLog
    {
        [JsonPropertyName("$schema")]
        public string Schema { get; init; } = "";
        public string Version { get; init; } = "";
        public List<SarifRun> Runs { get; init; } = [];
    }

    private sealed record SarifRun
    {
        public SarifTool Tool { get; init; } = new();
        public List<SarifResult> Results { get; init; } = [];
        public List<SarifInvocation> Invocations { get; init; } = [];
    }

    private sealed record SarifTool
    {
        public SarifDriver Driver { get; init; } = new();
    }

    private sealed record SarifDriver
    {
        public string Name { get; init; } = "";
        public string Version { get; init; } = "";
        public string? InformationUri { get; init; }
        public List<SarifRule> Rules { get; init; } = [];
    }

    private sealed record SarifRule
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public SarifMessage? ShortDescription { get; init; }
        public SarifMessage? FullDescription { get; init; }
        public string? HelpUri { get; init; }
        public SarifConfiguration? DefaultConfiguration { get; init; }
        public SarifRuleProperties? Properties { get; init; }
    }

    private sealed record SarifRuleProperties
    {
        public string? OwaspId { get; init; }
        public string[]? MitreAtlasIds { get; init; }
        public string? Severity { get; init; }
    }

    private sealed record SarifConfiguration
    {
        public string Level { get; init; } = "warning";
    }

    private sealed record SarifResult
    {
        public string RuleId { get; init; } = "";
        public int? RuleIndex { get; init; }
        public string Level { get; init; } = "";
        public SarifMessage Message { get; init; } = new();
        public List<SarifLocation>? Locations { get; init; }
        public Dictionary<string, string>? PartialFingerprints { get; init; }
        public SarifResultProperties? Properties { get; init; }
    }

    private sealed record SarifResultProperties
    {
        public string? Prompt { get; init; }
        public string? Response { get; init; }
        public string? Technique { get; init; }
        public string? Difficulty { get; init; }
    }

    private sealed record SarifMessage
    {
        public string Text { get; init; } = "";
    }

    private sealed record SarifLocation
    {
        public SarifPhysicalLocation? PhysicalLocation { get; init; }
    }

    private sealed record SarifPhysicalLocation
    {
        public SarifArtifactLocation? ArtifactLocation { get; init; }
    }

    private sealed record SarifArtifactLocation
    {
        public string Uri { get; init; } = "";
        public SarifMessage? Description { get; init; }
    }

    private sealed record SarifInvocation
    {
        public bool ExecutionSuccessful { get; init; }
        public DateTime StartTimeUtc { get; init; }
        public DateTime EndTimeUtc { get; init; }
    }
}
