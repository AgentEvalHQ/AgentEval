// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Gatekeeper.Memory;

/// <summary>Explicit, immutable operation contracts keyed by exact tool/function name.</summary>
public sealed class MemoryToolOperationRegistry
{
    private readonly IReadOnlyDictionary<string, MemoryOperationContract> _contracts;

    public MemoryToolOperationRegistry(IEnumerable<MemoryOperationContract> contracts)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        var snapshot = new Dictionary<string, MemoryOperationContract>(StringComparer.Ordinal);
        foreach (var contract in contracts)
        {
            ArgumentNullException.ThrowIfNull(contract);
            if (contract.Surface is not (MemorySurface.Tool or MemorySurface.LocalMcp))
            {
                throw new ArgumentException(
                    "Tool registries only accept Tool or LocalMcp operation contracts.",
                    nameof(contracts));
            }

            if (!snapshot.TryAdd(contract.OperationName, contract))
            {
                throw new ArgumentException(
                    $"Duplicate memory operation contract '{contract.OperationName}'.",
                    nameof(contracts));
            }
        }

        _contracts = new ReadOnlyDictionary<string, MemoryOperationContract>(snapshot);
        ConfigurationFingerprint = MemoryPolicyFingerprint.Compute(snapshot
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => string.Join(
                ":",
                entry.Key,
                (int)entry.Value.Kind,
                (int)entry.Value.Surface,
                (int)entry.Value.Category,
                entry.Value.IsSideEffecting,
                entry.Value.MayReturnSensitiveContent,
                string.Join(",", entry.Value.ContentArguments),
                string.Join(",", entry.Value.ScopeArguments))));
    }

    public IReadOnlyDictionary<string, MemoryOperationContract> Contracts => _contracts;
    public string ConfigurationFingerprint { get; }

    public bool TryGet(string functionName, out MemoryOperationContract contract)
    {
        ArgumentNullException.ThrowIfNull(functionName);
        return _contracts.TryGetValue(functionName, out contract!);
    }
}

/// <summary>Classification result that never invents operation semantics from a tool name.</summary>
public enum MemoryToolClassification
{
    NonMemory,
    RegisteredMemory,
    UnclassifiedMemoryLike,
}

/// <summary>Flags likely memory surfaces for review without assigning them trusted operation semantics.</summary>
public static class MemoryToolClassifier
{
    private static readonly string[] ReviewHints =
    [
        "memory",
        "remember",
        "recall",
        "preference",
        "profile",
        "knowledge",
        "context store",
    ];

    public static MemoryToolClassification Classify(
        AITool tool,
        MemoryToolOperationRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(registry);

        if (registry.TryGet(tool.Name, out _))
        {
            return MemoryToolClassification.RegisteredMemory;
        }

        return ReviewHints.Any(hint =>
            tool.Name.Contains(hint, StringComparison.OrdinalIgnoreCase) ||
            (tool.Description?.Contains(hint, StringComparison.OrdinalIgnoreCase) ?? false))
                ? MemoryToolClassification.UnclassifiedMemoryLike
                : MemoryToolClassification.NonMemory;
    }
}

/// <summary>
/// Trusted adapter between a concrete tool schema and provider-neutral memory contexts.
/// Implementations resolve host scope and provenance; model arguments are never authority.
/// </summary>
public interface IMemoryToolContextAdapter
{
    MemoryGateContext CreateCallContext(
        GatedToolCall call,
        MemoryOperationContract operation,
        MemoryGateStage stage);

    MemoryGateContext CreateResultContext(
        GatedToolResult result,
        MemoryOperationContract operation);

    IReadOnlyDictionary<string, object?> ApplySanitizedArguments(
        GatedToolCall call,
        MemoryOperationContract operation,
        string sanitizedContent);

    object ApplySanitizedResult(
        GatedToolResult result,
        MemoryOperationContract operation,
        string sanitizedContent);
}

/// <summary>Receives content-free pipeline decisions for audit or attribution.</summary>
public interface IMemoryGateDecisionSink
{
    ValueTask RecordAsync(
        MemoryGateContext context,
        MemoryGateDecision decision,
        CancellationToken cancellationToken = default);
}

/// <summary>Result of applying an enforcing decision exactly once at an adapter boundary.</summary>
public sealed class MemoryGateEnforcementResult
{
    internal MemoryGateEnforcementResult(
        bool allowed,
        string? effectiveContent,
        MemoryQuarantineReceipt? quarantineReceipt,
        MemoryApprovalDecision? approvalDecision)
    {
        Allowed = allowed;
        EffectiveContent = effectiveContent;
        QuarantineReceipt = quarantineReceipt;
        ApprovalDecision = approvalDecision;
    }

    public bool Allowed { get; }
    [JsonIgnore]
    public string? EffectiveContent { get; }
    public MemoryQuarantineReceipt? QuarantineReceipt { get; }
    public MemoryApprovalDecision? ApprovalDecision { get; }
}

/// <summary>
/// Applies quarantine or approval after side-effect-free gate aggregation. The caller invokes it once
/// immediately before the protected provider/tool side effect.
/// </summary>
public sealed class MemoryGateDecisionExecutor
{
    private readonly MemoryGateCapabilities _capabilities;

    public MemoryGateDecisionExecutor(MemoryGateCapabilities capabilities)
        => _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));

    public async ValueTask<MemoryGateEnforcementResult> ExecuteAsync(
        MemoryGateContext context,
        MemoryGateDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(decision);
        cancellationToken.ThrowIfCancellationRequested();

        if (decision.Profile is MemorySecurityProfile.Observe)
        {
            return new MemoryGateEnforcementResult(
                allowed: true,
                context.Content,
                quarantineReceipt: null,
                approvalDecision: null);
        }

        switch (decision.Action)
        {
            case MemoryGateAction.Allow:
            case MemoryGateAction.Sanitize:
                return new MemoryGateEnforcementResult(
                    allowed: true,
                    decision.EffectiveContent,
                    quarantineReceipt: null,
                    approvalDecision: null);

            case MemoryGateAction.Quarantine:
            {
                var receipt = FindDispositionReceipt(decision, MemoryGateAction.Quarantine);
                var store = _capabilities.QuarantineStore
                    ?? throw new InvalidOperationException("A quarantine decision has no configured store.");
                var stored = await store.StoreAsync(
                    new MemoryQuarantineRequest(context, receipt),
                    cancellationToken).ConfigureAwait(false);
                return new MemoryGateEnforcementResult(
                    allowed: false,
                    effectiveContent: null,
                    stored,
                    approvalDecision: null);
            }

            case MemoryGateAction.RequireApproval:
            {
                var receipt = FindDispositionReceipt(decision, MemoryGateAction.RequireApproval);
                var handler = _capabilities.ApprovalHandler
                    ?? throw new InvalidOperationException("An approval decision has no configured handler.");
                var approval = await handler.RequestApprovalAsync(
                    new MemoryApprovalRequest(receipt),
                    cancellationToken).ConfigureAwait(false);
                return new MemoryGateEnforcementResult(
                    approval.Approved,
                    approval.Approved ? decision.EffectiveContent : null,
                    quarantineReceipt: null,
                    approval);
            }

            default:
                return new MemoryGateEnforcementResult(
                    allowed: false,
                    effectiveContent: null,
                    quarantineReceipt: null,
                    approvalDecision: null);
        }
    }

    private static MemoryGateReceipt FindDispositionReceipt(
        MemoryGateDecision decision,
        MemoryGateAction action)
        => decision.Receipts.LastOrDefault(receipt => receipt.Action == action)
            ?? throw new InvalidOperationException(
                $"The aggregate '{action}' decision has no matching content-free receipt.");
}

/// <summary>Adapts registered memory write/read calls into the existing Gatekeeper pre-call seam.</summary>
public sealed class MemoryToolCallGate : IToolGate, IConfigurationFingerprintContributor
{
    private readonly MemoryGatePipeline _pipeline;
    private readonly MemoryToolOperationRegistry _registry;
    private readonly IMemoryToolContextAdapter _adapter;
    private readonly MemoryGateDecisionExecutor _executor;
    private readonly IMemoryGateDecisionSink? _decisionSink;

    public MemoryToolCallGate(
        MemoryGatePipeline pipeline,
        MemoryToolOperationRegistry registry,
        IMemoryToolContextAdapter adapter,
        IMemoryGateDecisionSink? decisionSink = null)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _executor = new MemoryGateDecisionExecutor(pipeline.Capabilities);
        _decisionSink = decisionSink;
    }

    public string PolicyName => "memory.tool.call";
    public GateCost Cost => _pipeline.Gates.Any(gate => gate.Cost is GateCost.Bounded)
        ? GateCost.Bounded
        : GateCost.PureCode;
    public ToolGatePolicy MinimumPolicy => _pipeline.Policy.Profile is MemorySecurityProfile.Enforce
        ? ToolGatePolicy.ReplaceResult
        : ToolGatePolicy.WarnOnly;
    public GateRequirements Requirements => _pipeline.Policy.Profile is MemorySecurityProfile.Enforce
        ? GateRequirements.RunScope
        : GateRequirements.None;
    internal string RegistryFingerprint => _registry.ConfigurationFingerprint;
    internal string PipelineFingerprint => _pipeline.PolicyFingerprint;
    internal MemorySecurityProfile Profile => _pipeline.Policy.Profile;

    string IConfigurationFingerprintContributor.ConfigurationFingerprintContribution
        => MemoryPolicyFingerprint.Compute(
            _pipeline.PolicyFingerprint,
            _registry.ConfigurationFingerprint,
            _adapter.GetType().FullName);

    public async ValueTask<ToolGateVerdict> InspectAsync(
        GatedToolCall call,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_registry.TryGet(call.FunctionName, out var operation))
        {
            return ToolGateVerdict.Allow(PolicyName);
        }

        foreach (var stage in CallStages(operation.Kind))
        {
            var context = _adapter.CreateCallContext(call, operation, stage);
            ValidateContext(context, operation, stage);
            var decision = await _pipeline.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
            if (_decisionSink is not null)
            {
                await _decisionSink.RecordAsync(context, decision, cancellationToken).ConfigureAwait(false);
            }

            var enforcement = await _executor.ExecuteAsync(context, decision, cancellationToken).ConfigureAwait(false);
            if (!enforcement.Allowed)
            {
                return ToolGateVerdict.Block(PolicyName, decision.ReasonCode);
            }

            if (decision.ShouldApplySanitizedContent)
            {
                var arguments = _adapter.ApplySanitizedArguments(
                    call,
                    operation,
                    enforcement.EffectiveContent
                        ?? throw new InvalidOperationException("An applied sanitize decision has no content."))
                    ?? throw new InvalidOperationException("The memory tool adapter returned null arguments.");
                return ToolGateVerdict.Mutate(PolicyName, arguments, decision.ReasonCode);
            }
        }

        return ToolGateVerdict.Allow(PolicyName);
    }

    internal static IReadOnlyList<MemoryGateStage> CallStages(MemoryOperationKind kind)
        => kind switch
        {
            MemoryOperationKind.Search or MemoryOperationKind.Recall => [MemoryGateStage.BeforeRead],
            MemoryOperationKind.Promote => [MemoryGateStage.BeforeWrite, MemoryGateStage.BeforePromotion],
            MemoryOperationKind.Audit => [MemoryGateStage.AfterDecision],
            _ => [MemoryGateStage.BeforeWrite],
        };

    internal static void ValidateContext(
        MemoryGateContext context,
        MemoryOperationContract operation,
        MemoryGateStage expectedStage)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Stage != expectedStage ||
            context.Operation.Kind != operation.Kind ||
            context.Operation.Surface != operation.Surface ||
            !string.Equals(context.Operation.OperationName, operation.OperationName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The trusted memory tool adapter returned a context for a different operation or stage.");
        }
    }
}

/// <summary>Adapts registered memory read results into the existing Gatekeeper post-result seam.</summary>
public sealed class MemoryToolResultGate : IToolResultGate, IConfigurationFingerprintContributor
{
    private readonly MemoryGatePipeline _pipeline;
    private readonly MemoryToolOperationRegistry _registry;
    private readonly IMemoryToolContextAdapter _adapter;
    private readonly IMemoryGateDecisionSink? _decisionSink;

    public MemoryToolResultGate(
        MemoryGatePipeline pipeline,
        MemoryToolOperationRegistry registry,
        IMemoryToolContextAdapter adapter,
        IMemoryGateDecisionSink? decisionSink = null)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _decisionSink = decisionSink;
    }

    public string PolicyName => "memory.tool.result";
    public GateCost Cost => _pipeline.Gates.Any(gate => gate.Cost is GateCost.Bounded)
        ? GateCost.Bounded
        : GateCost.PureCode;
    public ToolGatePolicy MinimumPolicy => _pipeline.Policy.Profile is MemorySecurityProfile.Enforce
        ? ToolGatePolicy.ReplaceResult
        : ToolGatePolicy.WarnOnly;
    public GateRequirements Requirements => _pipeline.Policy.Profile is MemorySecurityProfile.Enforce
        ? GateRequirements.RunScope
        : GateRequirements.None;
    internal string RegistryFingerprint => _registry.ConfigurationFingerprint;
    internal string PipelineFingerprint => _pipeline.PolicyFingerprint;
    internal MemorySecurityProfile Profile => _pipeline.Policy.Profile;

    string IConfigurationFingerprintContributor.ConfigurationFingerprintContribution
        => MemoryPolicyFingerprint.Compute(
            _pipeline.PolicyFingerprint,
            _registry.ConfigurationFingerprint,
            _adapter.GetType().FullName);

    public async ValueTask<ToolResultVerdict> InspectAsync(
        GatedToolResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_registry.TryGet(result.FunctionName, out var operation) ||
            operation.Kind is not (MemoryOperationKind.Search or MemoryOperationKind.Recall))
        {
            return ToolResultVerdict.Allow(PolicyName);
        }

        var context = _adapter.CreateResultContext(result, operation);
        MemoryToolCallGate.ValidateContext(context, operation, MemoryGateStage.AfterRead);
        var decision = await _pipeline.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
        if (_decisionSink is not null)
        {
            await _decisionSink.RecordAsync(context, decision, cancellationToken).ConfigureAwait(false);
        }

        if (decision.Profile is MemorySecurityProfile.Observe)
        {
            return ToolResultVerdict.Allow(PolicyName);
        }

        return decision.Action switch
        {
            MemoryGateAction.Allow => ToolResultVerdict.Allow(PolicyName),
            MemoryGateAction.Sanitize => ToolResultVerdict.Redact(
                PolicyName,
                _adapter.ApplySanitizedResult(
                    result,
                    operation,
                    decision.EffectiveContent
                        ?? throw new InvalidOperationException("An applied sanitize decision has no content."))
                    ?? throw new InvalidOperationException("The memory tool adapter returned a null result."),
                decision.ReasonCode),
            _ => ToolResultVerdict.Block(PolicyName, decision.ReasonCode),
        };
    }
}

/// <summary>
/// Conservative value-taint bridge from registered memory reads to sensitive tool sinks.
/// It reuses Gatekeeper's run-scoped taint engine and existing evidence path.
/// </summary>
public sealed class MemoryInfluenceGate : IToolGate, IConfigurationFingerprintContributor
{
    private readonly TaintTrackingGate _inner;
    private readonly IReadOnlyList<string> _memorySources;
    private readonly IReadOnlyList<string> _sensitiveSinks;

    public MemoryInfluenceGate(
        MemoryToolOperationRegistry registry,
        IEnumerable<string> sensitiveSinkTools,
        int minimumTaintLength = 8,
        bool canonicalize = true)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(sensitiveSinkTools);

        _memorySources = registry.Contracts.Values
            .Where(contract => contract.Kind is MemoryOperationKind.Search or MemoryOperationKind.Recall)
            .Select(contract => contract.OperationName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        _sensitiveSinks = sensitiveSinkTools
            .Select(name => MemoryValidation.Identifier(name, nameof(sensitiveSinkTools)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (_memorySources.Count == 0)
        {
            throw new ArgumentException("At least one registered memory read tool is required.", nameof(registry));
        }

        if (_sensitiveSinks.Count == 0)
        {
            throw new ArgumentException("At least one sensitive sink tool is required.", nameof(sensitiveSinkTools));
        }

        _inner = new TaintTrackingGate(
            _memorySources,
            _sensitiveSinks,
            minimumTaintLength,
            canonicalize);
        MinimumTaintLength = minimumTaintLength;
        Canonicalize = canonicalize;
        RegistryFingerprint = registry.ConfigurationFingerprint;
    }

    public string PolicyName => "memory.influence";
    public GateCost Cost => GateCost.Bounded;
    public ToolGatePolicy MinimumPolicy => ToolGatePolicy.ReplaceResult;
    public GateRequirements Requirements => GateRequirements.RunScope;
    public int MinimumTaintLength { get; }
    public bool Canonicalize { get; }
    internal string RegistryFingerprint { get; }
    string IConfigurationFingerprintContributor.ConfigurationFingerprintContribution
        => MemoryPolicyFingerprint.Compute(
            _memorySources,
            _sensitiveSinks,
            MinimumTaintLength,
            Canonicalize);

    public async ValueTask<ToolGateVerdict> InspectAsync(
        GatedToolCall call,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var verdict = await _inner.InspectAsync(call, cancellationToken).ConfigureAwait(false);
        return verdict.Action is ToolGateAction.Block
            ? ToolGateVerdict.Block(PolicyName, "recalled memory data reached a sensitive tool sink")
            : ToolGateVerdict.Allow(PolicyName);
    }
}
