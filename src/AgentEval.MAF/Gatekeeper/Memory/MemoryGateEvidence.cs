// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json.Serialization;
using AgentEval.Guardrails;

namespace AgentEval.MAF.Gatekeeper.Memory;

/// <summary>A privacy-minimized receipt for one individual or aggregate memory-gate decision.</summary>
public sealed class MemoryGateReceipt
{
    public MemoryGateReceipt(
        string operationId,
        MemoryGateStage stage,
        MemorySurface surface,
        MemoryGateAction action,
        string policyName,
        string reasonCode,
        string policyFingerprint,
        string contentDigest,
        string? runId,
        string? scopeCorrelation,
        DateTimeOffset evaluatedAtUtc)
    {
        OperationId = MemoryValidation.Identifier(operationId, nameof(operationId));
        Stage = MemoryValidation.SingleStage(stage, nameof(stage));
        Surface = MemoryValidation.Defined(surface, nameof(surface));
        Action = MemoryValidation.Defined(action, nameof(action));
        PolicyName = MemoryValidation.Identifier(policyName, nameof(policyName));
        if (!MemoryValidation.IsReasonCode(reasonCode))
        {
            throw new ArgumentException("Reason codes must be bounded machine-readable identifiers.", nameof(reasonCode));
        }

        ReasonCode = reasonCode;
        PolicyFingerprint = MemoryDigest.Validate(policyFingerprint, nameof(policyFingerprint));
        ContentDigest = MemoryDigest.Validate(contentDigest, nameof(contentDigest));
        RunId = MemoryValidation.OptionalIdentifier(runId, nameof(runId));
        ScopeCorrelation = scopeCorrelation is null
            ? null
            : MemoryDigest.Validate(scopeCorrelation, nameof(scopeCorrelation));
        EvaluatedAtUtc = evaluatedAtUtc;
    }

    public string OperationId { get; }
    public MemoryGateStage Stage { get; }
    public MemorySurface Surface { get; }
    public MemoryGateAction Action { get; }
    public string PolicyName { get; }
    public string ReasonCode { get; }
    public string PolicyFingerprint { get; }
    public string ContentDigest { get; }
    public string? RunId { get; }
    public string? ScopeCorrelation { get; }
    public DateTimeOffset EvaluatedAtUtc { get; }
}

/// <summary>The aggregate result of a frozen memory-gate pipeline.</summary>
public sealed class MemoryGateDecision
{
    internal MemoryGateDecision(
        MemorySecurityProfile profile,
        MemoryGateAction action,
        string reasonCode,
        string policyFingerprint,
        string? effectiveContent,
        IEnumerable<MemoryGateReceipt> receipts)
    {
        Action = MemoryValidation.Defined(action, nameof(action));
        Profile = MemoryValidation.Defined(profile, nameof(profile));
        ReasonCode = reasonCode;
        PolicyFingerprint = policyFingerprint;
        EffectiveContent = effectiveContent;
        Receipts = new ReadOnlyCollection<MemoryGateReceipt>(receipts.ToList());
    }

    public MemorySecurityProfile Profile { get; }
    public MemoryGateAction Action { get; }
    public string ReasonCode { get; }
    public string PolicyFingerprint { get; }

    [JsonIgnore]
    public string? EffectiveContent { get; }

    public IReadOnlyList<MemoryGateReceipt> Receipts { get; }
    public bool IsAllowed
        => Profile is MemorySecurityProfile.Observe ||
           Action is MemoryGateAction.Allow or MemoryGateAction.Sanitize;

    public bool ShouldApplySanitizedContent
        => Profile is MemorySecurityProfile.Enforce &&
           Action is MemoryGateAction.Sanitize;
}

/// <summary>Computes a stable, content-free fingerprint of a frozen memory-gate configuration.</summary>
public static class MemoryGateConfigFingerprint
{
    public static string Compute(
        IReadOnlyList<IMemoryGate> gates,
        MemorySecurityPolicy policy,
        MemoryGateCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(gates);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(capabilities);

        var builder = new StringBuilder();
        builder.Append("[memory-gates]\n");
        foreach (var gate in gates)
        {
            builder
                .Append(gate.GetType().FullName)
                .Append('#')
                .Append(gate.PolicyName)
                .Append('@')
                .Append((int)gate.Cost)
                .Append(':')
                .Append((int)gate.Stages)
                .Append(':')
                .Append((int)gate.Requirements);

            if (gate is IConfigurationFingerprintContributor contributor)
            {
                builder.Append(':').Append(contributor.ConfigurationFingerprintContribution);
            }

            builder.Append('\n');
        }

        builder
            .Append("[policy]\n")
            .Append(policy.PolicyId)
            .Append(':')
            .Append(policy.Version)
            .Append(':')
            .Append((int)policy.Profile)
            .Append(':')
            .Append((int)policy.AmbiguousWriteAction)
            .Append(':')
            .Append((int)policy.MinimumCoverage)
            .Append('\n')
            .Append("[capabilities]\n")
            .Append(capabilities.GuaranteesRunScope ? '1' : '0')
            .Append(capabilities.ScopeResolver is null ? '0' : '1')
            .Append(capabilities.QuarantineStore is null ? '0' : '1')
            .Append(capabilities.ApprovalHandler is null ? '0' : '1')
            .Append(capabilities.ProviderCandidateHook is null ? '0' : '1')
            .Append(capabilities.QuarantineOnApprovalUnavailable ? '1' : '0');


        AppendCapabilityType(builder, "scope", capabilities.ScopeResolver);
        AppendCapabilityType(builder, "quarantine", capabilities.QuarantineStore);
        AppendCapabilityType(builder, "approval", capabilities.ApprovalHandler);
        AppendCapabilityType(builder, "provider-hook", capabilities.ProviderCandidateHook);
        if (capabilities.ProviderCandidateHook is { } hook)
        {
            builder
                .Append('\n')
                .Append(MemoryDigest.Compute(hook.HookId))
                .Append(':')
                .Append(MemoryDigest.Compute(hook.Version));
        }

        return ManifestFingerprint.Hash(builder.ToString());
    }

    private static void AppendCapabilityType(StringBuilder builder, string label, object? capability)
    {
        if (capability is null)
        {
            return;
        }

        builder
            .Append('\n')
            .Append(label)
            .Append(':')
            .Append(capability.GetType().FullName);
    }
}
