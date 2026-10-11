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
/// control at <c>control</c>, case <c>benign/&lt;probe&gt;</c>. No prompt, response, grader reason or error message is
/// carried: reasons name the kind of absence only ([RUN-11]).
/// </summary>
internal static class RedTeamAef
{
    public static IReadOnlyList<AefProbeLine> Lines(RedTeamResult result)
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
                Control = true,
                State = state,
                Reason = state == AefState.Error ? ErrorReason(control.ErrorKind) : null,
                DurationMs = control.Duration?.TotalMilliseconds,
                // The over-refusal judge grades every benign control.
                Annotator = state == AefState.Error ? null : AefAnnotatorKind.Llm,
            });
        }

        return lines;
    }

    private static AefAnnotatorKind Grader(ProbeResult probe) =>
        probe.Grading?.ShippedBy == GradingProvenanceKind.Judge ? AefAnnotatorKind.Llm : AefAnnotatorKind.Code;

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
