// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Gatekeeper.Memory;

/// <summary>Fail behavior for an unavailable or invalid context-provider boundary.</summary>
public enum MemoryContextProviderFailureMode
{
    FailClosed,
    ContinueWithoutProviderData,
}

/// <summary>Origin of a message offered to a context provider for persistence.</summary>
public enum MemoryContextMessageOrigin
{
    Request,
    Response,
}

/// <summary>Content-free context-provider lifecycle event.</summary>
public enum MemoryContextProviderEventKind
{
    RecallProviderFailure,
    WriteProviderFailure,
    RecallRejected,
    WriteExcluded,
    DynamicToolExcluded,
}

/// <summary>Immutable context-provider boundary policy.</summary>
public sealed class MemoryContextProviderOptions
{
    public MemoryContextProviderOptions(
        MemoryContextProviderFailureMode recallFailureMode = MemoryContextProviderFailureMode.FailClosed,
        MemoryContextProviderFailureMode writeFailureMode = MemoryContextProviderFailureMode.FailClosed,
        int maximumMessages = 256,
        int maximumTools = 128)
    {
        RecallFailureMode = MemoryValidation.Defined(recallFailureMode, nameof(recallFailureMode));
        WriteFailureMode = MemoryValidation.Defined(writeFailureMode, nameof(writeFailureMode));
        MaximumMessages = ValidateLimit(maximumMessages, nameof(maximumMessages), 4_096);
        MaximumTools = ValidateLimit(maximumTools, nameof(maximumTools), 1_024);
    }

    public MemoryContextProviderFailureMode RecallFailureMode { get; }
    public MemoryContextProviderFailureMode WriteFailureMode { get; }
    public int MaximumMessages { get; }
    public int MaximumTools { get; }

    private static int ValidateLimit(int value, string parameterName, int maximum)
    {
        if (value is <= 0 || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"The value must be between 1 and {maximum}.");
        }

        return value;
    }
}

/// <summary>Content-free event emitted for a context-provider boundary decision or failure.</summary>
public sealed class MemoryContextProviderEvent
{
    public MemoryContextProviderEvent(
        MemoryContextProviderEventKind kind,
        string reasonCode,
        string providerFingerprint,
        string policyFingerprint)
    {
        Kind = MemoryValidation.Defined(kind, nameof(kind));
        if (!MemoryValidation.IsReasonCode(reasonCode))
        {
            throw new ArgumentException("A bounded machine-readable reason code is required.", nameof(reasonCode));
        }

        ReasonCode = reasonCode;
        ProviderFingerprint = MemoryDigest.Validate(providerFingerprint, nameof(providerFingerprint));
        PolicyFingerprint = MemoryDigest.Validate(policyFingerprint, nameof(policyFingerprint));
    }

    public MemoryContextProviderEventKind Kind { get; }
    public string ReasonCode { get; }
    public string ProviderFingerprint { get; }
    public string PolicyFingerprint { get; }
}

/// <summary>Receives bounded, content-free context-provider events.</summary>
public interface IMemoryContextProviderEventSink
{
    ValueTask RecordAsync(MemoryContextProviderEvent @event, CancellationToken cancellationToken = default);
}

/// <summary>Trusted host adapter for scoped context creation and safe message rewriting.</summary>
public interface IMemoryContextProviderAdapter
{
    MemoryGateContext CreateRecallInstructionsContext(
        AIContextProvider.InvokingContext context,
        string instructions);

    MemoryGateContext CreateRecallMessageContext(
        AIContextProvider.InvokingContext context,
        ChatMessage message,
        int ordinal);

    MemoryGateContext CreateWriteMessageContext(
        AIContextProvider.InvokedContext context,
        ChatMessage message,
        MemoryContextMessageOrigin origin,
        int ordinal);

    ChatMessage ApplySanitizedMessage(ChatMessage message, string sanitizedContent);
}

/// <summary>Safe context-provider failure that does not expose provider exceptions or content.</summary>
public sealed class MemoryContextProviderException : InvalidOperationException
{
    public MemoryContextProviderException(string reasonCode)
        : base($"Memory context provider operation failed safely ({ValidateReasonCode(reasonCode)}).")
        => ReasonCode = reasonCode;

    private static string ValidateReasonCode(string reasonCode)
    {
        if (reasonCode is null || !MemoryValidation.IsReasonCode(reasonCode))
        {
            throw new ArgumentException("A bounded machine-readable reason code is required.", nameof(reasonCode));
        }

        return reasonCode;
    }

    public string ReasonCode { get; }
}

/// <summary>
/// Decorates one MAF <see cref="AIContextProvider"/> and gates its complete recall and persistence
/// boundaries. Register this decorator instead of the inner provider, never as a sibling.
/// </summary>
public sealed class GatedAIContextProvider : AIContextProvider, IConfigurationFingerprintContributor
{
    private readonly AIContextProvider _inner;
    private readonly MemoryGatePipeline _pipeline;
    private readonly MemoryToolOperationRegistry _toolRegistry;
    private readonly IMemoryContextProviderAdapter _adapter;
    private readonly MemoryToolCallGate? _callGate;
    private readonly MemoryToolResultGate? _resultGate;
    private readonly MemoryInfluenceGate? _influenceGate;
    private readonly IMemoryGateDecisionSink? _decisionSink;
    private readonly IMemoryContextProviderEventSink? _eventSink;
    private readonly MemoryContextProviderOptions _options;
    private readonly MemoryGateDecisionExecutor _executor;
    private readonly IReadOnlyList<string> _stateKeys;
    private readonly string _providerFingerprint;

    public GatedAIContextProvider(
        AIContextProvider inner,
        MemoryGatePipeline pipeline,
        MemoryToolOperationRegistry toolRegistry,
        IMemoryContextProviderAdapter adapter,
        MemoryToolCallGate? callGate = null,
        MemoryToolResultGate? resultGate = null,
        MemoryInfluenceGate? influenceGate = null,
        IMemoryGateDecisionSink? decisionSink = null,
        IMemoryContextProviderEventSink? eventSink = null,
        MemoryContextProviderOptions? options = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _callGate = callGate;
        _resultGate = resultGate;
        _influenceGate = influenceGate;
        _decisionSink = decisionSink;
        _eventSink = eventSink;
        _options = options ?? new MemoryContextProviderOptions();
        _executor = new MemoryGateDecisionExecutor(pipeline.Capabilities);
        _providerFingerprint = MemoryDigest.Compute(inner.GetType().AssemblyQualifiedName ?? inner.GetType().FullName ?? inner.GetType().Name);
        _stateKeys = FreezeStateKeys(inner.StateKeys);

        if ((_options.RecallFailureMode is MemoryContextProviderFailureMode.ContinueWithoutProviderData ||
             _options.WriteFailureMode is MemoryContextProviderFailureMode.ContinueWithoutProviderData) &&
            eventSink is null)
        {
            throw new ArgumentException(
                "Continuing after a context-provider failure requires a content-free event sink.",
                nameof(eventSink));
        }
    }

    /// <summary>Forwards inner provider state ownership without decorator instance state.</summary>
    public override IReadOnlyList<string> StateKeys => _stateKeys;

    /// <summary>Gets the honest maximum coverage of this generic provider boundary.</summary>
    public MemoryCoverageLevel Coverage => _pipeline.Policy.Profile is MemorySecurityProfile.Observe
        ? MemoryCoverageLevel.ObserveOnly
        : MemoryCoverageLevel.Boundary;

    /// <summary>Gets the frozen memory policy fingerprint.</summary>
    public string PolicyFingerprint => _pipeline.PolicyFingerprint;

    /// <summary>Gets the content-free wrapped-provider type fingerprint.</summary>
    public string ProviderFingerprint => _providerFingerprint;

    string IConfigurationFingerprintContributor.ConfigurationFingerprintContribution
        => MemoryPolicyFingerprint.Compute(
            _providerFingerprint,
            _pipeline.PolicyFingerprint,
            _toolRegistry.ConfigurationFingerprint,
            _options.RecallFailureMode,
            _options.WriteFailureMode,
            _options.MaximumMessages,
            _options.MaximumTools,
            _stateKeys,
            _adapter.GetType().FullName);

    /// <inheritdoc />
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this)
            ? this
            : _inner.GetService(serviceType, serviceKey);
    }

    protected override async ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var input = SnapshotContext(context.AIContext, "memory.context.input_limit");
        AIContext provided;
        try
        {
#pragma warning disable MAAI001 // Required to preserve the exact MAF lifecycle while decorating it.
            var innerContext = new InvokingContext(context.Agent, context.Session, input);
#pragma warning restore MAAI001
            provided = await _inner.InvokingAsync(innerContext, cancellationToken).ConfigureAwait(false);
            provided = SnapshotContext(provided, "memory.context.recall_limit");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await RecordEventAsync(
                MemoryContextProviderEventKind.RecallProviderFailure,
                "memory.context.recall_provider_failed",
                cancellationToken).ConfigureAwait(false);
            if (_options.RecallFailureMode is MemoryContextProviderFailureMode.FailClosed)
            {
                throw new MemoryContextProviderException("memory.context.recall_provider_failed");
            }

            return input;
        }

        var inputMessages = input.Messages?.ToList() ?? [];
        var inputMessageReferences = new HashSet<ChatMessage>(inputMessages, ReferenceEqualityComparer.Instance);
        var outputMessages = new List<ChatMessage>();
        var ordinal = 0;
        foreach (var message in provided.Messages ?? [])
        {
            if (inputMessageReferences.Contains(message))
            {
                outputMessages.Add(message);
                continue;
            }

            var gated = await GateRecallMessageAsync(
                context,
                message,
                ordinal++,
                cancellationToken).ConfigureAwait(false);
            if (gated is not null)
            {
                outputMessages.Add(gated);
            }
        }

        var instructions = provided.Instructions;
        if (!string.Equals(instructions, input.Instructions, StringComparison.Ordinal) && instructions is not null)
        {
            instructions = await GateRecallInstructionsAsync(
                context,
                instructions,
                cancellationToken).ConfigureAwait(false);
        }

        var tools = await GateDynamicToolsAsync(
            input.Tools?.ToList() ?? [],
            provided.Tools?.ToList() ?? [],
            cancellationToken).ConfigureAwait(false);

        return new AIContext
        {
            Instructions = instructions,
            Messages = outputMessages,
            Tools = tools,
        };
    }

    protected override async ValueTask InvokedCoreAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.InvokeException is not null)
        {
            await InvokeInnerStoreAsync(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        var requests = MaterializeBounded(
            context.RequestMessages,
            _options.MaximumMessages,
            "memory.context.write_request_limit");
        var responses = MaterializeBounded(
            context.ResponseMessages ?? [],
            _options.MaximumMessages,
            "memory.context.write_response_limit");
        var gatedRequests = await GateWriteMessagesAsync(
            context,
            requests,
            MemoryContextMessageOrigin.Request,
            cancellationToken).ConfigureAwait(false);
        var gatedResponses = await GateWriteMessagesAsync(
            context,
            responses,
            MemoryContextMessageOrigin.Response,
            cancellationToken).ConfigureAwait(false);

#pragma warning disable MAAI001 // Required to delegate the filtered persistence boundary.
        var gatedContext = new InvokedContext(context.Agent, context.Session, gatedRequests, gatedResponses);
#pragma warning restore MAAI001
        await InvokeInnerStoreAsync(gatedContext, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ChatMessage?> GateRecallMessageAsync(
        InvokingContext context,
        ChatMessage message,
        int ordinal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (HasOpaqueContent(message))
        {
            await RecordEventAsync(
                MemoryContextProviderEventKind.RecallRejected,
                "memory.context.structured_message_unsupported",
                cancellationToken).ConfigureAwait(false);
            if (_pipeline.Policy.Profile is MemorySecurityProfile.Observe)
            {
                return message;
            }

            if (_options.RecallFailureMode is MemoryContextProviderFailureMode.FailClosed)
            {
                throw new MemoryContextProviderException("memory.context.structured_message_unsupported");
            }

            return null;
        }

        var gateContext = _adapter.CreateRecallMessageContext(context, message, ordinal)
            ?? throw new InvalidOperationException("The memory context adapter returned a null recall context.");
        ValidateContext(gateContext, MemoryGateStage.AfterRead, MemorySurface.AIContextProvider);
        var decision = await _pipeline.EvaluateAsync(gateContext, cancellationToken).ConfigureAwait(false);
        await RecordDecisionAsync(gateContext, decision, cancellationToken).ConfigureAwait(false);

        if (decision.Profile is MemorySecurityProfile.Observe)
        {
            return message;
        }

        if (decision.Action is MemoryGateAction.Allow)
        {
            return ApplySanitizedMessage(message, message.Text);
        }

        if (decision.Action is MemoryGateAction.Sanitize)
        {
            return ApplySanitizedMessage(
                message,
                decision.EffectiveContent
                    ?? throw new InvalidOperationException("A sanitize decision has no effective content."));
        }

        await RecordEventAsync(
            MemoryContextProviderEventKind.RecallRejected,
            decision.ReasonCode,
            cancellationToken).ConfigureAwait(false);
        if (decision.Action is MemoryGateAction.Exclude)
        {
            return null;
        }

        throw new MemoryContextProviderException(decision.ReasonCode);
    }

    private async ValueTask<string?> GateRecallInstructionsAsync(
        InvokingContext context,
        string instructions,
        CancellationToken cancellationToken)
    {
        var gateContext = _adapter.CreateRecallInstructionsContext(context, instructions)
            ?? throw new InvalidOperationException("The memory context adapter returned a null instruction context.");
        ValidateContext(gateContext, MemoryGateStage.AfterRead, MemorySurface.AIContextProvider);
        var decision = await _pipeline.EvaluateAsync(gateContext, cancellationToken).ConfigureAwait(false);
        await RecordDecisionAsync(gateContext, decision, cancellationToken).ConfigureAwait(false);

        if (decision.Profile is MemorySecurityProfile.Observe || decision.Action is MemoryGateAction.Allow)
        {
            return instructions;
        }

        if (decision.Action is MemoryGateAction.Sanitize)
        {
            return decision.EffectiveContent
                ?? throw new InvalidOperationException("A sanitize decision has no effective content.");
        }

        await RecordEventAsync(
            MemoryContextProviderEventKind.RecallRejected,
            decision.ReasonCode,
            cancellationToken).ConfigureAwait(false);
        if (decision.Action is MemoryGateAction.Exclude)
        {
            return null;
        }

        throw new MemoryContextProviderException(decision.ReasonCode);
    }

    private async ValueTask<IReadOnlyList<AITool>> GateDynamicToolsAsync(
        IReadOnlyList<AITool> inputTools,
        IReadOnlyList<AITool> outputTools,
        CancellationToken cancellationToken)
    {
        var inputReferences = new HashSet<AITool>(inputTools, ReferenceEqualityComparer.Instance);
        var additions = outputTools.Where(tool => !inputReferences.Contains(tool)).ToList();
        var report = MemoryToolCoverageAnalyzer.Analyze(
            additions,
            _toolRegistry,
            _callGate,
            _resultGate,
            _influenceGate);
        var unsupportedNames = report.Entries
            .Where(entry =>
                entry.Coverage is MemoryCoverageLevel.Unsupported ||
                entry.Classification is MemoryToolClassification.UnclassifiedMemoryLike ||
                entry.Coverage < _pipeline.Policy.MinimumCoverage)
            .Select(entry => entry.ToolName)
            .ToHashSet(StringComparer.Ordinal);

        if (unsupportedNames.Count == 0 || _pipeline.Policy.Profile is MemorySecurityProfile.Observe)
        {
            if (unsupportedNames.Count > 0)
            {
                await RecordEventAsync(
                    MemoryContextProviderEventKind.DynamicToolExcluded,
                    "memory.context.dynamic_tool_would_exclude",
                    cancellationToken).ConfigureAwait(false);
            }

            return outputTools;
        }

        await RecordEventAsync(
            MemoryContextProviderEventKind.DynamicToolExcluded,
            "memory.context.dynamic_tool_unsupported",
            cancellationToken).ConfigureAwait(false);
        if (_options.RecallFailureMode is MemoryContextProviderFailureMode.FailClosed)
        {
            throw new MemoryContextProviderException("memory.context.dynamic_tool_unsupported");
        }

        return outputTools.Where(tool => !unsupportedNames.Contains(tool.Name)).ToArray();
    }

    private async ValueTask<IReadOnlyList<ChatMessage>> GateWriteMessagesAsync(
        InvokedContext context,
        IReadOnlyList<ChatMessage> messages,
        MemoryContextMessageOrigin origin,
        CancellationToken cancellationToken)
    {
        var result = new List<ChatMessage>(messages.Count);
        for (var ordinal = 0; ordinal < messages.Count; ordinal++)
        {
            var message = messages[ordinal];
            if (HasOpaqueContent(message))
            {
                await RecordEventAsync(
                    MemoryContextProviderEventKind.WriteExcluded,
                    "memory.context.structured_message_unsupported",
                    cancellationToken).ConfigureAwait(false);
                if (_pipeline.Policy.Profile is MemorySecurityProfile.Observe)
                {
                    result.Add(message);
                }

                continue;
            }

            var gateContext = _adapter.CreateWriteMessageContext(context, message, origin, ordinal)
                ?? throw new InvalidOperationException("The memory context adapter returned a null write context.");
            ValidateContext(gateContext, MemoryGateStage.BeforeWrite, MemorySurface.AIContextProvider);
            var decision = await _pipeline.EvaluateAsync(gateContext, cancellationToken).ConfigureAwait(false);
            await RecordDecisionAsync(gateContext, decision, cancellationToken).ConfigureAwait(false);
            var enforcement = await _executor.ExecuteAsync(gateContext, decision, cancellationToken).ConfigureAwait(false);
            if (!enforcement.Allowed)
            {
                await RecordEventAsync(
                    MemoryContextProviderEventKind.WriteExcluded,
                    decision.ReasonCode,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            result.Add(decision.Profile is MemorySecurityProfile.Observe
                ? message
                : ApplySanitizedMessage(
                    message,
                    decision.ShouldApplySanitizedContent
                        ? enforcement.EffectiveContent
                            ?? throw new InvalidOperationException("An applied sanitize decision has no content.")
                        : message.Text));
        }

        return result;
    }

    private async ValueTask InvokeInnerStoreAsync(
        InvokedContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await _inner.InvokedAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await RecordEventAsync(
                MemoryContextProviderEventKind.WriteProviderFailure,
                "memory.context.write_provider_failed",
                cancellationToken).ConfigureAwait(false);
            if (_options.WriteFailureMode is MemoryContextProviderFailureMode.FailClosed)
            {
                throw new MemoryContextProviderException("memory.context.write_provider_failed");
            }
        }
    }

    private ChatMessage ApplySanitizedMessage(ChatMessage message, string sanitizedContent)
    {
        var sanitized = _adapter.ApplySanitizedMessage(message, sanitizedContent)
            ?? throw new InvalidOperationException("The memory context adapter returned a null sanitized message.");
        if (!string.Equals(sanitized.Text, sanitizedContent, StringComparison.Ordinal) ||
            sanitized.RawRepresentation is not null ||
            sanitized.Contents.Count != 1 ||
            sanitized.Contents[0] is not TextContent { RawRepresentation: null } ||
            sanitized.GetAgentRequestMessageSourceType() != message.GetAgentRequestMessageSourceType() ||
            !string.Equals(
                sanitized.GetAgentRequestMessageSourceId(),
                message.GetAgentRequestMessageSourceId(),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The memory context adapter returned an unsafe or attribution-changing sanitized message.");
        }

        return sanitized;
    }

    private static bool HasOpaqueContent(ChatMessage message)
        => message.Contents.Any(content => content is not TextContent);

    private async ValueTask RecordDecisionAsync(
        MemoryGateContext context,
        MemoryGateDecision decision,
        CancellationToken cancellationToken)
    {
        if (_decisionSink is not null)
        {
            await _decisionSink.RecordAsync(context, decision, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask RecordEventAsync(
        MemoryContextProviderEventKind kind,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        if (_eventSink is not null)
        {
            await _eventSink.RecordAsync(
                new MemoryContextProviderEvent(
                    kind,
                    reasonCode,
                    _providerFingerprint,
                    _pipeline.PolicyFingerprint),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private AIContext SnapshotContext(AIContext context, string reasonCode)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new AIContext
        {
            Instructions = MemoryValidation.OptionalContent(context.Instructions, nameof(context.Instructions)),
            Messages = context.Messages is null
                ? null
                : MaterializeBounded(context.Messages, _options.MaximumMessages, reasonCode),
            Tools = context.Tools is null
                ? null
                : MaterializeBounded(context.Tools, _options.MaximumTools, reasonCode),
        };
    }

    private static IReadOnlyList<T> MaterializeBounded<T>(
        IEnumerable<T> values,
        int maximum,
        string reasonCode)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = new List<T>(Math.Min(maximum, 256));
        foreach (var value in values)
        {
            if (value is null || result.Count == maximum)
            {
                throw new MemoryContextProviderException(reasonCode);
            }

            result.Add(value);
        }

        return result;
    }

    private static IReadOnlyList<string> FreezeStateKeys(IReadOnlyList<string> stateKeys)
    {
        ArgumentNullException.ThrowIfNull(stateKeys);
        if (stateKeys.Count > 64)
        {
            throw new ArgumentException("A context provider may declare at most 64 state keys.", nameof(stateKeys));
        }

        var keys = stateKeys
            .Select(key => MemoryValidation.Identifier(key, nameof(stateKeys)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (keys.Length != stateKeys.Count)
        {
            throw new ArgumentException("Context-provider state keys must be unique.", nameof(stateKeys));
        }

        return new ReadOnlyCollection<string>(keys);
    }

    private static void ValidateContext(
        MemoryGateContext context,
        MemoryGateStage expectedStage,
        MemorySurface expectedSurface)
    {
        if (context.Stage != expectedStage || context.Surface != expectedSurface)
        {
            throw new InvalidOperationException(
                "The trusted memory context adapter returned a context for a different surface or stage.");
        }

        if (expectedStage is MemoryGateStage.AfterRead &&
            context.Kind is not (MemoryOperationKind.Search or MemoryOperationKind.Recall))
        {
            throw new InvalidOperationException("A context-provider recall adapter must create a read operation.");
        }

        if (expectedStage is MemoryGateStage.BeforeWrite &&
            context.Kind is MemoryOperationKind.Search or MemoryOperationKind.Recall or MemoryOperationKind.Audit)
        {
            throw new InvalidOperationException("A context-provider persistence adapter must create a write operation.");
        }
    }
}

/// <summary>Inspectable provider-native lifecycle capabilities.</summary>
[Flags]
public enum MemoryProviderNativeCapabilities
{
    None = 0,
    CandidateWrites = 1 << 0,
    RecalledItems = 1 << 1,
}

/// <summary>Immutable identity and capability manifest for a provider-native hook.</summary>
public sealed class MemoryProviderNativeDescriptor
{
    public MemoryProviderNativeDescriptor(
        string providerId,
        string version,
        MemoryProviderNativeCapabilities capabilities)
    {
        ProviderId = MemoryValidation.Identifier(providerId, nameof(providerId));
        Version = MemoryValidation.Identifier(version, nameof(version));
        const MemoryProviderNativeCapabilities known =
            MemoryProviderNativeCapabilities.CandidateWrites |
            MemoryProviderNativeCapabilities.RecalledItems;
        if (capabilities is MemoryProviderNativeCapabilities.None || (capabilities & ~known) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capabilities),
                "At least one known provider-native capability is required.");
        }

        Capabilities = capabilities;
    }

    public string ProviderId { get; }
    public string Version { get; }
    public MemoryProviderNativeCapabilities Capabilities { get; }
    public string ConfigurationFingerprint
        => MemoryPolicyFingerprint.Compute(ProviderId, Version, Capabilities);
}

/// <summary>Applied provider-native recall decision with transient content excluded from serialization.</summary>
public sealed class MemoryProviderNativeRecallResult
{
    internal MemoryProviderNativeRecallResult(
        bool include,
        string? effectiveContent,
        IReadOnlyList<MemoryGateReceipt> receipts)
    {
        Include = include;
        EffectiveContent = effectiveContent;
        Receipts = receipts;
    }

    public bool Include { get; }
    [JsonIgnore]
    public string? EffectiveContent { get; }
    public IReadOnlyList<MemoryGateReceipt> Receipts { get; }
}

/// <summary>Provider-owned hook immediately before candidate commit and recalled-item formatting.</summary>
public interface IMemoryProviderNativeGate
{
    MemoryProviderNativeDescriptor Descriptor { get; }

    ValueTask<MemoryGateEnforcementResult> GateCandidateWriteAsync(
        MemoryGateContext context,
        CancellationToken cancellationToken = default);

    ValueTask<MemoryProviderNativeRecallResult> GateRecalledItemAsync(
        MemoryGateContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Provider-native hook backed by the same deterministic memory-gate pipeline.</summary>
public sealed class MemoryProviderNativeGate : IMemoryProviderNativeGate, IConfigurationFingerprintContributor
{
    private readonly MemoryGatePipeline _pipeline;
    private readonly MemoryGateDecisionExecutor _executor;
    private readonly IMemoryGateDecisionSink? _decisionSink;

    public MemoryProviderNativeGate(
        MemoryProviderNativeDescriptor descriptor,
        MemoryGatePipeline pipeline,
        IMemoryGateDecisionSink? decisionSink = null)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _executor = new MemoryGateDecisionExecutor(pipeline.Capabilities);
        _decisionSink = decisionSink;
    }

    public MemoryProviderNativeDescriptor Descriptor { get; }
    public string PolicyFingerprint => _pipeline.PolicyFingerprint;
    string IConfigurationFingerprintContributor.ConfigurationFingerprintContribution
        => MemoryPolicyFingerprint.Compute(Descriptor.ConfigurationFingerprint, _pipeline.PolicyFingerprint);

    public async ValueTask<MemoryGateEnforcementResult> GateCandidateWriteAsync(
        MemoryGateContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateNativeContext(
            context,
            MemoryProviderNativeCapabilities.CandidateWrites,
            MemoryGateStage.BeforeWrite);
        var decision = await _pipeline.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
        await RecordDecisionAsync(context, decision, cancellationToken).ConfigureAwait(false);
        return await _executor.ExecuteAsync(context, decision, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<MemoryProviderNativeRecallResult> GateRecalledItemAsync(
        MemoryGateContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateNativeContext(
            context,
            MemoryProviderNativeCapabilities.RecalledItems,
            MemoryGateStage.AfterRead);
        var decision = await _pipeline.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
        await RecordDecisionAsync(context, decision, cancellationToken).ConfigureAwait(false);
        var include = decision.Profile is MemorySecurityProfile.Observe ||
            decision.Action is MemoryGateAction.Allow or MemoryGateAction.Sanitize;
        return new MemoryProviderNativeRecallResult(
            include,
            include
                ? decision.Profile is MemorySecurityProfile.Observe
                    ? context.Content
                    : decision.EffectiveContent
                : null,
            decision.Receipts);
    }

    private async ValueTask RecordDecisionAsync(
        MemoryGateContext context,
        MemoryGateDecision decision,
        CancellationToken cancellationToken)
    {
        if (_decisionSink is not null)
        {
            await _decisionSink.RecordAsync(context, decision, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ValidateNativeContext(
        MemoryGateContext context,
        MemoryProviderNativeCapabilities requiredCapability,
        MemoryGateStage stage)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Descriptor.Capabilities.HasFlag(requiredCapability))
        {
            throw new InvalidOperationException(
                $"Provider '{Descriptor.ProviderId}' did not declare the required native hook capability.");
        }

        if (!string.Equals(context.ProviderId, Descriptor.ProviderId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The provider-native hook received a context for a different provider identity.");
        }

        if (context.Surface is not MemorySurface.ProviderNative || context.Stage != stage)
        {
            throw new InvalidOperationException(
                "The provider-native hook received a context for a different surface or stage.");
        }
    }
}
