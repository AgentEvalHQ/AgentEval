// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using Microsoft.Agents.AI;

namespace AgentEval.MAF.Gatekeeper.Memory;

/// <summary>Host capabilities required by a memory gate or requested enforcement profile.</summary>
[Flags]
public enum MemoryGateRequirements
{
    None = 0,
    RunScope = 1 << 0,
    AuthenticatedMemoryScope = 1 << 1,
    QuarantineStore = 1 << 2,
    ApprovalHandler = 1 << 3,
    ProviderCandidateHook = 1 << 4,
}

/// <summary>The disposition returned by an individual memory gate or aggregate pipeline decision.</summary>
public enum MemoryGateAction
{
    Allow,
    Sanitize,
    Exclude,
    Quarantine,
    RequireApproval,
    Reject,
}

/// <summary>A deterministic policy over one bounded memory lifecycle context.</summary>
public interface IMemoryGate
{
    string PolicyName { get; }
    GateCost Cost { get; }
    MemoryGateStage Stages { get; }
    MemoryGateRequirements Requirements { get; }

    ValueTask<MemoryGateVerdict> InspectAsync(
        MemoryGateContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>A single memory gate's bounded decision.</summary>
public sealed class MemoryGateVerdict
{
    private MemoryGateVerdict(
        MemoryGateAction action,
        string policyName,
        string reasonCode,
        string? sanitizedContent)
    {
        Action = MemoryValidation.Defined(action, nameof(action));
        PolicyName = MemoryValidation.Identifier(policyName, nameof(policyName));
        if (!MemoryValidation.IsReasonCode(reasonCode))
        {
            throw new ArgumentException("Reason codes must be bounded machine-readable identifiers.", nameof(reasonCode));
        }

        ReasonCode = reasonCode;
        SanitizedContent = MemoryValidation.OptionalContent(sanitizedContent, nameof(sanitizedContent));

        if ((Action is MemoryGateAction.Sanitize) != (SanitizedContent is not null))
        {
            throw new ArgumentException("Only sanitize verdicts carry replacement content.");
        }
    }

    public MemoryGateAction Action { get; }
    public string PolicyName { get; }
    public string ReasonCode { get; }

    [JsonIgnore]
    public string? SanitizedContent { get; }

    public static MemoryGateVerdict Allow(string policyName, string reasonCode = "memory.allow")
        => new(MemoryGateAction.Allow, policyName, reasonCode, sanitizedContent: null);

    public static MemoryGateVerdict Sanitize(string policyName, string sanitizedContent, string reasonCode)
        => new(MemoryGateAction.Sanitize, policyName, reasonCode, sanitizedContent);

    public static MemoryGateVerdict Exclude(string policyName, string reasonCode)
        => new(MemoryGateAction.Exclude, policyName, reasonCode, sanitizedContent: null);

    public static MemoryGateVerdict Quarantine(string policyName, string reasonCode)
        => new(MemoryGateAction.Quarantine, policyName, reasonCode, sanitizedContent: null);

    public static MemoryGateVerdict RequireApproval(string policyName, string reasonCode)
        => new(MemoryGateAction.RequireApproval, policyName, reasonCode, sanitizedContent: null);

    public static MemoryGateVerdict Reject(string policyName, string reasonCode)
        => new(MemoryGateAction.Reject, policyName, reasonCode, sanitizedContent: null);
}

/// <summary>Resolves trusted memory scope from application/session state rather than model arguments.</summary>
public interface IMemoryScopeResolver
{
    MemorySecurityScope Resolve(AgentSession session, string? agentName);
}

/// <summary>Stores a quarantined candidate outside normal memory retrieval.</summary>
public interface IMemoryQuarantineStore
{
    ValueTask<MemoryQuarantineReceipt> StoreAsync(
        MemoryQuarantineRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Obtains a surface-supported human or application approval decision.</summary>
public interface IMemoryApprovalHandler
{
    ValueTask<MemoryApprovalDecision> RequestApprovalAsync(
        MemoryApprovalRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Identifies a provider-native candidate/item interception capability.</summary>
public interface IMemoryProviderCandidateHook
{
    string HookId { get; }
    string Version { get; }
}

/// <summary>A private quarantine request; raw content is never serialized by the default JSON contract.</summary>
public sealed class MemoryQuarantineRequest
{
    public MemoryQuarantineRequest(MemoryGateContext context, MemoryGateReceipt decisionReceipt)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        DecisionReceipt = decisionReceipt ?? throw new ArgumentNullException(nameof(decisionReceipt));
        if (decisionReceipt.Action is not MemoryGateAction.Quarantine)
        {
            throw new ArgumentException("The decision receipt must represent quarantine.", nameof(decisionReceipt));
        }
    }

    [JsonIgnore]
    public MemoryGateContext Context { get; }
    public MemoryGateReceipt DecisionReceipt { get; }
}

/// <summary>Content-free confirmation that a candidate entered a separate quarantine boundary.</summary>
public sealed class MemoryQuarantineReceipt
{
    public MemoryQuarantineReceipt(string quarantineId, string operationId, DateTimeOffset storedAtUtc)
    {
        QuarantineId = MemoryValidation.Identifier(quarantineId, nameof(quarantineId));
        OperationId = MemoryValidation.Identifier(operationId, nameof(operationId));
        StoredAtUtc = storedAtUtc;
    }

    public string QuarantineId { get; }
    public string OperationId { get; }
    public DateTimeOffset StoredAtUtc { get; }
}

/// <summary>A content-free request for approval of a memory operation.</summary>
public sealed class MemoryApprovalRequest
{
    public MemoryApprovalRequest(MemoryGateReceipt decisionReceipt)
    {
        DecisionReceipt = decisionReceipt ?? throw new ArgumentNullException(nameof(decisionReceipt));
        if (decisionReceipt.Action is not MemoryGateAction.RequireApproval)
        {
            throw new ArgumentException("The decision receipt must represent an approval request.", nameof(decisionReceipt));
        }
    }

    public MemoryGateReceipt DecisionReceipt { get; }
}

/// <summary>The result returned by a configured memory approval handler.</summary>
public sealed class MemoryApprovalDecision
{
    public MemoryApprovalDecision(bool approved, string approvalId)
    {
        Approved = approved;
        ApprovalId = MemoryValidation.Identifier(approvalId, nameof(approvalId));
    }

    public bool Approved { get; }
    public string ApprovalId { get; }
}

/// <summary>Concrete host capabilities frozen into a memory pipeline at construction.</summary>
public sealed class MemoryGateCapabilities
{
    public MemoryGateCapabilities(
        bool guaranteesRunScope = false,
        IMemoryScopeResolver? scopeResolver = null,
        IMemoryQuarantineStore? quarantineStore = null,
        IMemoryApprovalHandler? approvalHandler = null,
        IMemoryProviderCandidateHook? providerCandidateHook = null,
        bool quarantineOnApprovalUnavailable = false)
    {
        GuaranteesRunScope = guaranteesRunScope;
        ScopeResolver = scopeResolver;
        QuarantineStore = quarantineStore;
        ApprovalHandler = approvalHandler;
        ProviderCandidateHook = providerCandidateHook;
        QuarantineOnApprovalUnavailable = quarantineOnApprovalUnavailable;

        if (quarantineOnApprovalUnavailable && quarantineStore is null)
        {
            throw new ArgumentException(
                "Approval fallback cannot target quarantine without a quarantine store.",
                nameof(quarantineOnApprovalUnavailable));
        }

        if (providerCandidateHook is not null)
        {
            _ = MemoryValidation.Identifier(providerCandidateHook.HookId, nameof(providerCandidateHook));
            _ = MemoryValidation.Identifier(providerCandidateHook.Version, nameof(providerCandidateHook));
        }
    }

    public bool GuaranteesRunScope { get; }
    public IMemoryScopeResolver? ScopeResolver { get; }
    public IMemoryQuarantineStore? QuarantineStore { get; }
    public IMemoryApprovalHandler? ApprovalHandler { get; }
    public IMemoryProviderCandidateHook? ProviderCandidateHook { get; }
    public bool QuarantineOnApprovalUnavailable { get; }

    internal bool Satisfies(MemoryGateRequirements requirement)
        => requirement switch
        {
            MemoryGateRequirements.RunScope => GuaranteesRunScope,
            MemoryGateRequirements.AuthenticatedMemoryScope => ScopeResolver is not null,
            MemoryGateRequirements.QuarantineStore => QuarantineStore is not null,
            MemoryGateRequirements.ApprovalHandler
                => ApprovalHandler is not null || (QuarantineOnApprovalUnavailable && QuarantineStore is not null),
            MemoryGateRequirements.ProviderCandidateHook => ProviderCandidateHook is not null,
            _ => requirement is MemoryGateRequirements.None,
        };
}
