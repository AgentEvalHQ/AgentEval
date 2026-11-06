// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365Demo.Tests.TestHelpers;

namespace AgentEval.Microsoft365Demo.Tests;

/// <summary>
/// The demo describes every evidence channel with the provider's one rule (<c>EvidenceSet&lt;T&gt;.Describe</c>): an
/// observed-empty channel says so, and a provided channel that is not valid names its validation, so the two never
/// print alike — on the screen, in turns.jsonl, results.json and summary.md.
/// </summary>
public sealed class EvidenceLineTests : IDisposable
{
    private readonly DemoRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public void EvidenceAvailability_AnObservedEmptyChannelAndAConflictingEmptyOne_NeverPrintAlike()
    {
        var evidence = Evidence(
            citationMarkers: EvidenceSet<CitationMarker>.Provided([], EvidenceValidation.Conflicting, EvidenceCoverage.Unknown, EvidenceScope.Response, []),
            citationSources: EvidenceSet<CitationSource>.Provided([], EvidenceValidation.Valid, EvidenceCoverage.Unknown, EvidenceScope.Response, []));

        var line = TurnRecorder.EvidenceAvailability(evidence);

        Assert.Contains("citation sources Provided (observed empty: 0 source(s) in this snapshot)", line, StringComparison.Ordinal);
        Assert.Contains("citation markers Provided (0 marker(s), Conflicting)", line, StringComparison.Ordinal);
        Assert.DoesNotContain("citation markers Provided (observed empty", line, StringComparison.Ordinal);
        Assert.Contains("tool executions NotRequested", line, StringComparison.Ordinal);
        Assert.StartsWith("answer none; ", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TurnRecord_DescribesEachChannelWithTheProvidersRule()
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();

        var run = await _rig.RunAsync(DemoRig.Consent, "--no-benchmark");

        run.AssertExit(0);
        var evidence = Assert.Single(run.JsonLines("turns.jsonl")).GetProperty("evidence").GetString()!;
        Assert.Contains("citation sources Provided (2 source(s)", evidence, StringComparison.Ordinal);
        Assert.Contains("tool executions NotRequested", evidence, StringComparison.Ordinal);
        Assert.Contains("citation sources Provided (2 source(s)", run.Text("summary.md"), StringComparison.Ordinal);
    }

    private static Microsoft365TurnEvidence Evidence(EvidenceSet<CitationMarker> citationMarkers, EvidenceSet<CitationSource> citationSources) => new(
        Microsoft365Evidence.SchemaVersion,
        Microsoft365Evidence.NormalizerVersion,
        Microsoft365Evidence.ProviderProfileVersion,
        Microsoft365Evidence.A2AVersion,
        new TurnIdentity("run-1", "case-1", "turn-1", 0, 0, "rpc-1", "msg-1", null, null, null),
        "TASK_STATE_COMPLETED",
        CaptureState.Complete,
        Answer: null,
        EvidenceSet<ToolSelectionObservation>.NotRequested(EvidenceScope.Response),
        EvidenceSet<ToolExecutionObservation>.NotRequested(EvidenceScope.Response),
        EvidenceSet<ApprovalIntentObservation>.NotRequested(EvidenceScope.Response),
        EvidenceSet<RetrievalExecutionObservation>.NotRequested(EvidenceScope.Response),
        citationMarkers,
        citationSources,
        [],
        EvidenceSet<SkillActivationObservation>.NotRequested(EvidenceScope.Response),
        [],
        []);
}
