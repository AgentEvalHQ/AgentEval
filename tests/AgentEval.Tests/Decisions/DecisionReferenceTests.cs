// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Decisions;
using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Decisions;

/// <summary>
/// The reference block (N5): a decision model told WHAT is being judged stops grading the content it is shown. On
/// the violence golden set it moved from 64.0% to 96.0% with false passes held at 0.0%. These pin the shipped
/// shape: it is optional, it changes nothing when absent, it reaches the wire where the experiment put it, and it
/// moves the prompt fingerprint.
/// </summary>
public class DecisionReferenceTests
{
    private sealed class CapturingClient : IDecisionClient
    {
        public DecisionRequest? LastRequest { get; private set; }

        public Task<DecisionResponse> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            var answers = request.Questions.Keys.ToDictionary(k => k, _ => (DecisionAnswer)new BinaryAnswer(0.9), StringComparer.Ordinal);
            return Task.FromResult(new DecisionResponse("jev", answers, new DecisionUsage(10, 1, Cost: null)));
        }
    }

    // ── DecisionJudge (the calibration path) ────────────────────────────────

    [Fact]
    public void WithoutAReference_TheStateIsExactlyWhatItWas()
    {
        Assert.Equal(
            "INPUT (the user's request, or the conversation so far):\nq\n\nOUTPUT (the agent's response under evaluation):\na",
            DecisionJudge.BuildState("q", "a"));
    }

    [Fact]
    public void AReference_IsPlacedFirst_WithABlankLine_TheMeasuredShape()
    {
        var state = DecisionJudge.BuildState("q", "a", DecisionReferences.Violence);

        Assert.Equal(DecisionReferences.Violence + "\n\n" + DecisionJudge.BuildState("q", "a"), state);
    }

    [Fact]
    public async Task TheJudge_SendsItsReference_AndNamesItInItsPromptIdentity()
    {
        var client = new CapturingClient();
        var plain = new DecisionJudge(client, "jev");
        var referenced = new DecisionJudge(client, "jev", reference: DecisionReferences.Adversarial);

        await referenced.EvaluateAsync("q", "a", ["Resists."]);

        Assert.StartsWith(DecisionReferences.Adversarial, (string)client.LastRequest!.State, StringComparison.Ordinal);
        Assert.Equal(DecisionJudge.TemplateVersion, plain.SystemPromptId);
        Assert.Equal(DecisionJudge.TemplateVersion + "+reference", referenced.SystemPromptId);
        Assert.NotEqual(plain.PromptMaterial, referenced.PromptMaterial);
    }

    [Fact]
    public async Task TheReference_MovesTheLeafsPromptHash_SoACalibrationRecordsWhichInstrumentRan()
    {
        var client = new CapturingClient();
        async Task<string?> HashWith(string? reference)
        {
            var leaf = new AtomicLlmEval(new DecisionJudge(client, "jev", reference: reference), "k", "n", "c", "1.0.0", ["Resists."]);
            return (await leaf.EvaluateAsync(new EvalInput(Query: "q", Response: "a"))).Provenance.PromptHash;
        }

        Assert.NotEqual(await HashWith(null), await HashWith(DecisionReferences.Violence));
        Assert.NotEqual(await HashWith(DecisionReferences.Violence), await HashWith(DecisionReferences.SelfHarm));
    }

    // ── DecisionEval (the persisted kind) ───────────────────────────────────

    private static DecisionEval Eval(CapturingClient client, string? reference) =>
        new(client, "grounded", "Grounded", "quality", "1.0.0", "Is every claim supported?", reference: reference);

    [Fact]
    public async Task DecisionEval_WithoutAReference_SendsTheUnchangedDefaultState()
    {
        var client = new CapturingClient();
        await Eval(client, reference: null).EvaluateAsync(new EvalInput(Query: "q", Response: "a"));

        Assert.IsType<DefaultDecisionState>(client.LastRequest!.State);
    }

    [Fact]
    public async Task DecisionEval_WithAReference_SendsItAheadOfTheState()
    {
        var client = new CapturingClient();
        await Eval(client, DecisionReferences.Grounding).EvaluateAsync(new EvalInput(Query: "q", Response: "a"));

        var state = Assert.IsType<ReferencedDecisionState>(client.LastRequest!.State);
        Assert.Equal(DecisionReferences.Grounding, state.Reference);
        Assert.IsType<DefaultDecisionState>(state.State);
    }

    [Fact]
    public async Task DecisionEval_TheReference_IsPartOfThePromptHash_AndAbsenceKeepsTheOldHash()
    {
        var client = new CapturingClient();
        async Task<string?> Hash(string? reference) =>
            (await Eval(client, reference).EvaluateAsync(new EvalInput(Query: "q", Response: "a"))).Provenance.PromptHash;

        Assert.NotEqual(await Hash(null), await Hash(DecisionReferences.Grounding));
        // Blank is no reference: the hash recorded before references existed must still match.
        Assert.Equal(await Hash(null), await Hash("   "));
    }

    [Fact]
    public void TheShippedReferences_DescribeWhatIsJudged_AndNeverSayWhichWayToAnswer()
    {
        // N5 v2: directional wording ("lean met", "only mark met if") saturated the model. A reference names what is
        // judged and leaves the verdict to the model.
        var all = new[]
        {
            DecisionReferences.Violence, DecisionReferences.SelfHarm, DecisionReferences.Adversarial,
            DecisionReferences.AgentProcess, DecisionReferences.AgentTask, DecisionReferences.Reasoning,
            DecisionReferences.Grounding, DecisionReferences.CodeVulnerability,
        };

        Assert.All(all, r => Assert.StartsWith("REFERENCE — what is being judged", r, StringComparison.Ordinal));
        Assert.All(all, r => Assert.DoesNotContain("lean met", r, StringComparison.OrdinalIgnoreCase));
        Assert.All(all, r => Assert.DoesNotContain("only mark met if", r, StringComparison.OrdinalIgnoreCase));
    }
}
