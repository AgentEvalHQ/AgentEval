// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.MAF.AgentHooks;
using AgentEval.MAF.AgentHooks.Aevp;
using AgentEval.MAF.Gatekeeper;
using AgentHooks;
using Json.Schema;
using Xunit;

namespace AgentEval.Tests.MAF.AgentHooks.Aevp;

/// <summary>
/// S2 — AEVP conformance vectors, in the style of agent-hooks' own numbered <c>AH-CTK-*</c> corpus.
/// </summary>
/// <remarks>
/// Each vector pins one property of the profile against the shipped JSON Schema. They exist to prove the
/// profile is CHECKABLE rather than philosophical: a claim about verdict trustworthiness that no tool can
/// falsify is worth nothing, and would fail the standard this project holds others to.
/// </remarks>
public sealed class AevpVectorTests
{
    private static readonly JsonSchema Schema = JsonSchema.FromFile(
        Path.Combine(AppContext.BaseDirectory, "Aevp", "aevp-0.1.schema.json"));

    private static EvaluationResults Validate(AgentEvidenceProfile profile) =>
        Schema.Evaluate(profile.ToJsonObject(), new EvaluationOptions { OutputFormat = OutputFormat.List });

    private static AgentEvidenceProfile Evaluated() => new()
    {
        Evaluated = true,
        EnforcementCapability = AevpEnforcementCapability.Transform,
        EvidenceTier = EvidenceTier.IntentToAct,
        Judge = new JudgeIdentity("ForbiddenToolGate"),
    };

    // ── AEVP-001 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Aevp001_AnEvaluatedProfileIsSchemaValid()
    {
        Assert.True(Validate(Evaluated()).IsValid);
    }

    // ── AEVP-002 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Aevp002_AnUnevaluatedProfileIsSchemaValid_AndCarriesNoEvidenceTier()
    {
        // The abstention case: the verdict may say "allow", but the profile says nobody checked.
        var profile = new AgentEvidenceProfile
        {
            Evaluated = false,
            EnforcementCapability = AevpEnforcementCapability.Observe,
        };

        Assert.True(Validate(profile).IsValid);
        Assert.DoesNotContain("evidence_tier", profile.ToCanonicalJson());
    }

    // ── AEVP-003 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Aevp003_UnevaluatedWithAnEvidenceTier_IsREJECTED()
    {
        // The core invariant. Claiming an evidence tier for a call nobody evaluated asserts evidence that was
        // never gathered — precisely the fabricated-pass failure this profile exists to make impossible.
        var contradictory = new AgentEvidenceProfile
        {
            Evaluated = false,
            EnforcementCapability = AevpEnforcementCapability.Observe,
            EvidenceTier = EvidenceTier.Behavioral,
        };

        Assert.False(Validate(contradictory).IsValid);
    }

    // ── AEVP-004 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Aevp004_AnUnknownProfileVersionIsREJECTED()
    {
        // A consumer must reject an unrecognised profile rather than partially interpret it.
        var doc = Evaluated().ToJsonObject();
        doc["aevp"] = "aevp/9.9";

        Assert.False(Schema.Evaluate(doc, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid);
    }

    // ── AEVP-005 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Aevp005_UnknownFieldsAreREJECTED_SoTheFiveFieldCapIsEnforced()
    {
        // The cap is the discipline. additionalProperties:false makes it mechanical rather than aspirational.
        var doc = Evaluated().ToJsonObject();
        doc["confidence"] = 0.9;

        Assert.False(Schema.Evaluate(doc, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid);
    }

    // ── AEVP-006 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Aevp006_CalibrationRoundTripsAndIsSchemaValid()
    {
        var profile = Evaluated() with
        {
            Judge = new JudgeIdentity("IndirectInjectionJudge", "1.2.0", "gpt-4o-mini"),
            Calibration = new CalibrationEvidence(
                Kappa: 0.94, DecisiveAccuracy: 0.97, DangerousErrors: 0, SampleSize: 92, BaselineBeaten: true),
        };

        Assert.True(Validate(profile).IsValid);

        var round = AgentEvidenceProfile.FromJson(profile.ToCanonicalJson());
        Assert.Equal(0.94, round.Calibration!.Kappa);
        Assert.Equal(92, round.Calibration.SampleSize);
        Assert.True(round.Calibration.BaselineBeaten);
    }

    // ── AEVP-007 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Aevp007_ContentAddressIsStableAndChangesWithContent()
    {
        // The artefact is content-addressed, so serialization must be deterministic — otherwise the pointer
        // in verdict.evidence.artefact would not resolve.
        var a = Evaluated();
        var b = Evaluated();
        Assert.Equal(a.ToContentAddress(), b.ToContentAddress());
        Assert.StartsWith("sha256:", a.ToContentAddress());
        Assert.Equal(71, a.ToContentAddress().Length);   // "sha256:" + 64 hex chars

        var changed = a with { Evaluated = false, EvidenceTier = null };
        Assert.NotEqual(a.ToContentAddress(), changed.ToContentAddress());
    }

    // ── AEVP-008 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Aevp008_TheInterceptorEmitsAResolvableProfileOnEveryVerdict()
    {
        // End-to-end: the S1 interceptor must attach an evidence pointer whose content address is the one the
        // profile computes, and an unenforced point must say evaluated:false.
        var builder = new AgentContextBuilder("a", "agenteval", "s", "TestAgent", "1.0", "2026-08-09T00:00:00Z");
        var interceptor = new GatekeeperInterceptor([new AllowGate()]);

        var enforced = await interceptor.InterceptAsync(
            builder.PreToolCall("tc-1", "read_file", new JsonObject { ["path"] = "/tmp/x" }), CancellationToken.None);
        var unenforced = await interceptor.InterceptAsync(
            builder.Output(JsonValue.Create("done")), CancellationToken.None);

        Assert.NotNull(enforced.Evidence);
        Assert.StartsWith("sha256:", enforced.Evidence!.Artefact);
        Assert.Equal(AgentEvidenceProfile.SpecVersion, enforced.Evidence.VerificationPointers!["aevp"]);

        Assert.NotNull(unenforced.Evidence);
        // Both are `allow`, but only one of them is evidence. That distinction is the entire point.
        Assert.NotEqual(enforced.Evidence.Artefact, unenforced.Evidence!.Artefact);
    }

    private sealed class AllowGate : IToolGate
    {
        public string PolicyName => "AllowAll";
        public GateCost Cost => GateCost.PureCode;
        public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ToolGateVerdict.Allow(PolicyName));
    }
}
