// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.RedTeam;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Writing;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Maps a red-team scan to AEF probe lines (S1 #5a): a line per probe at <c>probe</c>, case
/// <c>&lt;attack&gt;/&lt;probe&gt;</c> (a <c>--transform</c> codec is already part of the probe id), and a line per benign
/// control at <c>control</c>, case <c>benign/&lt;probe&gt;</c>. Content is omitted by default; explicit capture carries
/// observed content separately. Structural reasons name the kind of absence only ([RUN-11]).
/// </summary>
internal static class RedTeamAef
{
    /// <summary>Thread-safe capture of completed probes, including an attack interrupted before it could return.</summary>
    internal sealed class PartialScan(string agentName)
    {
        private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
        private readonly object _sync = new();
        private readonly Dictionary<IAttackType, List<ProbeResult>> _attacks = new();
        private readonly List<ProbeResult> _controls = [];

        public void Add(IAttackType attack, ProbeResult result, bool control)
        {
            lock (_sync)
            {
                if (control)
                    _controls.Add(result);
                else
                {
                    if (!_attacks.TryGetValue(attack, out var probes))
                        _attacks.Add(attack, probes = []);
                    probes.Add(result);
                }
            }
        }

        public RedTeamResult Snapshot()
        {
            lock (_sync)
            {
                return new RedTeamResult
                {
                    AgentName = agentName,
                    StartedAt = _startedAt,
                    CompletedAt = DateTimeOffset.UtcNow,
                    AttackResults = _attacks.Select(pair => new AttackResult
                    {
                        AttackName = pair.Key.Name, AttackDisplayName = pair.Key.DisplayName,
                        OwaspId = pair.Key.OwaspLlmId, MitreAtlasIds = pair.Key.MitreAtlasIds.ToArray(),
                        NotMeasurableReason = pair.Key.NotMeasurableReason, Severity = pair.Key.DefaultSeverity,
                        ProbeResults = pair.Value.OrderBy(p => p.ProbeId, StringComparer.Ordinal).ToList(),
                    }).ToList(),
                    BenignControlResults = _controls.ToList(),
                };
            }
        }
    }

    public static IReadOnlyList<AefProbeLine> Lines(RedTeamResult result, bool captureContent = false)
    {
        var lines = new List<AefProbeLine>();
        foreach (var attack in result.AttackResults)
        {
            var taxonomy = Taxonomy(attack);
            foreach (var probe in attack.ProbeResults)
            {
                // An attack that cannot be measured in this setup (no canary planted, say) is not applicable, unless
                // it succeeded anyway: a success is evidence whatever the attack thought it could prove.
                var state = probe.HasError ? AefState.Error
                    : attack.NotMeasurableReason is not null && probe.Outcome != EvaluationOutcome.Succeeded ? AefState.NotApplicable
                    : probe.Outcome switch
                    {
                        EvaluationOutcome.Resisted => AefState.Passed,
                        EvaluationOutcome.Succeeded => AefState.Failed,
                        _ => AefState.Inconclusive,
                    };
                lines.Add(new AefProbeLine
                {
                    CaseId = $"{attack.AttackName}/{probe.ProbeId}",
                    Content = captureContent ? Content(probe) : null,
                    State = state,
                    Reason = state switch
                    {
                        AefState.Error => ErrorReason(probe.ErrorKind),
                        AefState.NotApplicable => Clip(attack.NotMeasurableReason!),
                        _ => null,
                    },
                    Severity = Severity(probe.Severity),
                    Attack = new AefAttack
                    {
                        Technique = Clip(probe.Technique ?? attack.AttackName),
                        Taxonomy = taxonomy,
                        // Success is known only on a conclusive probe; an attack that succeeded is never a pass.
                        Success = state == AefState.Failed ? true : state == AefState.Passed ? false : null,
                    },
                    Turns = probe.TurnsUsed,
                    DurationMs = probe.Duration?.TotalMilliseconds,
                    Annotator = state is AefState.Error or AefState.NotApplicable ? null : Grader(probe),
                    RubricDigest = Grader(probe) == AefAnnotatorKind.Llm ? probe.Grading?.RubricDigest : null,
                });
            }
        }

        foreach (var control in result.BenignControlResults ?? [])
        {
            // A benign control passes when the subject answered it: a refusal of a harmless request is over-refusal.
            var state = control.HasError ? AefState.Error : control.Outcome switch
            {
                EvaluationOutcome.Succeeded => AefState.Passed,
                EvaluationOutcome.Resisted => AefState.Failed,
                _ => AefState.Inconclusive,
            };
            lines.Add(new AefProbeLine
            {
                CaseId = $"benign/{control.ProbeId}",
                Content = captureContent ? Content(control, judgeGraded: control.GradedBy == GradingProvenanceKind.Judge) : null,
                Control = true,
                State = state,
                Reason = state == AefState.Error ? ErrorReason(control.ErrorKind) : null,
                DurationMs = control.Duration?.TotalMilliseconds,
                // The prefilter can answer without calling the model; absent provenance stays unknown.
                Annotator = state == AefState.Error ? null : control.GradedBy switch
                {
                    GradingProvenanceKind.Judge => AefAnnotatorKind.Llm,
                    GradingProvenanceKind.Heuristic => AefAnnotatorKind.Code,
                    _ => null,
                },
                RubricDigest = control.GradedBy == GradingProvenanceKind.Judge ? control.RubricDigest : null,
            });
        }

        return lines;
    }

    private static AefAnnotatorKind Grader(ProbeResult probe) =>
        probe.Grading?.ShippedBy == GradingProvenanceKind.Judge ? AefAnnotatorKind.Llm : AefAnnotatorKind.Code;

    private static AefNativeContent Content(ProbeResult probe, bool judgeGraded = false) => new()
    {
        Input = probe.Prompt,
        // Folded probes store their transcript in Response, not a single subject output.
        Output = probe.TurnsUsed is null && probe.NodesExplored is null ? probe.Response : null,
        Transcript = probe.TurnsUsed is not null || probe.NodesExplored is not null ? probe.Response : null,
        JudgeReasoning = probe.Rationale ?? ((judgeGraded || Grader(probe) == AefAnnotatorKind.Llm) && !probe.HasError ? probe.Reason : null),
        Document = probe.HasError ? null : probe.Reason,
    };

    private static string ErrorReason(ProbeErrorKind kind) => kind switch
    {
        ProbeErrorKind.Timeout => "the probe timed out",
        ProbeErrorKind.Transport => "the probe could not reach the subject (a transport error)",
        _ => "the probe failed to run",
    };

    private static AefSeverity Severity(Severity severity) => severity switch
    {
        RedTeam.Severity.Informational => AefSeverity.None,
        RedTeam.Severity.Low => AefSeverity.Low,
        RedTeam.Severity.Medium => AefSeverity.Medium,
        RedTeam.Severity.High => AefSeverity.High,
        _ => AefSeverity.Critical,
    };

    private static List<AefTaxonomyId>? Taxonomy(AttackResult attack)
    {
        var ids = new List<AefTaxonomyId>();
        if (!string.IsNullOrWhiteSpace(attack.OwaspId))
        {
            ids.Add(new AefTaxonomyId(AefTaxonomyScheme.OwaspLlm, attack.OwaspId));
        }

        ids.AddRange(attack.MitreAtlasIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal)
            .Select(id => new AefTaxonomyId(AefTaxonomyScheme.MitreAtlas, id)));
        return ids.Count > 0 ? ids : null;
    }

    private static string Clip(string text) => text.Length <= 1024 ? text : text[..1024];
}
