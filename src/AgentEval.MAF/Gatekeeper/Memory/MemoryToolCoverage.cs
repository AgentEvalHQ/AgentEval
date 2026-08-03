// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.ObjectModel;
using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Gatekeeper.Memory;

/// <summary>One explicit memory-tool coverage assessment.</summary>
public sealed record MemoryToolCoverageEntry(
    string ToolName,
    MemoryToolClassification Classification,
    MemoryOperationKind? OperationKind,
    MemorySurface? Surface,
    ToolExecutionModel ExecutionModel,
    MemoryCoverageLevel Coverage,
    string Note);

/// <summary>Itemized coverage report for declared and suspected memory tools.</summary>
public sealed class MemoryToolCoverageReport
{
    internal MemoryToolCoverageReport(
        IEnumerable<MemoryToolCoverageEntry> entries,
        string? policyFingerprint = null)
    {
        Entries = new ReadOnlyCollection<MemoryToolCoverageEntry>(entries.ToList());
        PolicyFingerprint = policyFingerprint;
    }

    public IReadOnlyList<MemoryToolCoverageEntry> Entries { get; }
    public string? PolicyFingerprint { get; }
    public bool HasUnsupportedMemoryTools
        => Entries.Any(entry => entry.Coverage is MemoryCoverageLevel.Unsupported);
    public bool HasUnclassifiedMemoryLikeTools
        => Entries.Any(entry => entry.Classification is MemoryToolClassification.UnclassifiedMemoryLike);

    public bool HasCoverageBelow(MemoryCoverageLevel minimum)
    {
        MemoryValidation.Defined(minimum, nameof(minimum));
        return Entries.Any(entry => entry.Coverage < minimum);
    }
}

/// <summary>Thrown when enforcing construction cannot prove a declared memory tool path.</summary>
public sealed class MemoryToolCoverageException : InvalidOperationException
{
    public MemoryToolCoverageException(
        MemoryToolCoverageReport report,
        MemoryCoverageLevel minimumCoverage = MemoryCoverageLevel.Boundary)
        : base(BuildMessage(report, minimumCoverage))
    {
        MemoryValidation.Defined(minimumCoverage, nameof(minimumCoverage));
        Report = report;
        MinimumCoverage = minimumCoverage;
    }

    public MemoryToolCoverageReport Report { get; }
    public MemoryCoverageLevel MinimumCoverage { get; }

    private static string BuildMessage(
        MemoryToolCoverageReport report,
        MemoryCoverageLevel minimumCoverage)
    {
        ArgumentNullException.ThrowIfNull(report);
        var names = report.Entries
            .Where(entry =>
                entry.Coverage < minimumCoverage ||
                entry.Classification is MemoryToolClassification.UnclassifiedMemoryLike)
            .Select(entry => entry.ToolName)
            .OrderBy(name => name, StringComparer.Ordinal);
        return $"Memory tool coverage is below '{minimumCoverage}' for: {string.Join(", ", names)}.";
    }
}

/// <summary>Computes per-operation memory coverage without treating tool names as authority.</summary>
public static class MemoryToolCoverageAnalyzer
{
    public static MemoryToolCoverageReport Analyze(
        IEnumerable<AITool> tools,
        MemoryToolOperationRegistry registry,
        MemoryToolCallGate? callGate = null,
        MemoryToolResultGate? resultGate = null,
        MemoryInfluenceGate? influenceGate = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(registry);

        var callMatches = callGate?.RegistryFingerprint == registry.ConfigurationFingerprint;
        var resultMatches =
            resultGate?.RegistryFingerprint == registry.ConfigurationFingerprint &&
            callGate?.PipelineFingerprint == resultGate.PipelineFingerprint;
        var influenceMatches = influenceGate?.RegistryFingerprint == registry.ConfigurationFingerprint;
        var entries = new List<MemoryToolCoverageEntry>();
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            var classification = MemoryToolClassifier.Classify(tool, registry);
            if (classification is MemoryToolClassification.NonMemory)
            {
                continue;
            }

            var execution = ClassifyExecution(tool);
            if (!registry.TryGet(tool.Name, out var operation))
            {
                entries.Add(new MemoryToolCoverageEntry(
                    tool.Name,
                    classification,
                    OperationKind: null,
                    Surface: null,
                    execution,
                    MemoryCoverageLevel.Unsupported,
                    "memory-like tool has no explicit operation contract; no semantics were inferred"));
                continue;
            }

            var isRead = operation.Kind is MemoryOperationKind.Search or MemoryOperationKind.Recall;
            MemoryCoverageLevel coverage;
            string note;
            if (execution is not ToolExecutionModel.InterceptedLocalFunction)
            {
                coverage = MemoryCoverageLevel.Unsupported;
                note = "tool does not execute through the local function middleware seam";
            }
            else if (!callMatches || (isRead && !resultMatches))
            {
                coverage = MemoryCoverageLevel.Unsupported;
                note = isRead
                    ? "memory read requires matching pre-call and post-result adapters"
                    : "memory mutation requires a matching pre-call adapter";
            }
            else if (callGate!.Profile is MemorySecurityProfile.Observe ||
                (isRead && resultGate!.Profile is MemorySecurityProfile.Observe))
            {
                coverage = MemoryCoverageLevel.ObserveOnly;
                note = "matching memory adapters are installed in observe-only mode";
            }
            else if (operation.Surface is MemorySurface.LocalMcp)
            {
                coverage = MemoryCoverageLevel.Boundary;
                note = "local MCP client boundary is covered; repository lifecycle remains unproven";
            }
            else if (isRead && operation.MayReturnSensitiveContent && !influenceMatches)
            {
                coverage = MemoryCoverageLevel.Boundary;
                note = "read call/result are covered, but derived sensitive actions lack memory influence protection";
            }
            else
            {
                coverage = MemoryCoverageLevel.FullLifecycle;
                note = "declared local invocation path has required call/result/derived-action coverage";
            }

            entries.Add(new MemoryToolCoverageEntry(
                tool.Name,
                classification,
                operation.Kind,
                operation.Surface,
                execution,
                coverage,
                note));
        }

        return new MemoryToolCoverageReport(entries, callGate?.PipelineFingerprint);
    }

    public static MemoryToolCoverageReport AnalyzeOrThrow(
        IEnumerable<AITool> tools,
        MemoryToolOperationRegistry registry,
        MemoryToolCallGate? callGate = null,
        MemoryToolResultGate? resultGate = null,
        MemoryInfluenceGate? influenceGate = null,
        MemoryCoverageLevel minimumCoverage = MemoryCoverageLevel.Boundary)
    {
        var report = Analyze(tools, registry, callGate, resultGate, influenceGate);
        if (report.HasUnsupportedMemoryTools ||
            report.HasUnclassifiedMemoryLikeTools ||
            report.HasCoverageBelow(minimumCoverage))
        {
            throw new MemoryToolCoverageException(report, minimumCoverage);
        }

        return report;
    }

#pragma warning disable MEAI001 // Evaluation-only hosted type is classified, not adopted as a runtime dependency.
    private static ToolExecutionModel ClassifyExecution(AITool tool)
        => tool switch
        {
            AIFunction => ToolExecutionModel.InterceptedLocalFunction,
            HostedMcpServerTool or HostedCodeInterpreterTool or HostedWebSearchTool or
                HostedFileSearchTool or HostedImageGenerationTool or HostedToolSearchTool
                => ToolExecutionModel.ProviderHostedOpaque,
            _ => ToolExecutionModel.UnknownExecutionModel,
        };
#pragma warning restore MEAI001
}
