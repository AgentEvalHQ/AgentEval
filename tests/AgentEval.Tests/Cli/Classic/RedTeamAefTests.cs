// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands;
using AgentEval.RedTeam;
using AgentEval.Results.Writing;
using Xunit;

namespace AgentEval.Tests.Cli.Classic;

/// <summary>How a red-team scan's probes become AEF lines (S1 #5a).</summary>
public class RedTeamAefTests
{
    private static ProbeResult Probe(string id, EvaluationOutcome outcome, string? error = null, ProbeErrorKind kind = ProbeErrorKind.None,
        GraderProvenance? grading = null) => new()
    {
        ProbeId = id,
        Prompt = "secret attack prompt",
        Response = "secret agent reply",
        Outcome = outcome,
        Reason = "matched: secret agent reply",
        Error = error,
        ErrorKind = kind,
        Severity = Severity.High,
        Grading = grading,
        TurnsUsed = 2,
    };

    private static RedTeamResult Result() => new()
    {
        AgentName = "agent",
        AttackResults =
        [
            new AttackResult
            {
                AttackName = "PromptInjection",
                OwaspId = "LLM01",
                MitreAtlasIds = ["AML.T0051", "AML.T0051"],
                ProbeResults =
                [
                    Probe("p1", EvaluationOutcome.Resisted),
                    Probe("p2", EvaluationOutcome.Succeeded,
                        grading: new GraderProvenance(EvaluationOutcome.Resisted, EvaluationOutcome.Succeeded, default, GradingProvenanceKind.Judge)),
                    Probe("p3", EvaluationOutcome.Inconclusive),
                    Probe("p4", EvaluationOutcome.Inconclusive, error: "socket closed: secret", kind: ProbeErrorKind.Transport),
                ],
            },
            new AttackResult
            {
                AttackName = "SystemPromptExtraction",
                OwaspId = "LLM07",
                NotMeasurableReason = "no system-prompt canary was planted",
                ProbeResults = [Probe("s1", EvaluationOutcome.Inconclusive), Probe("s2", EvaluationOutcome.Succeeded)],
            },
        ],
        BenignControlResults =
        [
            Probe("b1", EvaluationOutcome.Succeeded),   // answered
            Probe("b2", EvaluationOutcome.Resisted),    // refused: over-refusal
        ],
    };

    [Fact]
    public void Lines_MapEachOutcome_AndKeepContentOut()
    {
        var lines = RedTeamAef.Lines(Result()).ToDictionary(l => l.CaseId);

        Assert.Equal(AefState.Passed, lines["PromptInjection/p1"].State);
        Assert.False(lines["PromptInjection/p1"].Attack!.Success);
        Assert.Equal(AefState.Failed, lines["PromptInjection/p2"].State);
        Assert.True(lines["PromptInjection/p2"].Attack!.Success);
        Assert.Equal(AefAnnotatorKind.Llm, lines["PromptInjection/p2"].Annotator);   // the judge's verdict shipped
        Assert.Equal(AefAnnotatorKind.Code, lines["PromptInjection/p1"].Annotator);
        Assert.Equal(AefState.Inconclusive, lines["PromptInjection/p3"].State);
        Assert.Null(lines["PromptInjection/p3"].Attack!.Success);
        Assert.Equal(AefState.Error, lines["PromptInjection/p4"].State);
        Assert.Equal("the probe could not reach the subject (a transport error)", lines["PromptInjection/p4"].Reason);
        Assert.Equal(AefSeverity.High, lines["PromptInjection/p2"].Severity);
        Assert.Equal(2, lines["PromptInjection/p2"].Turns);
        Assert.Equal(2, lines["PromptInjection/p1"].Attack!.Taxonomy!.Count);   // LLM01 and AML.T0051 once

        // Not measurable in this setup, unless the attack succeeded anyway.
        Assert.Equal(AefState.NotApplicable, lines["SystemPromptExtraction/s1"].State);
        Assert.Equal("no system-prompt canary was planted", lines["SystemPromptExtraction/s1"].Reason);
        Assert.Equal(AefState.Failed, lines["SystemPromptExtraction/s2"].State);

        Assert.True(lines["benign/b1"].Control);
        Assert.Equal(AefState.Passed, lines["benign/b1"].State);
        Assert.Equal(AefState.Failed, lines["benign/b2"].State);

        Assert.All(lines.Values, l => Assert.DoesNotContain("secret", l.Reason ?? "", StringComparison.Ordinal));
    }
}
