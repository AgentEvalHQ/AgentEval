// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.MAF.AgentHooks.Aevp;
using AgentEval.MAF.Gatekeeper;
using AgentHooks;

namespace AgentEval.MAF.AgentHooks;

/// <summary>
/// Exposes AgentEval's Gatekeeper gate pipeline as an AGENT-HOOKS-0.1 <see cref="IInterceptor"/>, so any
/// conformant host can run it.
/// </summary>
/// <remarks>
/// <para><b>Role.</b> Per AGENT-HOOKS-0.1 §2, an interceptor is "a component registered with the host that
/// receives an <c>AgentContext</c> and returns a <c>Verdict</c>". That is exactly Gatekeeper's shape: an
/// <see cref="IToolGate"/> is <c>InspectAsync(GatedToolCall) -&gt; ToolGateVerdict</c>. This adapter is a
/// translation layer, not a re-implementation — the gates and their ordering semantics are unchanged.</para>
///
/// <para><b>Verdict mapping.</b> Gatekeeper's three tool-gate actions map 1:1 onto the spec's three
/// decisions: <c>Allow → allow</c>, <c>Block → deny</c>, <c>Mutate → transform</c>. At
/// <c>pre_tool_call</c> the spec fixes <c>target = tool_call.args</c> (§4.2), so a Mutate becomes
/// <c>Transform("$target", &lt;new args&gt;)</c>.</para>
///
/// <para><b>Composition.</b> The gate loop is STRICT SEQUENTIAL, first-non-Allow-wins — identical to
/// <c>AgentEvalToolGateExtensions</c>. The spec's own <c>sequential_first_deny</c> composition profile
/// describes the same rule, so the host profile should be set accordingly.</para>
///
/// <para><b>Honesty — what this adapter does NOT claim.</b> Points other than <c>pre_tool_call</c> return
/// <see cref="Verdict.Allow"/> carrying a <see cref="Warning"/> that names the point as unenforced. That is
/// deliberate: an <c>allow</c> from a seam with no gate registered means "nothing here examined this", not
/// "this was checked and found safe". The warning is the only conformant way to say so today — the
/// <c>Verdict</c> schema is <c>additionalProperties: false</c> with no extension slot, and its
/// <c>decision</c> enum has no abstain value. This limitation is the motivating case for the AEVP evidence
/// profile (S2) and the upstream abstention proposal (S3).</para>
/// </remarks>
public sealed class GatekeeperInterceptor : IInterceptor
{
    private readonly IReadOnlyList<IToolGate> _toolGates;

    /// <summary>Reason prefix for verdicts this adapter produces. Interceptor-supplied reasons must not use
    /// the <c>host_error:</c> prefix, which the spec reserves for host-synthesized failures (§5).</summary>
    public const string ReasonPrefix = "agenteval.gatekeeper";

    /// <summary>Creates an interceptor over the supplied Gatekeeper tool gates.</summary>
    /// <param name="toolGates">
    /// The gates to run at <c>pre_tool_call</c>, in composition order. An empty list is permitted and is
    /// reported honestly (every verdict then carries the "no gate registered" warning) rather than silently
    /// reading as a clean pass.
    /// </param>
    public GatekeeperInterceptor(IReadOnlyList<IToolGate> toolGates)
    {
        ArgumentNullException.ThrowIfNull(toolGates);
        _toolGates = toolGates;
    }

    /// <inheritdoc />
    public async ValueTask<Verdict> InterceptAsync(AgentContext context, CancellationToken ct)
    {
        // No null guard: AgentHooks.AgentContext is a struct, so a ThrowIfNull here could never fire.
        // Only pre_tool_call is enforced today. Every other point is declared unenforced rather than
        // returning a bare permit that would read as "checked and safe".
        if (context.InterceptionPoint != InterceptionPoint.PreToolCall)
        {
            return NotEnforced(context.InterceptionPoint);
        }

        var call = AgentContextMapper.ToGatedToolCall(context);
        if (call is null)
        {
            // A pre_tool_call context without a usable tool_call is malformed for this point. Fail closed
            // with a deny rather than allowing an unexamined call through (never silently swallow).
            return Verdict.Deny(
                $"{ReasonPrefix}.malformed_context",
                "pre_tool_call context did not carry a readable tool_call.name; refusing to allow an unexamined tool call.");
        }

        if (_toolGates.Count == 0)
        {
            return NoGatesRegistered();
        }

        // STRICT SEQUENTIAL, first-non-Allow-wins — mirrors AgentEvalToolGateExtensions exactly.
        foreach (var gate in _toolGates)
        {
            ct.ThrowIfCancellationRequested();

            var verdict = await gate.InspectAsync(call, ct).ConfigureAwait(false);

            switch (verdict.Action)
            {
                case ToolGateAction.Allow:
                    continue;

                case ToolGateAction.Block:
                    return Verdict.Deny(
                        $"{ReasonPrefix}.{Slug(verdict.PolicyName)}",
                        verdict.Reason ?? $"Blocked by {verdict.PolicyName}.")
                        with { Evidence = Aevp(EvaluatedProfile(verdict.PolicyName)) };

                case ToolGateAction.Mutate:
                    return ToTransform(verdict);

                default:
                    // An unrecognised action must not be treated as Allow. Fail closed.
                    return Verdict.Deny(
                        $"{ReasonPrefix}.unknown_action",
                        $"Gate '{verdict.PolicyName}' returned an unrecognised action; failing closed.");
            }
        }

        // Every gate ran and agreed. Unlike the not-enforced paths above, this permit IS evidence.
        return Verdict.Allow with { Evidence = Aevp(EvaluatedProfile(_toolGates[^1].PolicyName)) };
    }

    /// <summary>Maps a Mutate verdict onto a spec <c>transform</c> rooted at <c>$target</c>.</summary>
    private static Verdict ToTransform(ToolGateVerdict verdict)
    {
        var rewritten = AgentContextMapper.ToJsonObject(verdict.NewArguments);
        if (rewritten is null)
        {
            // A Mutate with no usable arguments cannot be expressed as a transform. Denying is the honest
            // outcome: the gate wanted to change the call, and we could not carry that change.
            return Verdict.Deny(
                $"{ReasonPrefix}.{Slug(verdict.PolicyName)}",
                verdict.Reason ?? $"{verdict.PolicyName} required rewritten arguments that could not be serialized.");
        }

        // §4.2 fixes target = tool_call.args at pre_tool_call, so the whole args object is the transform root.
        return new Verdict(
            Decision.Transform,
            $"{ReasonPrefix}.{Slug(verdict.PolicyName)}",
            verdict.Reason ?? $"Arguments rewritten by {verdict.PolicyName}.",
            Warnings: [],
            Approval: null,
            Transform: new Transform("$target", rewritten),
            Evidence: Aevp(EvaluatedProfile(verdict.PolicyName)),
            ResultLabels: []);
    }

    private static Verdict NotEnforced(InterceptionPoint point) =>
        Verdict.Allow with
        {
            Warnings =
            [
                new Warning(
                    $"{ReasonPrefix}.point_not_enforced",
                    $"AgentEval Gatekeeper registers no gate at '{point}'. This allow means the point was not " +
                    "examined by this interceptor, not that it was checked and found safe.")
            ],
            // evaluated:false — the permit carries no evidence of safety. This is the abstention the closed
            // decision enum cannot express, and the reason AEVP exists.
            Evidence = Aevp(new AgentEvidenceProfile
            {
                Evaluated = false,
                EnforcementCapability = AevpEnforcementCapability.Observe,
            }),
        };

    private static Verdict NoGatesRegistered() =>
        Verdict.Allow with
        {
            Warnings =
            [
                new Warning(
                    $"{ReasonPrefix}.no_gates_registered",
                    "No Gatekeeper tool gates were registered. This allow means nothing examined the call.")
            ],
            Evidence = Aevp(new AgentEvidenceProfile
            {
                Evaluated = false,
                EnforcementCapability = AevpEnforcementCapability.Observe,
            }),
        };

    /// <summary>
    /// Builds the AGENT-HOOKS <c>evidence</c> pointer for a profile. The pointer carries only the content
    /// address — §5.3 caps the serialized <c>evidence</c> member at 10,240 bytes, so the profile itself lives
    /// outside the verdict and is resolved by an auditor.
    /// </summary>
    private static Evidence Aevp(AgentEvidenceProfile profile) =>
        new(profile.ToContentAddress(), new Dictionary<string, string>
        {
            ["aevp"] = AgentEvidenceProfile.SpecVersion,
        });

    /// <summary>The profile describing a verdict this interceptor actually computed at the tool seam.</summary>
    private static AgentEvidenceProfile EvaluatedProfile(string policyName) => new()
    {
        Evaluated = true,
        // The tool seam can deny AND rewrite arguments, so it is transform-capable.
        EnforcementCapability = AevpEnforcementCapability.Transform,
        // Deterministic gates decide from the call itself — an attempted action, not a real one, and not
        // merely something the model said.
        EvidenceTier = EvidenceTier.IntentToAct,
        Judge = new JudgeIdentity(policyName),
        // Calibration is deliberately ABSENT: these are deterministic gates, and asserting a calibration
        // record they never underwent would be exactly the fabrication this profile exists to prevent.
    };

    /// <summary>Reduces a policy name to a stable machine identifier for the verdict <c>reason</c>.</summary>
    private static string Slug(string? policyName)
    {
        if (string.IsNullOrWhiteSpace(policyName))
        {
            return "unnamed_policy";
        }

        var chars = policyName
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_')
            .ToArray();
        return new string(chars);
    }
}
