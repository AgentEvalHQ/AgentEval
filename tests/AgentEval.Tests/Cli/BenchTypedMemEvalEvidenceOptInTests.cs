// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Cli.Commands;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.External.TypedMemEval;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// The opt-in has to REACH the capture layer, not merely exist on the command line.
/// </summary>
/// <remarks>
/// <para>
/// The command passed <c>options: null</c> to the runner unconditionally, so
/// <see cref="EvidenceCaptureMode.Full"/> was unreachable however it was invoked. Every layer
/// underneath was correct — the option exists, the mapping copies it faithfully, the guard permits
/// content under Full — and the run still failed with <c>evidence_content_not_allowed</c>, because
/// the caller never set the field.
/// </para>
/// <para>
/// The consuming project hit the identical shape in their own facade the same week: their builder
/// set five properties and not this one, their mapping copied it faithfully, and an adapter
/// honouring their flag had its content rejected. Two codebases, two surfaces, one defect —
/// "reachable ≠ fed". A test that only checked the mapping would have passed in both.
/// </para>
/// </remarks>
public sealed class BenchTypedMemEvalEvidenceOptInTests
{
    [Fact]
    public void OptIn_ReachesTheRunnerAsFullCapture()
    {
        var options = BenchTypedMemEvalCommand.BuildRunOptions(captureEvidenceContent: true);

        Assert.NotNull(options);
        Assert.Equal(EvidenceCaptureMode.Full, options.EvidenceCaptureMode);

        // The whole point of Full: content is permitted rather than rejected downstream.
        Assert.True(options.ToExternalOptions(TypedMemEvalVerticals.All[0]).PersistsEvidenceContent);
    }

    [Fact]
    public void WithoutTheOptIn_TheRunnerKeepsItsOwnDefaults()
    {
        // Null rather than an explicit References object: the default belongs to the runner, and
        // restating it here would be a second place to change it.
        Assert.Null(BenchTypedMemEvalCommand.BuildRunOptions(captureEvidenceContent: false));
    }

    [Fact]
    public void TheDefaultIsNotFull()
    {
        // Content capture must never be what you get by not choosing. Asserted against the mapped
        // options the runner actually receives, not against the enum's declared default.
        var defaults = new TypedMemEvalOptions().ToExternalOptions(TypedMemEvalVerticals.All[0]);

        Assert.NotEqual(EvidenceCaptureMode.Full, defaults.EvidenceCaptureMode);
        Assert.False(defaults.PersistsEvidenceContent);
    }
}
