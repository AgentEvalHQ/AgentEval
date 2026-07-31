// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.ObjectModel;

namespace AgentEval.MAF.Gatekeeper.Memory;

/// <summary>
/// Executes a validated, immutable list of deterministic memory gates and aggregates their decisions
/// without performing provider, quarantine, approval, or persistence side effects.
/// </summary>
public sealed class MemoryGatePipeline
{
    private const MemoryGateStage KnownStages =
        MemoryGateStage.BeforeRead |
        MemoryGateStage.AfterRead |
        MemoryGateStage.BeforeWrite |
        MemoryGateStage.BeforePromotion |
        MemoryGateStage.BeforeAction |
        MemoryGateStage.AfterDecision;

    private const MemoryGateRequirements KnownRequirements =
        MemoryGateRequirements.RunScope |
        MemoryGateRequirements.AuthenticatedMemoryScope |
        MemoryGateRequirements.QuarantineStore |
        MemoryGateRequirements.ApprovalHandler |
        MemoryGateRequirements.ProviderCandidateHook;

    private readonly IReadOnlyList<IMemoryGate> _gates;
    private readonly MemoryGateCapabilities _capabilities;
    private readonly TimeProvider _timeProvider;

    public MemoryGatePipeline(
        IEnumerable<IMemoryGate> gates,
        MemoryGateCapabilities? capabilities = null,
        MemorySecurityPolicy? policy = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(gates);

        var snapshot = gates.ToArray();
        if (snapshot.Any(gate => gate is null))
        {
            throw new ArgumentException("Memory gate lists cannot contain null entries.", nameof(gates));
        }

        _capabilities = capabilities ?? new MemoryGateCapabilities();
        Policy = policy ?? MemorySecurityPolicy.Default;
        ValidateGates(snapshot, _capabilities);
        ValidatePolicyCapabilities(Policy, _capabilities);
        _gates = new ReadOnlyCollection<IMemoryGate>(snapshot);
        _timeProvider = timeProvider ?? TimeProvider.System;
        PolicyFingerprint = MemoryGateConfigFingerprint.Compute(_gates, Policy, _capabilities);
    }

    public IReadOnlyList<IMemoryGate> Gates => _gates;
    public MemorySecurityPolicy Policy { get; }
    public MemoryGateCapabilities Capabilities => _capabilities;
    public string PolicyFingerprint { get; }
    public MemoryGateRequirements Requirements
        => _gates.Aggregate(MemoryGateRequirements.None, (current, gate) => current | gate.Requirements);

    public async ValueTask<MemoryGateDecision> EvaluateAsync(
        MemoryGateContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var effectiveContext = context;
        var receipts = new List<MemoryGateReceipt>();
        var finalAction = MemoryGateAction.Allow;
        var finalReason = "memory.pipeline.allow";
        var sanitized = false;

        foreach (var gate in _gates)
        {
            if ((gate.Stages & context.Stage) == 0)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            MemoryGateVerdict verdict;
            try
            {
                verdict = await gate.InspectAsync(effectiveContext, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("A memory gate returned a null verdict.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                finalAction = MemoryGateAction.Reject;
                finalReason = "memory.gate.failure";
                receipts.Add(CreateReceipt(effectiveContext, gate.PolicyName, finalAction, finalReason));
                break;
            }

            if (!string.Equals(verdict.PolicyName, gate.PolicyName, StringComparison.Ordinal))
            {
                finalAction = MemoryGateAction.Reject;
                finalReason = "memory.gate.policy_mismatch";
                receipts.Add(CreateReceipt(effectiveContext, gate.PolicyName, finalAction, finalReason));
                break;
            }

            var action = NormalizeActionForCapabilities(verdict.Action, out var capabilityReason);
            var reasonCode = capabilityReason ?? verdict.ReasonCode;
            if (!IsLegal(context.Stage, action))
            {
                action = MemoryGateAction.Reject;
                reasonCode = "memory.gate.invalid_action";
            }

            receipts.Add(CreateReceipt(effectiveContext, gate.PolicyName, action, reasonCode));

            if (action is MemoryGateAction.Sanitize)
            {
                if (sanitized)
                {
                    finalAction = MemoryGateAction.Reject;
                    finalReason = "memory.pipeline.sanitize_loop";
                    receipts.Add(CreateReceipt(effectiveContext, "memory.pipeline", finalAction, finalReason));
                    break;
                }

                sanitized = true;
                effectiveContext = effectiveContext.WithContent(verdict.SanitizedContent);
            }

            if (Severity(action) > Severity(finalAction))
            {
                finalAction = action;
                finalReason = reasonCode;
            }

            if (finalAction is MemoryGateAction.Reject)
            {
                break;
            }
        }

        if (receipts.Count == 0)
        {
            finalReason = "memory.pipeline.no_applicable_gate";
        }

        return new MemoryGateDecision(
            Policy.Profile,
            finalAction,
            finalReason,
            PolicyFingerprint,
            Policy.Profile is MemorySecurityProfile.Observe ? context.Content : effectiveContext.Content,
            receipts);
    }

    private static void ValidateGates(
        IReadOnlyList<IMemoryGate> gates,
        MemoryGateCapabilities capabilities)
    {
        var policyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gate in gates)
        {
            var policyName = MemoryValidation.Identifier(gate.PolicyName, nameof(gates));
            if (!policyNames.Add(policyName))
            {
                throw new ArgumentException("Memory gate policy names must be unique.", nameof(gates));
            }

            if (gate.Cost is not (GateCost.PureCode or GateCost.Bounded))
            {
                throw new ArgumentException(
                    "Network and LLM memory gates cannot run in the inline pipeline.",
                    nameof(gates));
            }

            if (gate.Stages is MemoryGateStage.None || (gate.Stages & ~KnownStages) != 0)
            {
                throw new ArgumentException("A memory gate declares unknown or empty lifecycle stages.", nameof(gates));
            }

            if ((gate.Requirements & ~KnownRequirements) != 0)
            {
                throw new ArgumentException("A memory gate declares unknown host requirements.", nameof(gates));
            }

            foreach (var requirement in Enum.GetValues<MemoryGateRequirements>())
            {
                if (requirement is MemoryGateRequirements.None ||
                    (gate.Requirements & requirement) == 0)
                {
                    continue;
                }

                if (!capabilities.Satisfies(requirement))
                {
                    throw new InvalidOperationException(
                        $"Memory gate '{policyName}' requires unavailable capability '{requirement}'.");
                }
            }
        }
    }

    private static void ValidatePolicyCapabilities(
        MemorySecurityPolicy policy,
        MemoryGateCapabilities capabilities)
    {
        if (policy.Profile is MemorySecurityProfile.Observe)
        {
            return;
        }

        if (policy.AmbiguousWriteAction is MemoryGateAction.Quarantine &&
            capabilities.QuarantineStore is null)
        {
            throw new InvalidOperationException(
                "The enforcing memory policy can quarantine ambiguous writes but no quarantine store is configured.");
        }

        if (policy.AmbiguousWriteAction is MemoryGateAction.RequireApproval &&
            capabilities.ApprovalHandler is null &&
            !(capabilities.QuarantineOnApprovalUnavailable && capabilities.QuarantineStore is not null))
        {
            throw new InvalidOperationException(
                "The enforcing memory policy requires approval but no approval handler or quarantine fallback is configured.");
        }
    }

    private MemoryGateAction NormalizeActionForCapabilities(
        MemoryGateAction action,
        out string? reasonCode)
    {
        reasonCode = null;
        if (Policy.Profile is MemorySecurityProfile.Observe)
        {
            return action;
        }

        if (action is MemoryGateAction.Quarantine && _capabilities.QuarantineStore is null)
        {
            reasonCode = "memory.capability.quarantine_missing";
            return MemoryGateAction.Reject;
        }

        if (action is MemoryGateAction.RequireApproval && _capabilities.ApprovalHandler is null)
        {
            if (_capabilities.QuarantineOnApprovalUnavailable &&
                _capabilities.QuarantineStore is not null)
            {
                reasonCode = "memory.approval.fallback_quarantine";
                return MemoryGateAction.Quarantine;
            }

            reasonCode = "memory.capability.approval_missing";
            return MemoryGateAction.Reject;
        }

        return action;
    }

    private MemoryGateReceipt CreateReceipt(
        MemoryGateContext context,
        string policyName,
        MemoryGateAction action,
        string reasonCode)
        => new(
            context.OperationId,
            context.Stage,
            context.Surface,
            action,
            policyName,
            reasonCode,
            PolicyFingerprint,
            context.ContentDigest,
            context.RunId,
            context.AuthenticatedScope?.ComputeCorrelation(),
            _timeProvider.GetUtcNow());

    private static bool IsLegal(MemoryGateStage stage, MemoryGateAction action)
        => stage switch
        {
            MemoryGateStage.BeforeRead
                => action is MemoryGateAction.Allow or MemoryGateAction.Reject,
            MemoryGateStage.AfterRead
                => action is MemoryGateAction.Allow or MemoryGateAction.Sanitize or
                    MemoryGateAction.Exclude or MemoryGateAction.Reject,
            MemoryGateStage.BeforeWrite or MemoryGateStage.BeforePromotion
                => action is MemoryGateAction.Allow or MemoryGateAction.Sanitize or
                    MemoryGateAction.Quarantine or MemoryGateAction.RequireApproval or
                    MemoryGateAction.Reject,
            MemoryGateStage.BeforeAction
                => action is MemoryGateAction.Allow or MemoryGateAction.RequireApproval or
                    MemoryGateAction.Reject,
            MemoryGateStage.AfterDecision
                => action is MemoryGateAction.Allow,
            _ => false,
        };

    private static int Severity(MemoryGateAction action)
        => action switch
        {
            MemoryGateAction.Allow => 0,
            MemoryGateAction.Sanitize => 1,
            MemoryGateAction.Exclude => 2,
            MemoryGateAction.RequireApproval => 3,
            MemoryGateAction.Quarantine => 4,
            MemoryGateAction.Reject => 5,
            _ => int.MaxValue,
        };
}
