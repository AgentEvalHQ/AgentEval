// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using Xunit;

namespace AgentEval.Memory.Tests;

/// <summary>
/// Characterization tests for the free-text judge protocol, pinning the failure mode that motivated
/// <see cref="JudgeVerdictProtocol.StructuredJson"/>.
/// </summary>
/// <remarks>
/// <para>These are not aspirational — they assert what the free-text parser <i>actually does</i>, so the
/// defect stays visible after it has been routed around. The free-text parser recovers the verdict from
/// the leading token and then vetoes it if the word "no" appears anywhere later
/// (LongMemEvalJudge.ParseResponse). That veto exists to catch genuinely self-contradicting output like
/// "yes, but no", and for that it is correct.</para>
/// <para>It also fires on a judge that answered <i>yes</i> and then used the word "no" in ordinary
/// reasoning prose — "there is no discrepancy", "no other date is mentioned". Nothing about that output is
/// ambiguous to a reader, and the veto turns it into <see cref="JudgeOutcomeStatus.Invalid"/>. That is
/// deterministic per input, which is why the same question fails across separate runs rather than
/// intermittently.</para>
/// </remarks>
public class LongMemEvalJudgeFreeTextFailureTests
{
    /// <summary>
    /// Judge prose that a reader would call an unambiguous "yes", which the free-text parser rejects
    /// because the reasoning happens to contain the word "no".
    /// </summary>
    public static TheoryData<string> UnambiguousYesVetoedByReasoningProse() => new()
    {
        "Yes. The model response identifies the correct date, and there is no discrepancy with the correct answer.",
        "Yes, the response contains the correct answer and no other interpretation is supported.",
        "yes — the response recalls the user's preference, so no information is missing.",
    };

    [Theory]
    [MemberData(nameof(UnambiguousYesVetoedByReasoningProse))]
    public void FreeTextParser_YesWithReasoningContainingNo_IsVetoedToInvalid(string judgeProse)
    {
        var status = LongMemEvalJudge.ParseResponse(judgeProse);

        // Documents the defect: a human reads "yes", the parser produces Invalid.
        Assert.Equal(JudgeOutcomeStatus.Invalid, status);
    }

    [Fact]
    public void FreeTextParser_SelfContradictingOutput_IsCorrectlyVetoed()
    {
        // The veto is not wrong in general — this input genuinely is ambiguous and Invalid is right.
        // Keeping both cases in one file is the point: the rule is right here and wrong above, which is
        // why the fix is a different protocol rather than a weaker guard.
        Assert.Equal(JudgeOutcomeStatus.Invalid, LongMemEvalJudge.ParseResponse("yes, but no"));
    }

    [Fact]
    public void FreeTextParser_LeadingNonVerdictToken_IsInvalid()
    {
        // The second free-text failure shape: the judge answered in a sentence instead of a bare token.
        Assert.Equal(JudgeOutcomeStatus.Invalid, LongMemEvalJudge.ParseResponse("The model response is correct."));
    }
}
