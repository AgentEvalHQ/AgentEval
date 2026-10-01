// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.MAF.AgentHooks;
using AgentEval.MAF.Gatekeeper;
using AgentHooks;
using Xunit;

namespace AgentEval.Tests.MAF.AgentHooks.Ctk;

/// <summary>
/// S1b — the AGENT-HOOKS-0.1 reference core used as an independent oracle over what our adapter emits.
/// </summary>
/// <remarks>
/// <para><b>Scope, stated honestly.</b> The 51 <c>AH-CTK-*</c> vectors are a <i>host</i> conformance corpus:
/// each drives an agent loop with a scripted model, scripted tools and a CTK-supplied scripted interceptor,
/// then asserts which interception points the host emitted. Per AGENT-HOOKS-0.1 §2 Gatekeeper is an
/// <b>interceptor</b>, not a host, so those vectors do not exercise our code — running them against the
/// vendor's bundled <c>ReferenceHarness</c> would produce a green report about <i>Microsoft's</i> reference
/// host and tell us nothing about Gatekeeper. Reporting that as an AgentEval conformance result would be a
/// fabricated pass.</para>
///
/// <para>What <i>is</i> ours to verify is the other direction: every <see cref="Verdict"/> our interceptor
/// returns, and every context our mapper consumes, must satisfy §4/§5. These tests submit our own output to
/// the reference core — the same <c>ValidateVerdict</c> call the vendor's emitter makes internally before
/// dispatching — so a non-conformant verdict fails here rather than at an adopter's integration.</para>
/// </remarks>
public sealed class CtkNativeSmokeTests
{
    /// <summary>
    /// Asserts the reference core loaded. Deliberately a FAILURE, not a skip: <c>ResponsibleAI.AgentHooks</c>
    /// ships <c>agent_hooks_ffi</c> for win-x64, linux-x64, osx-x64 and osx-arm64, which covers every RID in
    /// this repo's CI matrix, so an unloadable core means the adapter is untestable there. xUnit 2.x has no
    /// first-class runtime skip (this repo deliberately does not take <c>Xunit.SkippableFact</c>), and the
    /// alternative — logging a message and returning success — would report a pass that was never measured.
    /// </summary>
    private static void RequireCore() =>
        Assert.True(
            CtkNative.IsAvailable,
            $"agent_hooks_ffi did not load on {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}. "
            + "The AGENT-HOOKS-0.1 reference core is the oracle for these tests; without it they measure nothing.");

    private static AgentContextBuilder Builder() =>
        new("inv-1", "agenteval", "sess-1", "TestAgent", "1.0", "2026-08-09T00:00:00Z");

    // ── ABI reachability ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void TheReferenceCoreIsReachable_AndImplementsTheSpecWeTargeted()
    {
        RequireCore();

        // Pinning the version matters: AEVP and the adapter are written against 0.1, and a core that silently
        // moved on would invalidate every assertion below.
        Assert.Equal("agent-hooks/0.1", CtkNative.SpecVersion());
    }

    [Fact]
    public void CanonicalJson_MatchesRfc8785_SoContentAddressesResolve()
    {
        RequireCore();

        // AEVP content-addresses its artefact; if our notion of canonical JSON diverged from the core's, the
        // pointer in verdict.evidence.artefact would not resolve for anyone else.
        Assert.Equal("""{"a":2,"b":1}""", CtkNative.CanonicalJson("""{"b":1,"a":2}"""));
    }

    // ── capability gating (§3.2) ─────────────────────────────────────────────────────────────────────
    [Fact]
    public void ShouldSkip_GatesAVectorOnDeclaredCapabilities()
    {
        RequireCore();

        const string vector = """
            {"id":"AH-CTK-PROBE","title":"probe","capabilities":["model_calls"],
             "scenario":{},"interceptor_script":[],"expect":{}}
            """;

        var skipped = CtkNative.CtkShouldSkip(vector, """["tool_calls"]""");
        var applies = CtkNative.CtkShouldSkip(vector, """["model_calls","tool_calls"]""");

        Assert.NotEqual("null", skipped.Trim());   // a harness lacking model_calls must skip it
        Assert.Equal("null", applies.Trim());      // a harness declaring it must run it
    }

    // ── negative control: the oracle must have teeth ─────────────────────────────────────────────────
    [Theory]
    [InlineData("""{"decision":"abstain"}""", "a decision outside the closed §5 enum")]
    [InlineData("""{"reason":"no decision at all"}""", "a verdict missing the required decision")]
    [InlineData("""{"decision":"transform"}""", "a transform carrying no transform payload")]
    public void TheValidatorRejectsMalformedVerdicts_SoAGreenRunIsNotVacuous(string malformed, string why)
    {
        RequireCore();

        // Without this, every assertion above would pass against a validator that rubber-stamps anything, and
        // "our verdicts are spec-conformant" would be a measurement that never happened. `abstain` is the
        // specific rejection that motivates AEVP: the enum really is closed, so abstention really is
        // inexpressible in a verdict and has to live in the evidence artefact instead.
        Assert.ThrowsAny<Exception>(() => CtkNative.ValidateVerdict(malformed));
        Assert.NotNull(why);
    }

    // ── THE TEST THAT IS ACTUALLY ABOUT US ───────────────────────────────────────────────────────────
    [Theory]
    [InlineData("allow")]
    [InlineData("deny")]
    [InlineData("transform")]
    public async Task EveryVerdictGatekeeperEmits_IsAcceptedByTheReferenceValidator(string expected)
    {
        RequireCore();

        var interceptor = new GatekeeperInterceptor([GateFor(expected)]);
        var context = Builder().PreToolCall("tc-1", "delete_files", new JsonObject { ["path"] = "/" });

        var verdict = await interceptor.InterceptAsync(context, CancellationToken.None);

        Assert.Equal(expected, verdict.Decision.ToWireName());

        // The oracle. Throws with the violated §5 clause if our verdict is malformed; the assertion below only
        // runs when the reference core accepted it.
        CtkNative.ValidateVerdict(verdict.ToWire().ToJsonString());
    }

    [Fact]
    public async Task TheAbstentionVerdict_IsConformant_EvenThoughItCarriesNoEvidenceOfSafety()
    {
        RequireCore();

        // The case AEVP exists for: no gate is registered, so nothing was evaluated. The spec's closed decision
        // enum forces us to say "allow". That verdict must still be structurally conformant — the profile, not
        // the verdict, is what records that nobody checked.
        var interceptor = new GatekeeperInterceptor([]);

        var verdict = await interceptor.InterceptAsync(
            Builder().PreToolCall("tc-1", "read_file", new JsonObject { ["path"] = "/tmp/x" }),
            CancellationToken.None);

        CtkNative.ValidateVerdict(verdict.ToWire().ToJsonString());
        Assert.Equal(Decision.Allow, verdict.Decision);
    }

    private static IToolGate GateFor(string decision) => decision switch
    {
        "deny" => new ScriptedGate(ToolGateVerdict.Block("ScriptedGate", "ctk:dangerous_tool")),
        "transform" => new ScriptedGate(ToolGateVerdict.Mutate(
            "ScriptedGate", new Dictionary<string, object?> { ["path"] = "/tmp/safe" }, "redirected")),
        _ => new ScriptedGate(ToolGateVerdict.Allow("ScriptedGate")),
    };

    private sealed class ScriptedGate(ToolGateVerdict verdict) : IToolGate
    {
        public string PolicyName => "ScriptedGate";
        public GateCost Cost => GateCost.PureCode;
        public ValueTask<ToolGateVerdict> InspectAsync(
            GatedToolCall call, CancellationToken cancellationToken = default) => ValueTask.FromResult(verdict);
    }
}
