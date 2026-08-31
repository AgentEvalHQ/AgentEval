using AgentEval.Memory.External.Models;
using AgentEval.Memory.External.TypedMemEval;
using Xunit;

namespace AgentEval.Memory.Tests;

/// <summary>
/// Pins the judge's completion budget to the value its shipped calibration was measured at.
/// </summary>
/// <remarks>
/// <para>
/// The default was 512 — ample for a non-reasoning model and dangerous on a reasoning one, where
/// the budget covers reasoning tokens too. A model can spend the whole allowance thinking and
/// return an empty completion, and this family has already paid for that once: empty completions
/// scored as answers until the silence accounting was built. A consumer pointing the judge at a
/// reasoning deployment on defaults would have hit it silently.
/// </para>
/// <para>
/// 1500 is not a guess. It is the value <c>TypedMemEvalJudgeCalibrationTests</c> floors at, which
/// makes it the configuration the published agreement number (0.987 over 230 cases) was actually
/// measured under. <b>A default that differs from the configuration the claim came from means the
/// out-of-box judge is not the judge the claim describes.</b>
/// </para>
/// <para>
/// Pinned by a test rather than trusted to a comment, because the whole lesson of the arc that
/// produced it is that a rule written down is not a rule enforced. Raising a cap can flip a
/// formerly-truncated verdict, so the effective value also has to travel with the run — asserted
/// below.
/// </para>
/// </remarks>
public class TypedMemEvalJudgeBudgetTests
{
    /// <summary>The budget the shipped calibration was measured at.</summary>
    private const int CalibratedCompletionBudget = 1500;

    [Fact]
    public void JudgeCompletionBudget_DefaultsToTheValueTheCalibrationWasMeasuredAt()
    {
        Assert.Equal(CalibratedCompletionBudget, new TypedMemEvalOptions().JudgeMaxOutputTokens);
    }

    [Fact]
    public void JudgeCompletionBudget_ReachesTheOptionsTheRunnerActuallyUses()
    {
        // TWO CLASSES CARRY THIS SETTING and only one of them is TypedMemEval's. The base
        // ExternalBenchmarkOptions defaults to 256 and serves LongMemEval, whose own calibration
        // was measured there -- raising it would be changing a different benchmark's judge on the
        // way past. So the guarantee is not "the base default is 1500"; it is that the TypedMemEval
        // facade OVERRIDES it on the way through.
        //
        // The first draft of this test asserted the base default and failed, which is the
        // applied-once shape catching its own author: a value corrected in one of two places.
        var built = new TypedMemEvalOptions()
            .ToExternalOptions(TypedMemEvalVerticals.For(TypedMemEvalVertical.Temporal));
        Assert.Equal(CalibratedCompletionBudget, built.JudgeMaxOutputTokens);
        Assert.NotEqual(new ExternalBenchmarkOptions().JudgeMaxOutputTokens, built.JudgeMaxOutputTokens);
    }

    [Fact]
    public void Provenance_CanCarryTheEffectiveBudget()
    {
        // Two runs differing only in this number are not comparable, and that has to be visible in
        // the record rather than inferred from a version. The report builder stamps it; this pins
        // that the field exists and survives serialisation-shaped use.
        var provenance = new BenchmarkRunProvenance
        {
            Mode = RunProvenanceMode.Full,
            JudgeMaxOutputTokens = 4096
        };
        Assert.Equal(4096, provenance.JudgeMaxOutputTokens);
    }
}
