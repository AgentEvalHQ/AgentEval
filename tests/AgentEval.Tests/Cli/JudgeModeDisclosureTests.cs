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
/// <c>mode-a</c> is what every single-judge run records; the PDFs used to describe it as a deterministic stub.
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
