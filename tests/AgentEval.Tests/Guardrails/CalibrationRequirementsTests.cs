// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.Guardrails.Judges;
using Xunit;

namespace AgentEval.Tests.Guardrails;

/// <summary>
/// The walk behind the inline-judge calibration check: it must find a judge through any wrapper, in a stable order,
/// once, and terminate on a wrapper that (wrongly) lists itself.
/// </summary>
public class CalibrationRequirementsTests
{
    private sealed class StubJudge(string axis) : IChatGate, IRequiresCalibration
    {
        public string AxisName => axis;
        public string PolicyName => $"judge:{axis}";
        public ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
            => new(GateVerdict.Allow(PolicyName));
    }

    private sealed class PlainGate : IChatGate
    {
        public string PolicyName => "plain";
        public ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
            => new(GateVerdict.Allow(PolicyName));
    }

    // A wrapper whose inner list includes itself: the walk must not loop.
    private sealed class SelfListingWrapper(IChatGate inner) : IChatGate, IDelegatingGate
    {
        public string PolicyName => "self-listing";
        public IEnumerable<object> InnerGates => new object[] { this, inner };
        public ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
            => inner.InspectAsync(text, cancellationToken);
    }

    [Fact]
    public void UnwrappedJudge_IsFound()
    {
        var judge = new StubJudge("a");
        Assert.Same(judge, Assert.Single(CalibrationRequirements.FindIn(judge)));
    }

    [Fact]
    public void NullOrPlainGate_FindsNothing()
    {
        Assert.Empty(CalibrationRequirements.FindIn(null));
        Assert.Empty(CalibrationRequirements.FindIn(new PlainGate()));
        Assert.Empty(CalibrationRequirements.FindIn(new JudgeVerdictCache(new PlainGate())));
    }

    [Fact]
    public void StockWrappers_AreSeenThrough_InPanelOrder()
    {
        var a = new StubJudge("a");
        var b = new StubJudge("b");
        var panel = new ParallelJudgeFanOut([new JudgeVerdictCache(a), new PlainGate(), new ParallelJudgeFanOut([b])]);

        var found = CalibrationRequirements.FindIn(panel);

        Assert.Equal(["a", "b"], found.Select(j => j.AxisName));
    }

    [Fact]
    public void SameJudgeReachedTwice_IsReportedOnce()
    {
        var shared = new StubJudge("shared");
        var panel = new ParallelJudgeFanOut([shared, new JudgeVerdictCache(shared)]);

        Assert.Same(shared, Assert.Single(CalibrationRequirements.FindIn(panel)));
    }

    [Fact]
    public void SelfListingWrapper_Terminates_AndStillFindsTheJudge()
    {
        var judge = new StubJudge("a");
        Assert.Same(judge, Assert.Single(CalibrationRequirements.FindIn(new SelfListingWrapper(judge))));
    }
}
