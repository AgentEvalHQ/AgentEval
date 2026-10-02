// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands;
using AgentEval.Compliance.EuAiAct.Reporting.Pdf;
using AgentEval.Evals.Agentic.Reporting.Pdf;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// What a report says about the judge that graded it, and the one placeholder judge left.
/// </summary>
/// <remarks>
/// Through 0.43 the EU AI Act PDF described <c>mode-a</c>, which every single-judge run records, as "a deterministic
/// stub … without requiring a live LLM endpoint", so every real-judge PDF told its reader the evidence was
/// stub-graded. The agentic PDF did the same for <c>mode-a</c>, and called a pure-code preset "single LLM judge".
/// </remarks>
public sealed class JudgeModeDisclosureTests
{
    [Fact]
    public void EuAiAct_ModeA_IsASingleRealJudge_NotAStub()
    {
        var text = EuAiActPdfRenderer.GetJudgeModeDescription("mode-a");

        Assert.Contains("Single LLM judge", text, StringComparison.Ordinal);
        Assert.DoesNotContain("stub", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("mode-b", "Per-criterion LLM judge")]
    [InlineData("multi-judge", "Multi-run judge")]
    public void EuAiAct_EveryRecordedModeHasItsOwnDescription(string mode, string expected) =>
        Assert.Contains(expected, EuAiActPdfRenderer.GetJudgeModeDescription(mode), StringComparison.Ordinal);

    [Fact]
    public void EuAiAct_LegacyStubGradedEvidence_IsStillDescribedAsMeasuringNoModel()
    {
        var mode = EuAiActPdfRenderer.EffectiveJudgeMode(evaluatorModel: "stub", judgeMode: "mode-a");

        Assert.Equal("stub", mode);
        Assert.Contains("measures no model", EuAiActPdfRenderer.GetJudgeModeDescription(mode), StringComparison.Ordinal);
        Assert.Equal("mode-a", EuAiActPdfRenderer.EffectiveJudgeMode(evaluatorModel: "gpt-4o-mini", judgeMode: "mode-a"));
    }

    [Fact]
    public void Agentic_ModeA_IsASingleRealJudge_AndNoneSaysNoModelGraded()
    {
        Assert.Contains("Single LLM judge", AgenticPdfRenderer.GetJudgeModeDescription("mode-a"), StringComparison.Ordinal);
        Assert.Contains("no language model graded anything", AgenticPdfRenderer.GetJudgeModeDescription("none"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMockJudge_LabelsEveryCriterionAsNotGraded()
    {
        var (judge, model, exit) = MockTarget.JudgeResolution;

        var result = await judge!.EvaluateAsync("question", "answer", ["criterion-a", "criterion-b"]);

        Assert.Equal(0, exit);
        Assert.Equal(MockTarget.Sut, model);
        Assert.StartsWith("MOCK", result.Summary, StringComparison.Ordinal);
        Assert.All(result.CriteriaResults, c => Assert.StartsWith("MOCK", c.Explanation, StringComparison.Ordinal));
    }
}
