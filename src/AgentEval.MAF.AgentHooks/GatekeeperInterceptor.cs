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
/// <para><b>Composition.</b> The gate loop is STRICT SEQUENTIAL, first-Block-wins, with the same fixed-point
/// rule as <c>AgentEvalToolGateExtensions</c>: a gate that rewrites the arguments restarts the scan from the first
/// gate against the rewritten call, so a later gate sees exactly what the host would execute. A transform is
/// emitted only once a full pass changes nothing. A gate that keeps rewriting is denied after
/// <see cref="MaxMutationRevalidations"/> passes. A gate that throws fails closed.</para>
///
/// <para><b>Not supported: run-scoped gates.</b> An AGENT-HOOKS host does not establish an
/// <c>AgentRunScope</c>, so a gate that requires one (<c>GateRequirements.RunScope</c>, e.g. <c>RunBudgetGate</c>)
/// would fall back to <c>RunLedger</c>'s process-wide state, and unrelated sessions would share its counters. The
/// constructor rejects such gates.</para>
///
/// <para><b>Honesty — what this adapter does NOT claim.</b> Points other than <c>pre_tool_call</c> return
/// <see cref="Verdict.Allow"/> carrying a <see cref="Warning"/> that names the point as unenforced. That is
/// deliberate: an <c>allow</c> from a seam with no gate registered means "nothing here examined this", not
/// "this was checked and found safe". The warning is the only conformant way to say so today — the
/// <c>Verdict</c> schema is <c>additionalProperties: false</c> with no extension slot, and its
/// <c>decision</c> enum has no abstain value. This limitation is the motivating case for the AEVP evidence
/// profile (<c>docs/aevp/AEVP-0.1.md</c>) and for a first-class abstention decision, which would be an
/// AGENT-HOOKS change of its own: planned, not filed upstream. A context without <c>messages</c> is evaluated, but
/// the verdict carries a warning that conversation-correlating gates had nothing to check.</para>
///
/// <para><b>Evidence.</b> Every verdict carries an AEVP profile's content address, and the profile's canonical
/// bytes are written to the <see cref="ArtifactStore"/> before the verdict is returned, so the address resolves —
/// with two exceptions, both fail-closed denies that carry no evidence: a <c>pre_tool_call</c> context without a
/// readable <c>tool_call</c> (reason <c>agenteval.gatekeeper.malformed_context</c>), and a gate that returns an
/// unrecognised action (reason <c>agenteval.gatekeeper.unknown_action</c>).</para>
/// </remarks>
public sealed class GatekeeperInterceptor : IInterceptor
{
    /// <summary>Full gate passes allowed after an argument rewrite before the call is denied as non-convergent; the
    /// same bound as the Gatekeeper tool pipeline.</summary>
    public const int MaxMutationRevalidations = 8;

    private readonly IToolGate[] _toolGates;

    /// <summary>Reason prefix for verdicts this adapter produces. Interceptor-supplied reasons must not use
    /// the <c>host_error:</c> prefix, which the spec reserves for host-synthesized failures (§5).</summary>
    public const string ReasonPrefix = "agenteval.gatekeeper";

    /// <summary>Creates an interceptor over the supplied Gatekeeper tool gates.</summary>
    /// <param name="toolGates">
    /// The gates to run at <c>pre_tool_call</c>, in composition order. The list is copied, so later changes to the
    /// caller's list do not change what is enforced. An empty list is permitted and is reported honestly (every
    /// verdict then carries the "no gate registered" warning) rather than silently reading as a clean pass.
    /// </param>
    /// <param name="artifactStore">
    /// Where emitted AEVP profiles are written so their addresses resolve. Defaults to an
    /// <see cref="InMemoryAevpArtifactStore"/>, which resolves for the lifetime of the process.
    /// </param>
    /// <exception cref="ArgumentException">A gate is null, or requires a run scope that an AGENT-HOOKS host does not
    /// establish.</exception>
    public GatekeeperInterceptor(IReadOnlyList<IToolGate> toolGates, IAevpArtifactStore? artifactStore = null)
    {
        ArgumentNullException.ThrowIfNull(toolGates);

        // Snapshot now: a caller-owned List<IToolGate> mutated later must not change enforcement, and must not throw
        // "collection was modified" in the middle of an interception.
        _toolGates = toolGates.ToArray();
        for (var i = 0; i < _toolGates.Length; i++)
        {
            var gate = _toolGates[i] ?? throw new ArgumentException($"Gate at index {i} is null.", nameof(toolGates));
            if (gate.Requirements.HasFlag(GateRequirements.RunScope))
            {
                throw new ArgumentException(
                    $"Gate '{gate.PolicyName}' requires a run scope (GateRequirements.RunScope). An AGENT-HOOKS host does " +
                    "not establish one, so its state would fall back to a process-wide ledger shared by every session. " +
                    "Use it inside a MAF agent built with UseGatekeeper instead.",
                    nameof(toolGates));
            }
        }

        ArtifactStore = artifactStore ?? new InMemoryAevpArtifactStore();
    }

    /// <summary>Where this interceptor writes the AEVP profiles its verdicts point to.</summary>
    public IAevpArtifactStore ArtifactStore { get; }

    /// <inheritdoc />
    public async ValueTask<Verdict> InterceptAsync(AgentContext context, CancellationToken ct)
    {
        // No null guard: AgentHooks.AgentContext is a struct, so a ThrowIfNull here could never fire.
        // Only pre_tool_call is enforced today. Every other point is declared unenforced rather than
        // returning a bare permit that would read as "checked and safe".
        if (context.InterceptionPoint != InterceptionPoint.PreToolCall)
        {
            return await NotEnforcedAsync(context.InterceptionPoint, ct).ConfigureAwait(false);
        }

        var original = AgentContextMapper.ToGatedToolCall(context);
        if (original is null)
        {
            // A pre_tool_call context without a usable tool_call is malformed for this point. Fail closed
            // with a deny rather than allowing an unexamined call through (never silently swallow).
            return Verdict.Deny(
                $"{ReasonPrefix}.malformed_context",
                "pre_tool_call context did not carry a readable tool_call.name; refusing to allow an unexamined tool call.");
        }

        if (_toolGates.Length == 0)
        {
            return await NoGatesRegisteredAsync(ct).ConfigureAwait(false);
        }

        var call = original;
        ToolGateVerdict? lastMutation = null;
        var revalidations = 0;

        // STRICT SEQUENTIAL, first-Block-wins, to a fixed point: an argument rewrite restarts the scan from the first
        // gate, so no gate is skipped and the rewritten call is checked by every gate before the host may run it.
        while (true)
        {
            var argumentsChanged = false;

            foreach (var gate in _toolGates)
            {
                ct.ThrowIfCancellationRequested();

                ToolGateVerdict verdict;
                try
                {
                    verdict = await gate.InspectAsync(call, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A gate that cannot decide must not let the call through.
                    return Verdict.Deny(
                        $"{ReasonPrefix}.{Slug(gate.PolicyName)}",
                        $"Gate '{gate.PolicyName}' threw ({ex.GetType().Name}); failing closed.")
                        with { Evidence = await EvidenceAsync(EvaluatedProfile(gate.PolicyName), ct).ConfigureAwait(false) };
                }

                switch (verdict.Action)
                {
                    case ToolGateAction.Allow:
                        continue;

                    case ToolGateAction.Block:
                        return Verdict.Deny(
                            $"{ReasonPrefix}.{Slug(verdict.PolicyName)}",
                            verdict.Reason ?? $"Blocked by {verdict.PolicyName}.")
                            with { Evidence = await EvidenceAsync(EvaluatedProfile(verdict.PolicyName), ct).ConfigureAwait(false) };

                    case ToolGateAction.Mutate:
                    {
                        var rewritten = AgentContextMapper.ToJsonObject(verdict.NewArguments);
                        if (rewritten is null)
                        {
                            // The gate wanted to change the call and the change cannot be carried faithfully.
                            return Verdict.Deny(
                                $"{ReasonPrefix}.{Slug(verdict.PolicyName)}",
                                verdict.Reason ?? $"{verdict.PolicyName} required rewritten arguments that could not be serialized faithfully.")
                                with { Evidence = await EvidenceAsync(EvaluatedProfile(verdict.PolicyName), ct).ConfigureAwait(false) };
                        }

                        if (JsonNode.DeepEquals(rewritten, AgentContextMapper.ToJsonObject(call.Arguments) ?? new JsonObject()))
                        {
                            continue;   // a rewrite to the same arguments is a fixed point, not a change
                        }

                        call = call with { Arguments = verdict.NewArguments };
                        lastMutation = verdict;
                        argumentsChanged = true;
                        break;
                    }

                    default:
                        // An unrecognised action must not be treated as Allow. Fail closed.
                        return Verdict.Deny(
                            $"{ReasonPrefix}.unknown_action",
                            $"Gate '{verdict.PolicyName}' returned an unrecognised action; failing closed.");
                }

                if (argumentsChanged)
                {
                    break;   // restart the scan against the rewritten arguments
                }
            }

            if (!argumentsChanged)
            {
                break;   // a full pass changed nothing: the call (rewritten or not) satisfies every gate
            }

            if (++revalidations > MaxMutationRevalidations)
            {
                return Verdict.Deny(
                    $"{ReasonPrefix}.mutation_revalidation",
                    $"Gate rewrites did not converge within {MaxMutationRevalidations} passes; failing closed.")
                    with { Evidence = await EvidenceAsync(EvaluatedProfile(lastMutation!.PolicyName), ct).ConfigureAwait(false) };
            }
        }

        var warnings = AgentContextMapper.HasConversation(context) ? [] : new[] { NoConversationWarning };

        if (lastMutation is not null)
        {
            // §4.2 fixes target = tool_call.args at pre_tool_call, so the whole args object is the transform root.
            return new Verdict(
                Decision.Transform,
                $"{ReasonPrefix}.{Slug(lastMutation.PolicyName)}",
                lastMutation.Reason ?? $"Arguments rewritten by {lastMutation.PolicyName}.",
                Warnings: warnings,
                Approval: null,
                Transform: new Transform("$target", AgentContextMapper.ToJsonObject(call.Arguments)!),
                Evidence: await EvidenceAsync(EvaluatedProfile(lastMutation.PolicyName), ct).ConfigureAwait(false),
                ResultLabels: []);
        }

        // Every gate ran and agreed. Unlike the not-enforced paths above, this permit IS evidence.
        return Verdict.Allow with
        {
            Warnings = warnings,
            Evidence = await EvidenceAsync(EvaluatedProfile(_toolGates[^1].PolicyName), ct).ConfigureAwait(false),
        };
    }

    private static readonly Warning NoConversationWarning = new(
        $"{ReasonPrefix}.no_conversation",
        "The context carried no messages, so gates that correlate against the conversation (ReferentialIntegrityGate, " +
        "TaintTrackingGate) had nothing to check. Their silence here is a coverage gap, not a pass.");

    private async ValueTask<Verdict> NotEnforcedAsync(InterceptionPoint point, CancellationToken ct) =>
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
            Evidence = await EvidenceAsync(new AgentEvidenceProfile
            {
                Evaluated = false,
                EnforcementCapability = AevpEnforcementCapability.Observe,
            }, ct).ConfigureAwait(false),
        };

    private async ValueTask<Verdict> NoGatesRegisteredAsync(CancellationToken ct) =>
        Verdict.Allow with
        {
            Warnings =
            [
                new Warning(
                    $"{ReasonPrefix}.no_gates_registered",
                    "No Gatekeeper tool gates were registered. This allow means nothing examined the call.")
            ],
            Evidence = await EvidenceAsync(new AgentEvidenceProfile
            {
                Evaluated = false,
                EnforcementCapability = AevpEnforcementCapability.Observe,
            }, ct).ConfigureAwait(false),
        };

    /// <summary>
    /// Writes the profile's canonical bytes to <see cref="ArtifactStore"/> and returns the AGENT-HOOKS
    /// <c>evidence</c> pointer to them. The pointer carries only the content address — §5.3 caps the serialized
    /// <c>evidence</c> member at 10,240 bytes, so the profile itself lives outside the verdict and is resolved by an
    /// auditor. It is stored first, so no verdict ever points at bytes nobody kept.
    /// </summary>
    private async ValueTask<Evidence> EvidenceAsync(AgentEvidenceProfile profile, CancellationToken ct)
    {
        var canonical = profile.ToCanonicalJson();
        var address = AgentEvidenceProfile.AddressOf(canonical);
        await ArtifactStore.PutAsync(address, canonical, ct).ConfigureAwait(false);
        return new Evidence(address, new Dictionary<string, string>
        {
            ["aevp"] = AgentEvidenceProfile.SpecVersion,
        });
    }

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
