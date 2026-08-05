// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Gatekeeper.Memory;

/// <summary>Complete runtime configuration for memory protection composed by <c>UseGatekeeper</c>.</summary>
public sealed class MemoryProtectionOptions
{
    /// <summary>Creates a configuration from immutable runtime collaborators.</summary>
    public MemoryProtectionOptions(
        MemoryGatePipeline pipeline,
        MemoryToolOperationRegistry toolRegistry,
        IMemoryToolContextAdapter toolContextAdapter)
    {
        Pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        ToolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        ToolContextAdapter = toolContextAdapter ?? throw new ArgumentNullException(nameof(toolContextAdapter));
    }

    public MemoryGatePipeline Pipeline { get; }
    public MemoryToolOperationRegistry ToolRegistry { get; }
    public IMemoryToolContextAdapter ToolContextAdapter { get; }
    public IMemoryGateDecisionSink? DecisionSink { get; init; }
    public MemoryProtectionConfiguration? DeploymentConfiguration { get; init; }
    public IReadOnlyList<string> SensitiveSinkTools { get; init; } = [];
    public int MinimumTaintLength { get; init; } = 8;
    public bool CanonicalizeInfluence { get; init; } = true;
    public MemoryMcpClientOperationRegistry? LocalMcpRegistry { get; init; }
    public IReadOnlyList<MemoryMcpClientToolBinding> LocalMcpBindings { get; init; } = [];
    public MemoryMcpServerCoverageEvidence? OwnedMcpServer { get; init; }
    public IReadOnlyList<MemoryHostedMcpOperationContract> HostedMcpContracts { get; init; } = [];
    public IReadOnlyList<GatedAIContextProvider> ContextProviders { get; init; } = [];
    public IReadOnlyList<MemoryProviderNativeGate> ProviderNativeGates { get; init; } = [];
}

/// <summary>One provider integration coverage decision with content-free policy provenance.</summary>
public sealed record MemoryProviderCoverageEntry(
    string ProviderFingerprint,
    string Integration,
    MemoryProviderNativeCapabilities NativeCapabilities,
    MemoryCoverageLevel Coverage,
    string PolicyFingerprint,
    string Note);

/// <summary>Authoritative, content-free report for the exact memory adapters wired by Gatekeeper.</summary>
public sealed class MemoryProtectionReport
{
    private static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    internal MemoryProtectionReport(
        MemorySecurityPolicy policy,
        string policyFingerprint,
        string adapterFingerprint,
        MemoryToolCoverageReport toolCoverage,
        MemoryMcpCoverageReport mcpCoverage,
        IEnumerable<MemoryProviderCoverageEntry> providerCoverage)
    {
        ArgumentNullException.ThrowIfNull(policy);
        PolicyId = policy.PolicyId;
        PolicyVersion = policy.Version;
        Profile = policy.Profile;
        MinimumCoverage = policy.MinimumCoverage;
        PolicyFingerprint = MemoryDigest.Validate(policyFingerprint, nameof(policyFingerprint));
        AdapterFingerprint = MemoryDigest.Validate(adapterFingerprint, nameof(adapterFingerprint));
        ToolCoverage = toolCoverage ?? throw new ArgumentNullException(nameof(toolCoverage));
        McpCoverage = mcpCoverage ?? throw new ArgumentNullException(nameof(mcpCoverage));
        ProviderCoverage = new ReadOnlyCollection<MemoryProviderCoverageEntry>(providerCoverage.ToArray());
        ConfigurationFingerprint = MemoryPolicyFingerprint.Compute(
            SchemaVersion,
            PolicyFingerprint,
            AdapterFingerprint,
            ToolCoverage.Entries.Select(entry => $"{entry.ToolName}:{(int)entry.Coverage}"),
            McpCoverage.ConfigurationFingerprint,
            ProviderCoverage.Select(entry => $"{entry.ProviderFingerprint}:{entry.Integration}:{(int)entry.Coverage}"));
    }

    public const string SchemaVersion = "gatekeeper.memory-protection-report/1";
    public string Schema => SchemaVersion;
    public string PolicyId { get; }
    public string PolicyVersion { get; }
    public MemorySecurityProfile Profile { get; }
    public MemoryCoverageLevel MinimumCoverage { get; }
    public string PolicyFingerprint { get; }
    public string AdapterFingerprint { get; }
    public MemoryToolCoverageReport ToolCoverage { get; }
    public MemoryMcpCoverageReport McpCoverage { get; }
    public IReadOnlyList<MemoryProviderCoverageEntry> ProviderCoverage { get; }
    public string ConfigurationFingerprint { get; }
    public bool HasCoverageBelowMinimum
        => ToolCoverage.Entries.Any(IsToolBelowMinimum) ||
           McpCoverage.HasCoverageBelow(MinimumCoverage) ||
           ProviderCoverage.Any(entry => entry.Coverage < MinimumCoverage);

    internal bool IsToolBelowMinimum(MemoryToolCoverageEntry entry)
    {
        if (entry.Coverage >= MinimumCoverage)
        {
            return false;
        }

        return entry.Surface is not MemorySurface.LocalMcp ||
            !McpCoverage.Entries.Any(mcp =>
                mcp.Surface is MemorySurface.LocalMcp &&
                string.Equals(mcp.OperationName, entry.ToolName, StringComparison.Ordinal) &&
                mcp.Coverage >= MinimumCoverage);
    }

    public string ToJson(JsonSerializerOptions? options = null)
        => JsonSerializer.Serialize(this, options ?? DefaultJsonOptions);
}

internal sealed record ResolvedMemoryProtection(
    MemoryToolCallGate CallGate,
    MemoryToolResultGate ResultGate,
    MemoryInfluenceGate? InfluenceGate,
    MemoryProtectionReport Report);

internal static class MemoryProtectionOptionsResolver
{
    public static ResolvedMemoryProtection Resolve(
        MemoryProtectionOptions configured,
        GatekeeperEnforcement enforcement,
        IReadOnlyList<AITool>? knownTools)
    {
        ArgumentNullException.ThrowIfNull(configured);
        ValidateProfile(enforcement, configured.Pipeline.Policy.Profile);
        if (configured.MinimumTaintLength < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configured.MinimumTaintLength),
                "Minimum taint length must be positive.");
        }

        var sensitiveSinks = ResolveSensitiveSinks(configured);
        var localBindings = Snapshot(configured.LocalMcpBindings, nameof(configured.LocalMcpBindings));
        var hostedContracts = Snapshot(configured.HostedMcpContracts, nameof(configured.HostedMcpContracts));
        var contextProviders = Snapshot(configured.ContextProviders, nameof(configured.ContextProviders));
        var nativeGates = Snapshot(configured.ProviderNativeGates, nameof(configured.ProviderNativeGates));

        var callGate = new MemoryToolCallGate(
            configured.Pipeline,
            configured.ToolRegistry,
            configured.ToolContextAdapter,
            configured.DecisionSink);
        var resultGate = new MemoryToolResultGate(
            configured.Pipeline,
            configured.ToolRegistry,
            configured.ToolContextAdapter,
            configured.DecisionSink);
        var influenceGate = sensitiveSinks.Length == 0 ||
            configured.Pipeline.Policy.Profile is MemorySecurityProfile.Observe
            ? null
            : new MemoryInfluenceGate(
                configured.ToolRegistry,
                sensitiveSinks,
                configured.MinimumTaintLength,
                configured.CanonicalizeInfluence);

        if (configured.ToolRegistry.Contracts.Count > 0 && knownTools is null)
        {
            throw new InvalidOperationException(
                "UseGatekeeper: memory protection has tool contracts but GatekeeperOptions.KnownTools is null. " +
                "Pass the same frozen tool inventory used by ChatOptions.Tools so coverage can fail closed.");
        }

        var toolCoverage = knownTools is null
            ? new MemoryToolCoverageReport([], configured.Pipeline.PolicyFingerprint)
            : MemoryToolCoverageAnalyzer.Analyze(
                knownTools.ToArray(),
                configured.ToolRegistry,
                callGate,
                resultGate,
                influenceGate);
        var mcpCoverage = AnalyzeMcp(
            configured,
            localBindings,
            hostedContracts,
            callGate,
            resultGate,
            influenceGate);
        var providerCoverage = AnalyzeProviders(configured.Pipeline, contextProviders, nativeGates);
        var adapterFingerprint = GateConfigFingerprint.Compute(
            influenceGate is null ? [callGate] : [callGate, influenceGate],
            [resultGate]);
        var report = new MemoryProtectionReport(
            configured.Pipeline.Policy,
            configured.Pipeline.PolicyFingerprint,
            adapterFingerprint,
            toolCoverage,
            mcpCoverage,
            providerCoverage);

        if (configured.Pipeline.Policy.Profile is MemorySecurityProfile.Enforce &&
            (toolCoverage.HasUnclassifiedMemoryLikeTools || report.HasCoverageBelowMinimum))
        {
            throw new MemoryProtectionCoverageException(report);
        }

        return new ResolvedMemoryProtection(callGate, resultGate, influenceGate, report);
    }

    private static MemoryMcpCoverageReport AnalyzeMcp(
        MemoryProtectionOptions configured,
        IReadOnlyList<MemoryMcpClientToolBinding> localBindings,
        IReadOnlyList<MemoryHostedMcpOperationContract> hostedContracts,
        MemoryToolCallGate callGate,
        MemoryToolResultGate resultGate,
        MemoryInfluenceGate? influenceGate)
    {
        if ((configured.LocalMcpRegistry is null) != (localBindings.Count == 0))
        {
            throw new InvalidOperationException(
                "UseGatekeeper: LocalMcpRegistry and LocalMcpBindings must be configured together or both omitted.");
        }

        var entries = new List<MemoryMcpCoverageEntry>();
        if (configured.LocalMcpRegistry is not null)
        {
            entries.AddRange(MemoryMcpCoverageAnalyzer.AnalyzeLocal(
                localBindings,
                configured.LocalMcpRegistry,
                callGate,
                resultGate,
                influenceGate,
                configured.OwnedMcpServer).Entries);
        }

        if (hostedContracts.Count > 0)
        {
            entries.AddRange(MemoryMcpCoverageAnalyzer.AnalyzeHosted(
                hostedContracts,
                configured.Pipeline.Policy.Profile,
                configured.OwnedMcpServer,
                configured.Pipeline.PolicyFingerprint).Entries);
        }

        return new MemoryMcpCoverageReport(entries, configured.Pipeline.PolicyFingerprint);
    }

    private static IReadOnlyList<MemoryProviderCoverageEntry> AnalyzeProviders(
        MemoryGatePipeline pipeline,
        IReadOnlyList<GatedAIContextProvider> contextProviders,
        IReadOnlyList<MemoryProviderNativeGate> nativeGates)
    {
        var entries = new List<MemoryProviderCoverageEntry>(contextProviders.Count + nativeGates.Count);
        foreach (var provider in contextProviders)
        {
            var matches = string.Equals(
                provider.PolicyFingerprint,
                pipeline.PolicyFingerprint,
                StringComparison.Ordinal);
            entries.Add(new MemoryProviderCoverageEntry(
                provider.ProviderFingerprint,
                "generic-context-provider",
                MemoryProviderNativeCapabilities.None,
                matches ? provider.Coverage : MemoryCoverageLevel.Unsupported,
                provider.PolicyFingerprint,
                matches
                    ? "generic decorator protects recall and persistence boundaries; provider-internal extraction remains opaque"
                    : "provider decorator uses a different memory policy fingerprint"));
        }

        foreach (var nativeGate in nativeGates)
        {
            var descriptor = nativeGate.Descriptor;
            var matches = string.Equals(
                nativeGate.PolicyFingerprint,
                pipeline.PolicyFingerprint,
                StringComparison.Ordinal);
            var complete = descriptor.Capabilities.HasFlag(MemoryProviderNativeCapabilities.CandidateWrites) &&
                           descriptor.Capabilities.HasFlag(MemoryProviderNativeCapabilities.RecalledItems);
            var coverage = !matches
                ? MemoryCoverageLevel.Unsupported
                : pipeline.Policy.Profile is MemorySecurityProfile.Observe
                    ? MemoryCoverageLevel.ObserveOnly
                    : complete
                        ? MemoryCoverageLevel.FullLifecycle
                        : MemoryCoverageLevel.Boundary;
            entries.Add(new MemoryProviderCoverageEntry(
                descriptor.ConfigurationFingerprint,
                "provider-native",
                descriptor.Capabilities,
                coverage,
                nativeGate.PolicyFingerprint,
                !matches
                    ? "provider-native hook uses a different memory policy fingerprint"
                    : complete
                        ? "candidate-write and recalled-item hooks expose the complete provider lifecycle"
                        : "only part of the provider-native lifecycle is exposed"));
        }

        return new ReadOnlyCollection<MemoryProviderCoverageEntry>(entries);
    }

    private static void ValidateProfile(
        GatekeeperEnforcement enforcement,
        MemorySecurityProfile profile)
    {
        var compatible = enforcement is GatekeeperEnforcement.Observe
            ? profile is MemorySecurityProfile.Observe
            : profile is MemorySecurityProfile.Enforce;
        if (!compatible)
        {
            throw new InvalidOperationException(
                "UseGatekeeper: Gatekeeper enforcement '" + enforcement +
                "' is incompatible with memory profile '" + profile +
                "'. Observe must compose an Observe memory policy; ReplaceResult/Terminate must compose Enforce.");
        }
    }


    private static string[] ResolveSensitiveSinks(MemoryProtectionOptions configured)
    {
        if (configured.DeploymentConfiguration is null)
        {
            return Snapshot(configured.SensitiveSinkTools, nameof(configured.SensitiveSinkTools));
        }

        if (configured.SensitiveSinkTools is null)
        {
            throw new ArgumentNullException(nameof(configured.SensitiveSinkTools));
        }

        if (configured.SensitiveSinkTools.Count > 0)
        {
            throw new InvalidOperationException(
                "UseGatekeeper: sensitive sinks must come from either DeploymentConfiguration or " +
                "MemoryProtectionOptions.SensitiveSinkTools, not both.");
        }

        var deployment = configured.DeploymentConfiguration;
        if (!string.Equals(
                deployment.ExpectedPolicyFingerprint,
                configured.Pipeline.PolicyFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "UseGatekeeper: deployment configuration expected a different memory policy fingerprint.");
        }

        if (deployment.MinimumCoverage != configured.Pipeline.Policy.MinimumCoverage)
        {
            throw new InvalidOperationException(
                "UseGatekeeper: deployment configuration and runtime policy minimum coverage differ.");
        }

        return Snapshot(deployment.SensitiveSinkTools, nameof(deployment.SensitiveSinkTools));
    }

    private static T[] Snapshot<T>(IEnumerable<T> values, string parameterName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var snapshot = values.ToArray();
        if (snapshot.Any(value => value is null))
        {
            throw new ArgumentException(
                "Memory-protection collections cannot contain null entries.",
                parameterName);
        }

        return snapshot;
    }
}

/// <summary>Thrown when composite memory protection cannot prove the policy's minimum coverage.</summary>
public sealed class MemoryProtectionCoverageException : InvalidOperationException
{
    internal MemoryProtectionCoverageException(MemoryProtectionReport report)
        : base(BuildMessage(report))
        => Report = report;

    public MemoryProtectionReport Report { get; }

    private static string BuildMessage(MemoryProtectionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var surfaces = report.ToolCoverage.Entries
            .Where(entry => report.IsToolBelowMinimum(entry) ||
                            entry.Classification is MemoryToolClassification.UnclassifiedMemoryLike)
            .Select(entry => "tool:" + entry.ToolName)
            .Concat(report.McpCoverage.Entries
                .Where(entry => entry.Coverage < report.MinimumCoverage)
                .Select(entry => "mcp:" + entry.ServerId + "/" + entry.OperationName))
            .Concat(report.ProviderCoverage
                .Where(entry => entry.Coverage < report.MinimumCoverage)
                .Select(entry => "provider:" + entry.ProviderFingerprint))
            .OrderBy(value => value, StringComparer.Ordinal);
        return "Memory protection coverage is below '" + report.MinimumCoverage +
               "' for: " + string.Join(", ", surfaces) + ".";
    }
}
