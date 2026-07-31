// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Buffers;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Gatekeeper.Memory;

internal static class MemoryMcpValidation
{
    internal const int MaximumOperations = 256;
    internal const int MaximumCoverageEntries = 512;

    internal static T[] Snapshot<T>(
        IEnumerable<T> values,
        string parameterName,
        int maximum = MaximumOperations)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var snapshot = values.Take(maximum + 1).ToArray();
        if (snapshot.Length > maximum)
        {
            throw new ArgumentException(
                $"MCP operation inventories cannot exceed {maximum} entries.",
                parameterName);
        }

        if (snapshot.Any(value => value is null))
        {
            throw new ArgumentException("MCP operation inventories cannot contain null entries.", parameterName);
        }

        return snapshot;
    }
}

/// <summary>Transport category pinned by a reviewed MCP memory-operation contract.</summary>
public enum MemoryMcpTransport
{
    InProcess,
    Stdio,
    StreamableHttp,
    ProviderHosted,
}

/// <summary>Provider-side approval behavior for a hosted MCP operation.</summary>
public enum MemoryHostedMcpApprovalMode
{
    Never,
    Always,
    ProviderDefault,
}

/// <summary>Inspectable provider callbacks available around a hosted MCP operation.</summary>
[Flags]
public enum MemoryHostedMcpCallbackCapabilities
{
    None = 0,
    CallArguments = 1,
    ResultContent = 2,
    TrustedScope = 4,
    PreExecutionApproval = 8,
    DerivedActions = 16,
}

/// <summary>Creates stable SHA-256 fingerprints from bounded JSON MCP schemas.</summary>
public static class MemoryMcpSchema
{
    /// <summary>Maximum accepted schema source size before canonicalization.</summary>
    public const int MaximumSchemaCharacters = 65_536;

    /// <summary>Canonicalizes object-property order and hashes bounded JSON.</summary>
    public static string ComputeFingerprint(string schemaJson)
    {
        ArgumentNullException.ThrowIfNull(schemaJson);
        if (schemaJson.Length is 0 or > MaximumSchemaCharacters)
        {
            throw new ArgumentException(
                $"MCP schemas must contain between 1 and {MaximumSchemaCharacters} characters.",
                nameof(schemaJson));
        }

        using var document = JsonDocument.Parse(
            schemaJson,
            new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = 64,
            });
        if (document.RootElement.ValueKind is not JsonValueKind.Object)
        {
            throw new ArgumentException("An MCP schema root must be a JSON object.", nameof(schemaJson));
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCanonical(document.RootElement, writer, nameof(schemaJson));
        }

        return MemoryDigest.Compute(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer, string parameterName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    writer.WriteStartObject();
                    var properties = element.EnumerateObject().ToArray();
                    if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() !=
                        properties.Length)
                    {
                        throw new ArgumentException(
                            "MCP schemas cannot contain duplicate JSON property names.",
                            parameterName);
                    }

                    foreach (var property in properties.OrderBy(property => property.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property.Name);
                        WriteCanonical(property.Value, writer, parameterName);
                    }

                    writer.WriteEndObject();
                    break;
                }

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer, parameterName);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }
}

/// <summary>Reviewed client-side contract for one local MCP memory tool.</summary>
public sealed class MemoryMcpClientOperationContract
{
    public MemoryMcpClientOperationContract(
        string serverId,
        string serverVersion,
        MemoryMcpTransport transport,
        string schemaFingerprint,
        MemoryOperationContract operation)
    {
        ServerId = MemoryValidation.Identifier(serverId, nameof(serverId));
        ServerVersion = MemoryValidation.Identifier(serverVersion, nameof(serverVersion));
        Transport = MemoryValidation.Defined(transport, nameof(transport));
        if (Transport is MemoryMcpTransport.ProviderHosted)
        {
            throw new ArgumentException(
                "Local MCP client contracts cannot use the provider-hosted transport.",
                nameof(transport));
        }

        SchemaFingerprint = MemoryDigest.Validate(schemaFingerprint, nameof(schemaFingerprint));
        Operation = operation ?? throw new ArgumentNullException(nameof(operation));
        if (Operation.Surface is not MemorySurface.LocalMcp)
        {
            throw new ArgumentException(
                "Local MCP client contracts require a LocalMcp memory operation.",
                nameof(operation));
        }
    }

    public string ServerId { get; }
    public string ServerVersion { get; }
    public MemoryMcpTransport Transport { get; }
    public string SchemaFingerprint { get; }
    public MemoryOperationContract Operation { get; }
}

/// <summary>Immutable exact-name registry for reviewed local MCP memory tools.</summary>
public sealed class MemoryMcpClientOperationRegistry
{
    private readonly IReadOnlyDictionary<string, MemoryMcpClientOperationContract> _contracts;

    public MemoryMcpClientOperationRegistry(IEnumerable<MemoryMcpClientOperationContract> contracts)
    {
        var bounded = MemoryMcpValidation.Snapshot(contracts, nameof(contracts));
        var snapshot = new Dictionary<string, MemoryMcpClientOperationContract>(StringComparer.Ordinal);
        foreach (var contract in bounded)
        {
            ArgumentNullException.ThrowIfNull(contract);
            if (!snapshot.TryAdd(contract.Operation.OperationName, contract))
            {
                throw new ArgumentException(
                    $"Duplicate local MCP memory operation '{contract.Operation.OperationName}'.",
                    nameof(contracts));
            }
        }

        _contracts = new ReadOnlyDictionary<string, MemoryMcpClientOperationContract>(snapshot);
        OperationRegistry = new MemoryToolOperationRegistry(snapshot.Values.Select(value => value.Operation));
        ConfigurationFingerprint = MemoryPolicyFingerprint.Compute(
            OperationRegistry.ConfigurationFingerprint,
            snapshot.Values
                .OrderBy(value => value.Operation.OperationName, StringComparer.Ordinal)
                .Select(value => string.Join(
                    ":",
                    value.ServerId,
                    value.ServerVersion,
                    (int)value.Transport,
                    value.SchemaFingerprint,
                    value.Operation.OperationName))
                .ToArray());
    }

    public IReadOnlyDictionary<string, MemoryMcpClientOperationContract> Contracts => _contracts;
    public MemoryToolOperationRegistry OperationRegistry { get; }
    public string ConfigurationFingerprint { get; }

    public bool TryGet(string operationName, out MemoryMcpClientOperationContract contract)
    {
        ArgumentNullException.ThrowIfNull(operationName);
        return _contracts.TryGetValue(operationName, out contract!);
    }
}

/// <summary>Runtime identity observed for a local MCP AIFunction.</summary>
public sealed class MemoryMcpClientToolBinding
{
    public MemoryMcpClientToolBinding(
        AITool tool,
        string serverId,
        string serverVersion,
        MemoryMcpTransport transport,
        string schemaFingerprint)
    {
        Tool = tool ?? throw new ArgumentNullException(nameof(tool));
        ServerId = MemoryValidation.Identifier(serverId, nameof(serverId));
        ServerVersion = MemoryValidation.Identifier(serverVersion, nameof(serverVersion));
        Transport = MemoryValidation.Defined(transport, nameof(transport));
        if (Transport is MemoryMcpTransport.ProviderHosted)
        {
            throw new ArgumentException(
                "Local MCP bindings cannot use the provider-hosted transport.",
                nameof(transport));
        }

        SchemaFingerprint = MemoryDigest.Validate(schemaFingerprint, nameof(schemaFingerprint));
    }

    [JsonIgnore]
    public AITool Tool { get; }
    public string ServerId { get; }
    public string ServerVersion { get; }
    public MemoryMcpTransport Transport { get; }
    public string SchemaFingerprint { get; }

    /// <summary>Creates a binding by hashing the actual AIFunction JSON schema.</summary>
    public static MemoryMcpClientToolBinding FromFunction(
        AIFunction tool,
        string serverId,
        string serverVersion,
        MemoryMcpTransport transport)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return new MemoryMcpClientToolBinding(
            tool,
            serverId,
            serverVersion,
            transport,
            MemoryMcpSchema.ComputeFingerprint(tool.JsonSchema.GetRawText()));
    }
}

/// <summary>Reviewed contract for one owned MCP server operation.</summary>
public sealed class MemoryMcpServerOperationContract
{
    public MemoryMcpServerOperationContract(string schemaFingerprint, MemoryOperationContract operation)
    {
        SchemaFingerprint = MemoryDigest.Validate(schemaFingerprint, nameof(schemaFingerprint));
        Operation = operation ?? throw new ArgumentNullException(nameof(operation));
        if (Operation.Surface is not MemorySurface.McpServer)
        {
            throw new ArgumentException(
                "Owned MCP server contracts require an McpServer memory operation.",
                nameof(operation));
        }
    }

    public string SchemaFingerprint { get; }
    public MemoryOperationContract Operation { get; }
}

/// <summary>Frozen operation and identity registry for one owned MCP server.</summary>
public sealed class MemoryMcpServerOperationRegistry
{
    private readonly IReadOnlyDictionary<string, MemoryMcpServerOperationContract> _contracts;

    public MemoryMcpServerOperationRegistry(
        string serverId,
        string serverVersion,
        MemoryMcpTransport transport,
        IEnumerable<MemoryMcpServerOperationContract> contracts)
    {
        ServerId = MemoryValidation.Identifier(serverId, nameof(serverId));
        ServerVersion = MemoryValidation.Identifier(serverVersion, nameof(serverVersion));
        Transport = MemoryValidation.Defined(transport, nameof(transport));
        if (Transport is MemoryMcpTransport.ProviderHosted)
        {
            throw new ArgumentException(
                "Owned MCP servers cannot use the provider-hosted transport.",
                nameof(transport));
        }

        var bounded = MemoryMcpValidation.Snapshot(contracts, nameof(contracts));
        var snapshot = new Dictionary<string, MemoryMcpServerOperationContract>(StringComparer.Ordinal);
        foreach (var contract in bounded)
        {
            ArgumentNullException.ThrowIfNull(contract);
            if (!snapshot.TryAdd(contract.Operation.OperationName, contract))
            {
                throw new ArgumentException(
                    $"Duplicate owned MCP memory operation '{contract.Operation.OperationName}'.",
                    nameof(contracts));
            }
        }

        _contracts = new ReadOnlyDictionary<string, MemoryMcpServerOperationContract>(snapshot);
        ConfigurationFingerprint = MemoryPolicyFingerprint.Compute(
            ServerId,
            ServerVersion,
            ((int)Transport).ToString(),
            snapshot.Values
                .OrderBy(value => value.Operation.OperationName, StringComparer.Ordinal)
                .Select(value => string.Join(
                    ":",
                    value.Operation.OperationName,
                    (int)value.Operation.Kind,
                    (int)value.Operation.Category,
                    value.Operation.IsSideEffecting,
                    value.Operation.MayReturnSensitiveContent,
                    string.Join(",", value.Operation.ContentArguments),
                    string.Join(",", value.Operation.ScopeArguments),
                    value.SchemaFingerprint))
                .ToArray());
    }

    public string ServerId { get; }
    public string ServerVersion { get; }
    public MemoryMcpTransport Transport { get; }
    public IReadOnlyDictionary<string, MemoryMcpServerOperationContract> Contracts => _contracts;
    public string ConfigurationFingerprint { get; }

    public bool TryGet(string operationName, out MemoryMcpServerOperationContract contract)
    {
        ArgumentNullException.ThrowIfNull(operationName);
        return _contracts.TryGetValue(operationName, out contract!);
    }
}

/// <summary>Supplies a content-free fingerprint for trusted MCP adapter configuration.</summary>
public interface IMemoryMcpConfigurationFingerprintContributor
{
    string ConfigurationFingerprint { get; }
}

/// <summary>Trusted adapter between an owned MCP transport model and memory contexts.</summary>
public interface IMemoryMcpServerContextAdapter<TRequest, TResult>
{
    MemoryGateContext CreateRequestContext(
        TRequest request,
        MemoryMcpServerOperationContract operation,
        MemoryGateStage stage);

    TRequest ApplySanitizedRequest(
        TRequest request,
        MemoryMcpServerOperationContract operation,
        string sanitizedContent);

    MemoryGateContext CreateResultContext(
        TRequest request,
        TResult result,
        MemoryMcpServerOperationContract operation);

    TResult ApplySanitizedResult(
        TResult result,
        MemoryMcpServerOperationContract operation,
        string sanitizedContent);
}

/// <summary>Content-free, client-safe MCP error with no provider exception chain.</summary>
public sealed class MemoryMcpSafeException : InvalidOperationException
{
    internal MemoryMcpSafeException(string reasonCode, string correlationId)
        : base($"MCP memory operation failed. Code={reasonCode}; CorrelationId={correlationId}.")
    {
        if (!MemoryValidation.IsReasonCode(reasonCode))
        {
            throw new ArgumentException(
                "Reason codes must be bounded machine-readable identifiers.",
                nameof(reasonCode));
        }

        ReasonCode = reasonCode;
        CorrelationId = MemoryValidation.Identifier(correlationId, nameof(correlationId));
    }

    public string ReasonCode { get; }
    public string CorrelationId { get; }
}

/// <summary>Coverage evidence emitted only by an owned server gate.</summary>
public sealed class MemoryMcpServerCoverageEvidence
{
    private readonly IReadOnlyDictionary<string, MemoryMcpServerOperationContract> _operations;
    private readonly IReadOnlyList<IMemoryGate> _gates;

    internal MemoryMcpServerCoverageEvidence(
        MemoryMcpServerOperationRegistry registry,
        MemoryGatePipeline pipeline,
        string? adapterConfigurationFingerprint)
    {
        ServerId = registry.ServerId;
        ServerVersion = registry.ServerVersion;
        Transport = registry.Transport;
        RegistryFingerprint = registry.ConfigurationFingerprint;
        PipelineFingerprint = pipeline.PolicyFingerprint;
        Profile = pipeline.Policy.Profile;
        AdapterConfigurationFingerprint = adapterConfigurationFingerprint;
        _operations = registry.Contracts;
        _gates = pipeline.Gates;
        ConfigurationFingerprint = MemoryPolicyFingerprint.Compute(
            ServerId,
            ServerVersion,
            ((int)Transport).ToString(),
            RegistryFingerprint,
            PipelineFingerprint,
            AdapterConfigurationFingerprint,
            ((int)Profile).ToString());
    }

    public string ServerId { get; }
    public string ServerVersion { get; }
    public MemoryMcpTransport Transport { get; }
    public string RegistryFingerprint { get; }
    public string PipelineFingerprint { get; }
    public MemorySecurityProfile Profile { get; }
    public string? AdapterConfigurationFingerprint { get; }
    public string ConfigurationFingerprint { get; }

    internal bool Covers(MemoryMcpClientOperationContract contract)
        => Profile is MemorySecurityProfile.Enforce &&
           AdapterConfigurationFingerprint is not null &&
           string.Equals(ServerId, contract.ServerId, StringComparison.Ordinal) &&
           string.Equals(ServerVersion, contract.ServerVersion, StringComparison.Ordinal) &&
           Transport == contract.Transport &&
           _operations.TryGetValue(contract.Operation.OperationName, out var serverOperation) &&
           SameSemantics(serverOperation.Operation, contract.Operation) &&
           HasRequiredStages(serverOperation.Operation.Kind) &&
           string.Equals(
               serverOperation.SchemaFingerprint,
               contract.SchemaFingerprint,
               StringComparison.Ordinal);

    internal bool Covers(MemoryHostedMcpOperationContract contract)
        => Profile is MemorySecurityProfile.Enforce &&
           AdapterConfigurationFingerprint is not null &&
           string.Equals(ServerId, contract.ServerId, StringComparison.Ordinal) &&
           string.Equals(ServerVersion, contract.ServerVersion, StringComparison.Ordinal) &&
           _operations.TryGetValue(contract.Operation.OperationName, out var serverOperation) &&
           SameSemantics(serverOperation.Operation, contract.Operation) &&
           HasRequiredStages(serverOperation.Operation.Kind) &&
           string.Equals(
               serverOperation.SchemaFingerprint,
               contract.SchemaFingerprint,
               StringComparison.Ordinal);

    private bool HasRequiredStages(MemoryOperationKind kind)
    {
        var stages = MemoryToolCallGate.CallStages(kind).AsEnumerable();
        if (kind is MemoryOperationKind.Search or MemoryOperationKind.Recall)
        {
            stages = stages.Append(MemoryGateStage.AfterRead);
        }

        return stages.All(stage => _gates.Any(gate => (gate.Stages & stage) != 0));
    }

    private static bool SameSemantics(
        MemoryOperationContract server,
        MemoryOperationContract client)
        => server.Kind == client.Kind &&
           server.Category == client.Category &&
           server.IsSideEffecting == client.IsSideEffecting &&
           server.MayReturnSensitiveContent == client.MayReturnSensitiveContent &&
           server.ContentArguments.SequenceEqual(client.ContentArguments, StringComparer.Ordinal) &&
           server.ScopeArguments.SequenceEqual(client.ScopeArguments, StringComparer.Ordinal);
}

/// <summary>
/// Gates an owned MCP server immediately before service/repository access and gates recalled content
/// before the transport serializes it.
/// </summary>
public sealed class MemoryMcpServerGate<TRequest, TResult> : IConfigurationFingerprintContributor
{
    private readonly MemoryGatePipeline _pipeline;
    private readonly MemoryMcpServerOperationRegistry _registry;
    private readonly IMemoryMcpServerContextAdapter<TRequest, TResult> _adapter;
    private readonly MemoryGateDecisionExecutor _executor;
    private readonly IMemoryGateDecisionSink? _decisionSink;

    public MemoryMcpServerGate(
        MemoryGatePipeline pipeline,
        MemoryMcpServerOperationRegistry registry,
        IMemoryMcpServerContextAdapter<TRequest, TResult> adapter,
        IMemoryGateDecisionSink? decisionSink = null)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _executor = new MemoryGateDecisionExecutor(pipeline.Capabilities);
        _decisionSink = decisionSink;
        var adapterFingerprint = adapter is IMemoryMcpConfigurationFingerprintContributor contributor
            ? MemoryDigest.Validate(
                contributor.ConfigurationFingerprint,
                nameof(IMemoryMcpConfigurationFingerprintContributor.ConfigurationFingerprint))
            : null;
        CoverageEvidence = new MemoryMcpServerCoverageEvidence(
            registry,
            pipeline,
            adapterFingerprint);
        ConfigurationFingerprint = MemoryPolicyFingerprint.Compute(
            pipeline.PolicyFingerprint,
            registry.ConfigurationFingerprint,
            adapter.GetType().FullName,
            adapterFingerprint,
            decisionSink?.GetType().FullName);
    }

    public MemoryMcpServerCoverageEvidence CoverageEvidence { get; }
    public string ConfigurationFingerprint { get; }
    string IConfigurationFingerprintContributor.ConfigurationFingerprintContribution
        => ConfigurationFingerprint;

    public async ValueTask<TResult> ExecuteAsync(
        string operationName,
        string correlationId,
        TRequest request,
        Func<TRequest, CancellationToken, ValueTask<TResult>> invokeBackend,
        CancellationToken cancellationToken = default)
    {
        operationName = MemoryValidation.Identifier(operationName, nameof(operationName));
        correlationId = MemoryValidation.Identifier(correlationId, nameof(correlationId));
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(invokeBackend);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_registry.TryGet(operationName, out var operation))
        {
            throw new MemoryMcpSafeException("memory.mcp.contract_mismatch", correlationId);
        }

        TRequest effectiveRequest = request;
        try
        {
            foreach (var stage in MemoryToolCallGate.CallStages(operation.Operation.Kind))
            {
                var context = _adapter.CreateRequestContext(effectiveRequest, operation, stage);
                ValidateContext(context, operation, stage);
                var decision = await EvaluateAndRecordAsync(context, cancellationToken).ConfigureAwait(false);
                var enforcement = await _executor.ExecuteAsync(
                    context,
                    decision,
                    cancellationToken).ConfigureAwait(false);
                if (!enforcement.Allowed)
                {
                    throw new MemoryMcpSafeException("memory.mcp.request_denied", correlationId);
                }

                if (decision.ShouldApplySanitizedContent)
                {
                    effectiveRequest = _adapter.ApplySanitizedRequest(
                        effectiveRequest,
                        operation,
                        enforcement.EffectiveContent
                            ?? throw new InvalidOperationException(
                                "An applied MCP request sanitization has no content."));
                    if (effectiveRequest is null)
                    {
                        throw new InvalidOperationException(
                            "The MCP server adapter returned a null sanitized request.");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MemoryMcpSafeException)
        {
            throw;
        }
        catch
        {
            throw new MemoryMcpSafeException("memory.mcp.adapter_failure", correlationId);
        }

        TResult result;
        try
        {
            result = await invokeBackend(effectiveRequest, cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                throw new InvalidOperationException("The MCP memory backend returned a null result.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new MemoryMcpSafeException("memory.mcp.backend_failure", correlationId);
        }

        if (operation.Operation.Kind is not (MemoryOperationKind.Search or MemoryOperationKind.Recall))
        {
            return result;
        }

        try
        {
            var context = _adapter.CreateResultContext(effectiveRequest, result, operation);
            ValidateContext(context, operation, MemoryGateStage.AfterRead);
            var decision = await EvaluateAndRecordAsync(context, cancellationToken).ConfigureAwait(false);
            if (decision.Profile is MemorySecurityProfile.Observe ||
                decision.Action is MemoryGateAction.Allow)
            {
                return result;
            }

            if (decision.Action is MemoryGateAction.Sanitize)
            {
                var sanitized = _adapter.ApplySanitizedResult(
                    result,
                    operation,
                    decision.EffectiveContent
                        ?? throw new InvalidOperationException(
                            "An applied MCP result sanitization has no content."));
                return sanitized is null
                    ? throw new InvalidOperationException(
                        "The MCP server adapter returned a null sanitized result.")
                    : sanitized;
            }

            throw new MemoryMcpSafeException("memory.mcp.result_denied", correlationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MemoryMcpSafeException)
        {
            throw;
        }
        catch
        {
            throw new MemoryMcpSafeException("memory.mcp.adapter_failure", correlationId);
        }
    }

    private async ValueTask<MemoryGateDecision> EvaluateAndRecordAsync(
        MemoryGateContext context,
        CancellationToken cancellationToken)
    {
        var decision = await _pipeline.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
        if (_decisionSink is not null)
        {
            await _decisionSink.RecordAsync(context, decision, cancellationToken).ConfigureAwait(false);
        }

        return decision;
    }

    private void ValidateContext(
        MemoryGateContext context,
        MemoryMcpServerOperationContract operation,
        MemoryGateStage expectedStage)
    {
        MemoryToolCallGate.ValidateContext(context, operation.Operation, expectedStage);
        if (!string.Equals(context.ProviderId, _registry.ServerId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The trusted MCP server adapter returned a context for a different server.");
        }
    }
}

/// <summary>Reviewed hosted MCP memory operation and available provider enforcement seams.</summary>
public sealed class MemoryHostedMcpOperationContract
{
    private const MemoryHostedMcpCallbackCapabilities KnownCapabilities =
        MemoryHostedMcpCallbackCapabilities.CallArguments |
        MemoryHostedMcpCallbackCapabilities.ResultContent |
        MemoryHostedMcpCallbackCapabilities.TrustedScope |
        MemoryHostedMcpCallbackCapabilities.PreExecutionApproval |
        MemoryHostedMcpCallbackCapabilities.DerivedActions;

    public MemoryHostedMcpOperationContract(
        string serverId,
        string serverVersion,
        string schemaFingerprint,
        MemoryOperationContract operation,
        MemoryHostedMcpApprovalMode approvalMode,
        MemoryHostedMcpCallbackCapabilities callbackCapabilities)
    {
        ServerId = MemoryValidation.Identifier(serverId, nameof(serverId));
        ServerVersion = MemoryValidation.Identifier(serverVersion, nameof(serverVersion));
        SchemaFingerprint = MemoryDigest.Validate(schemaFingerprint, nameof(schemaFingerprint));
        Operation = operation ?? throw new ArgumentNullException(nameof(operation));
        if (Operation.Surface is not MemorySurface.HostedMcp)
        {
            throw new ArgumentException(
                "Hosted MCP contracts require a HostedMcp memory operation.",
                nameof(operation));
        }

        ApprovalMode = MemoryValidation.Defined(approvalMode, nameof(approvalMode));
        if ((callbackCapabilities & ~KnownCapabilities) != 0)
        {
            throw new ArgumentException(
                "Hosted MCP callback capabilities contain unknown flags.",
                nameof(callbackCapabilities));
        }

        CallbackCapabilities = callbackCapabilities;
        ConfigurationFingerprint = MemoryPolicyFingerprint.Compute(
            ServerId,
            ServerVersion,
            SchemaFingerprint,
            Operation.OperationName,
            ((int)Operation.Kind).ToString(),
            ((int)Operation.Category).ToString(),
            Operation.IsSideEffecting.ToString(),
            Operation.MayReturnSensitiveContent.ToString(),
            string.Join(",", Operation.ContentArguments),
            string.Join(",", Operation.ScopeArguments),
            ((int)ApprovalMode).ToString(),
            ((int)CallbackCapabilities).ToString());
    }

    public string ServerId { get; }
    public string ServerVersion { get; }
    public string SchemaFingerprint { get; }
    public MemoryOperationContract Operation { get; }
    public MemoryHostedMcpApprovalMode ApprovalMode { get; }
    public MemoryHostedMcpCallbackCapabilities CallbackCapabilities { get; }
    public string ConfigurationFingerprint { get; }
}

/// <summary>One content-free MCP coverage decision.</summary>
public sealed record MemoryMcpCoverageEntry(
    string ServerId,
    string ServerVersion,
    MemoryMcpTransport Transport,
    string OperationName,
    MemoryOperationKind? OperationKind,
    MemorySurface Surface,
    string SchemaFingerprint,
    MemoryCoverageLevel Coverage,
    string Note);

/// <summary>Separate per-operation coverage report for local, owned, and hosted MCP paths.</summary>
public sealed class MemoryMcpCoverageReport
{
    internal MemoryMcpCoverageReport(IEnumerable<MemoryMcpCoverageEntry> entries)
    {
        Entries = new ReadOnlyCollection<MemoryMcpCoverageEntry>(
            MemoryMcpValidation.Snapshot(
                entries,
                nameof(entries),
                MemoryMcpValidation.MaximumCoverageEntries));
        ConfigurationFingerprint = MemoryPolicyFingerprint.Compute(Entries.Select(entry => string.Join(
            ":",
            entry.ServerId,
            entry.ServerVersion,
            (int)entry.Transport,
            entry.OperationName,
            entry.OperationKind is null ? "unknown" : ((int)entry.OperationKind).ToString(),
            (int)entry.Surface,
            entry.SchemaFingerprint,
            (int)entry.Coverage)));
    }

    public IReadOnlyList<MemoryMcpCoverageEntry> Entries { get; }
    public string ConfigurationFingerprint { get; }

    public bool HasCoverageBelow(MemoryCoverageLevel minimum)
    {
        MemoryValidation.Defined(minimum, nameof(minimum));
        return Entries.Any(entry => entry.Coverage < minimum);
    }
}

/// <summary>Thrown when MCP memory coverage cannot meet the requested enforcing threshold.</summary>
public sealed class MemoryMcpCoverageException : InvalidOperationException
{
    public MemoryMcpCoverageException(
        MemoryMcpCoverageReport report,
        MemoryCoverageLevel minimumCoverage)
        : base(BuildMessage(report, minimumCoverage))
    {
        Report = report ?? throw new ArgumentNullException(nameof(report));
        MinimumCoverage = MemoryValidation.Defined(minimumCoverage, nameof(minimumCoverage));
    }

    public MemoryMcpCoverageReport Report { get; }
    public MemoryCoverageLevel MinimumCoverage { get; }

    private static string BuildMessage(
        MemoryMcpCoverageReport report,
        MemoryCoverageLevel minimumCoverage)
    {
        ArgumentNullException.ThrowIfNull(report);
        MemoryValidation.Defined(minimumCoverage, nameof(minimumCoverage));
        var operations = report.Entries
            .Where(entry => entry.Coverage < minimumCoverage)
            .Select(entry => $"{entry.ServerId}/{entry.OperationName}")
            .OrderBy(value => value, StringComparer.Ordinal);
        return $"MCP memory coverage is below '{minimumCoverage}' for: {string.Join(", ", operations)}.";
    }
}

/// <summary>Computes honest, per-operation MCP memory coverage.</summary>
public static class MemoryMcpCoverageAnalyzer
{
    private const MemoryHostedMcpCallbackCapabilities CompleteHostedCallbacks =
        MemoryHostedMcpCallbackCapabilities.CallArguments |
        MemoryHostedMcpCallbackCapabilities.ResultContent |
        MemoryHostedMcpCallbackCapabilities.TrustedScope |
        MemoryHostedMcpCallbackCapabilities.PreExecutionApproval;

    public static MemoryMcpCoverageReport AnalyzeLocal(
        IEnumerable<MemoryMcpClientToolBinding> bindings,
        MemoryMcpClientOperationRegistry registry,
        MemoryToolCallGate? callGate = null,
        MemoryToolResultGate? resultGate = null,
        MemoryInfluenceGate? influenceGate = null,
        MemoryMcpServerCoverageEvidence? ownedServer = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var snapshot = MemoryMcpValidation.Snapshot(bindings, nameof(bindings));

        var duplicate = snapshot
            .GroupBy(binding => binding.Tool.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Duplicate local MCP runtime binding '{duplicate.Key}'.",
                nameof(bindings));
        }

        var entries = new List<MemoryMcpCoverageEntry>();
        foreach (var binding in snapshot)
        {
            if (!registry.TryGet(binding.Tool.Name, out var contract))
            {
                entries.Add(new MemoryMcpCoverageEntry(
                    binding.ServerId,
                    binding.ServerVersion,
                    binding.Transport,
                    binding.Tool.Name,
                    OperationKind: null,
                    MemorySurface.LocalMcp,
                    binding.SchemaFingerprint,
                    MemoryCoverageLevel.Unsupported,
                    "local MCP tool has no explicit reviewed operation contract"));
                continue;
            }

            if (binding.Tool is not AIFunction ||
                !string.Equals(binding.ServerId, contract.ServerId, StringComparison.Ordinal) ||
                !string.Equals(binding.ServerVersion, contract.ServerVersion, StringComparison.Ordinal) ||
                binding.Transport != contract.Transport ||
                !string.Equals(
                    binding.SchemaFingerprint,
                    contract.SchemaFingerprint,
                    StringComparison.Ordinal))
            {
                entries.Add(CreateLocalEntry(
                    contract,
                    MemoryCoverageLevel.Unsupported,
                    "runtime server, version, transport, schema, or execution seam differs from the reviewed contract"));
                continue;
            }

            var toolCoverage = MemoryToolCoverageAnalyzer.Analyze(
                [binding.Tool],
                registry.OperationRegistry,
                callGate,
                resultGate,
                influenceGate).Entries.Single();
            var hasServer = ownedServer?.Covers(contract) == true;
            var requiresInfluence =
                contract.Operation.Kind is MemoryOperationKind.Search or MemoryOperationKind.Recall &&
                contract.Operation.MayReturnSensitiveContent;
            var influenceMatches =
                influenceGate?.RegistryFingerprint == registry.OperationRegistry.ConfigurationFingerprint;

            if (hasServer &&
                toolCoverage.Coverage is MemoryCoverageLevel.Boundary &&
                (!requiresInfluence || influenceMatches))
            {
                entries.Add(CreateLocalEntry(
                    contract,
                    MemoryCoverageLevel.FullLifecycle,
                    "local call/result, matching owned server, and required derived-action seams are enforced"));
            }
            else
            {
                entries.Add(CreateLocalEntry(contract, toolCoverage.Coverage, toolCoverage.Note));
            }
        }

        foreach (var missing in registry.Contracts.Values
                     .Where(contract => snapshot.All(binding =>
                         !string.Equals(
                             binding.Tool.Name,
                             contract.Operation.OperationName,
                             StringComparison.Ordinal))))
        {
            entries.Add(CreateLocalEntry(
                missing,
                MemoryCoverageLevel.Unsupported,
                "reviewed local MCP operation has no runtime tool binding"));
        }

        return new MemoryMcpCoverageReport(entries);
    }

    public static MemoryMcpCoverageReport AnalyzeHosted(
        IEnumerable<MemoryHostedMcpOperationContract> contracts,
        MemorySecurityProfile profile,
        MemoryMcpServerCoverageEvidence? ownedServer = null)
    {
        MemoryValidation.Defined(profile, nameof(profile));
        var snapshot = MemoryMcpValidation.Snapshot(contracts, nameof(contracts));

        var duplicate = snapshot
            .GroupBy(
                contract => $"{contract.ServerId}\0{contract.Operation.OperationName}",
                StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                "Hosted MCP operation identities must be unique.",
                nameof(contracts));
        }

        var entries = new List<MemoryMcpCoverageEntry>(snapshot.Length);
        foreach (var contract in snapshot)
        {
            MemoryCoverageLevel coverage;
            string note;
            if (profile is MemorySecurityProfile.Observe)
            {
                coverage = MemoryCoverageLevel.ObserveOnly;
                note = "hosted MCP policy is observe-only";
            }
            else
            {
                var callbacksComplete =
                    (contract.CallbackCapabilities & CompleteHostedCallbacks) == CompleteHostedCallbacks;
                var requiresDerivedActions =
                    contract.Operation.Kind is MemoryOperationKind.Search or MemoryOperationKind.Recall &&
                    contract.Operation.MayReturnSensitiveContent;
                var derivedActionsCovered =
                    !requiresDerivedActions ||
                    (contract.CallbackCapabilities & MemoryHostedMcpCallbackCapabilities.DerivedActions) != 0;
                var ownedServerCovered = ownedServer?.Covers(contract) == true;

                if ((callbacksComplete || ownedServerCovered) && derivedActionsCovered)
                {
                    coverage = MemoryCoverageLevel.FullLifecycle;
                    note = callbacksComplete
                        ? "complete provider callbacks expose call, result, trusted scope, approval, and required derived actions"
                        : "matching owned server plus declared derived-action coverage protects the hosted path";
                }
                else if (contract.ApprovalMode is MemoryHostedMcpApprovalMode.Always)
                {
                    coverage = MemoryCoverageLevel.ActionOnly;
                    note = "mandatory provider approval covers execution only; write content and recalled items remain opaque";
                }
                else
                {
                    coverage = MemoryCoverageLevel.Unsupported;
                    note = "hosted MCP lacks complete callbacks, matching owned-server evidence, or mandatory approval";
                }
            }

            entries.Add(new MemoryMcpCoverageEntry(
                contract.ServerId,
                contract.ServerVersion,
                MemoryMcpTransport.ProviderHosted,
                contract.Operation.OperationName,
                contract.Operation.Kind,
                contract.Operation.Surface,
                contract.SchemaFingerprint,
                coverage,
                note));
        }

        return new MemoryMcpCoverageReport(entries);
    }

    public static MemoryMcpCoverageReport RequireCoverage(
        MemoryMcpCoverageReport report,
        MemoryCoverageLevel minimumCoverage)
    {
        ArgumentNullException.ThrowIfNull(report);
        MemoryValidation.Defined(minimumCoverage, nameof(minimumCoverage));
        if (report.HasCoverageBelow(minimumCoverage))
        {
            throw new MemoryMcpCoverageException(report, minimumCoverage);
        }

        return report;
    }

    private static MemoryMcpCoverageEntry CreateLocalEntry(
        MemoryMcpClientOperationContract contract,
        MemoryCoverageLevel coverage,
        string note)
        => new(
            contract.ServerId,
            contract.ServerVersion,
            contract.Transport,
            contract.Operation.OperationName,
            contract.Operation.Kind,
            contract.Operation.Surface,
            contract.SchemaFingerprint,
            coverage,
            note);
}
