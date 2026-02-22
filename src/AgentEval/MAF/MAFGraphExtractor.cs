// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;
using MAFWorkflows = Microsoft.Agents.AI.Workflows;
using MAFCheckpointing = Microsoft.Agents.AI.Workflows.Checkpointing;

namespace AgentEval.MAF;

/// <summary>
/// Extracts workflow graph structure from a built MAF <see cref="MAFWorkflows.Workflow"/>
/// and translates it into AgentEval's <see cref="WorkflowGraphSnapshot"/>.
/// </summary>
/// <remarks>
/// Uses <see cref="MAFWorkflows.Workflow.ReflectEdges()"/> (public API) to discover edges and
/// accepts executor IDs from the caller because <c>Workflow.ExecutorBindings</c> is internal.
/// </remarks>
public static class MAFGraphExtractor
{
    /// <summary>
    /// Extracts the static workflow graph from a built MAF Workflow object.
    /// </summary>
    /// <param name="workflow">The built MAF Workflow.</param>
    /// <param name="executorIds">
    /// The list of executor IDs in the workflow. Must be provided because
    /// <c>Workflow.ExecutorBindings</c> is internal to the MAF assembly.
    /// </param>
    /// <param name="displayNameMap">
    /// Optional mapping from full MAF executor IDs (e.g., "Planner_abc123") to clean display
    /// names (e.g., "Planner"). When provided, node and edge IDs use clean names.
    /// </param>
    /// <returns>A <see cref="WorkflowGraphSnapshot"/> representing the workflow structure.</returns>
    public static WorkflowGraphSnapshot ExtractGraph(
        MAFWorkflows.Workflow workflow,
        IReadOnlyList<string> executorIds,
        IReadOnlyDictionary<string, string>? displayNameMap = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(executorIds);

        // Helper to translate full MAF IDs to clean names
        string Normalize(string id) =>
            displayNameMap is not null && displayNameMap.TryGetValue(id, out var clean) ? clean : id;

        var reflectedEdges = workflow.ReflectEdges();
        var startId = workflow.StartExecutorId;

        // Determine exit nodes: executors that have no outgoing edges
        // (use raw MAF IDs for this check since reflectedEdges keys are raw MAF IDs)
        var sourcesWithEdges = new HashSet<string>(reflectedEdges.Keys);
        var exitNodeIds = executorIds
            .Where(id => !sourcesWithEdges.Contains(id))
            .Select(Normalize)
            .ToList();

        // If all nodes have edges (e.g., cyclic), fall back to last in the list
        if (exitNodeIds.Count == 0 && executorIds.Count > 0)
        {
            exitNodeIds.Add(Normalize(executorIds[^1]));
        }

        // Build nodes (translate IDs to clean names)
        var normalizedStartId = Normalize(startId);
        var nodes = executorIds.Select(id =>
        {
            var cleanId = Normalize(id);
            return new WorkflowNode
            {
                NodeId = cleanId,
                DisplayName = cleanId,
                IsEntryPoint = cleanId == normalizedStartId,
                IsExitNode = exitNodeIds.Contains(cleanId)
            };
        }).ToList();

        // Build edges from MAF's ReflectEdges (deduplicate by EdgeId since fan-in
        // edges appear under each source executor's entry in the reflected map)
        var edges = new List<WorkflowEdge>();
        var seenEdgeIds = new HashSet<string>();
        foreach (var (sourceId, edgeInfos) in reflectedEdges)
        {
            foreach (var edgeInfo in edgeInfos)
            {
                foreach (var edge in TranslateEdge(Normalize(sourceId), edgeInfo, Normalize))
                {
                    if (seenEdgeIds.Add(edge.EdgeId))
                    {
                        edges.Add(edge);
                    }
                }
            }
        }

        return new WorkflowGraphSnapshot
        {
            Nodes = nodes,
            Edges = edges,
            EntryNodeId = normalizedStartId,
            ExitNodeIds = exitNodeIds
        };
    }

    /// <summary>
    /// Translates a single MAF EdgeInfo into one or more AgentEval WorkflowEdge entries.
    /// </summary>
    private static IEnumerable<WorkflowEdge> TranslateEdge(
        string sourceId,
        MAFCheckpointing.EdgeInfo edgeInfo,
        Func<string, string> normalize)
    {
        switch (edgeInfo.Kind)
        {
            case MAFWorkflows.EdgeKind.Direct:
                var targetId = normalize(edgeInfo.Connection.SinkIds.FirstOrDefault() ?? "unknown");
                var isConditional = edgeInfo is MAFCheckpointing.DirectEdgeInfo directInfo
                    && directInfo.HasCondition;

                yield return new WorkflowEdge
                {
                    EdgeId = $"{sourceId}->{targetId}",
                    SourceExecutorId = sourceId,
                    TargetExecutorId = targetId,
                    EdgeType = isConditional ? EdgeType.Conditional : EdgeType.Sequential
                };
                break;

            case MAFWorkflows.EdgeKind.FanOut:
                // Fan-out: one source → multiple targets
                foreach (var sinkId in edgeInfo.Connection.SinkIds)
                {
                    var normalizedSink = normalize(sinkId);
                    yield return new WorkflowEdge
                    {
                        EdgeId = $"{sourceId}->>{normalizedSink}",
                        SourceExecutorId = sourceId,
                        TargetExecutorId = normalizedSink,
                        EdgeType = EdgeType.ParallelFanOut
                    };
                }
                break;

            case MAFWorkflows.EdgeKind.FanIn:
                // Fan-in: multiple sources → one target
                var fanInTarget = normalize(edgeInfo.Connection.SinkIds.FirstOrDefault() ?? "unknown");
                foreach (var fanInSource in edgeInfo.Connection.SourceIds)
                {
                    var normalizedFanInSource = normalize(fanInSource);
                    yield return new WorkflowEdge
                    {
                        EdgeId = $"{normalizedFanInSource}>>{fanInTarget}",
                        SourceExecutorId = normalizedFanInSource,
                        TargetExecutorId = fanInTarget,
                        EdgeType = EdgeType.ParallelFanIn
                    };
                }
                break;

            default:
                // Unknown edge kind — create a generic sequential edge
                var fallbackTarget = normalize(edgeInfo.Connection.SinkIds.FirstOrDefault() ?? "unknown");
                yield return new WorkflowEdge
                {
                    EdgeId = $"{sourceId}->{fallbackTarget}",
                    SourceExecutorId = sourceId,
                    TargetExecutorId = fallbackTarget,
                    EdgeType = EdgeType.Sequential
                };
                break;
        }
    }
}
