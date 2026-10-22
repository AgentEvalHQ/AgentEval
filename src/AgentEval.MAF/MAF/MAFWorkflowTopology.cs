// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;
using MAFWorkflows = Microsoft.Agents.AI.Workflows;

namespace AgentEval.MAF;

/// <summary>Resolves the identities and containment visible in MAF's flattened event stream.</summary>
internal sealed class MAFWorkflowTopology
{
    private readonly List<(string AgentId, string ExecutorId)> _agents = [];
    public Dictionary<string, string> IdMap { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Parents { get; } = new(StringComparer.Ordinal);
    public HashSet<string> ChatIds { get; } = new(StringComparer.Ordinal);
    public HashSet<string> HostIds { get; } = new(StringComparer.Ordinal);
    public WorkflowGraphSnapshot Graph { get; private set; } = null!;

    public MAFWorkflows.WorkflowEvent RestoreNestedAgentEvent(MAFWorkflows.WorkflowEvent evt)
    {
        if (evt is not MAFWorkflows.WorkflowOutputEvent output || !HostIds.Contains(output.ExecutorId)) return evt;
        var agentId = output.Data switch
        {
            Microsoft.Agents.AI.AgentResponseUpdate update => update.AgentId,
            Microsoft.Agents.AI.AgentResponse response => response.AgentId,
            _ => null
        };
        if (output.Data is not (Microsoft.Agents.AI.AgentResponseUpdate or Microsoft.Agents.AI.AgentResponse)) return evt;
        var candidates = _agents.Where(agent => agent.AgentId == agentId && IsDescendant(agent.ExecutorId, output.ExecutorId)).ToArray();
        if (candidates.Length != 1)
            throw new NotSupportedException($"Expected: one identifiable nested agent for output from '{IdMap[output.ExecutorId]}'. Actual: {candidates.Length} matches for agent ID '{agentId}'. Suggestions: use agents with unique, populated AgentId values; MAF replaces the nested event's executor ID with its host ID.");
        return output.Data is Microsoft.Agents.AI.AgentResponseUpdate chunk
            ? new MAFWorkflows.AgentResponseUpdateEvent(candidates[0].ExecutorId, chunk)
            : new MAFWorkflows.AgentResponseEvent(candidates[0].ExecutorId, (Microsoft.Agents.AI.AgentResponse)output.Data);
    }

    private bool IsDescendant(string child, string host)
    {
        while (Parents.TryGetValue(child, out var parent))
        {
            if (parent == host) return true;
            child = parent;
        }
        return false;
    }

    public static MAFWorkflowTopology Create(MAFWorkflows.Workflow workflow,
        IReadOnlyList<string>? names = null, IReadOnlyDictionary<string, string>? rootMap = null)
    {
        var result = new MAFWorkflowTopology();
        var nodes = new List<WorkflowNode>();
        var edges = new List<WorkflowEdge>();
        var rawIds = new HashSet<string>(StringComparer.Ordinal);
        var displayIds = new HashSet<string>(StringComparer.Ordinal);
        var root = Visit(workflow, null, "", "");
        result.Graph = root with { Nodes = nodes, Edges = edges };
        return result;

        WorkflowGraphSnapshot Visit(MAFWorkflows.Workflow current, string? parent, string prefix, string portPrefix)
        {
            var bindings = current.ReflectExecutors();
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var id in bindings.Keys)
            {
                // MAF forwards child events without a scope. Equal raw IDs in different workflows cannot
                // be disambiguated, even if their desired display paths differ. Refuse before executing.
                if (!rawIds.Add(id))
                    throw new NotSupportedException($"Expected: globally unique executor IDs for nested recording. Actual: '{id}' occurs in multiple scopes. Suggestions: give each nested executor a unique raw ID; MAF events do not carry its parent scope.");
                var localNames = (names ?? []).Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(name => name[prefix.Length..]).OrderByDescending(name => name.Length);
                var clean = localNames.FirstOrDefault(name => id == name || id.StartsWith(name + "_", StringComparison.Ordinal)) ?? id;
                if (rootMap?.TryGetValue(id, out var mapped) == true) clean = mapped;
                var display = parent is null || clean.StartsWith(prefix, StringComparison.Ordinal) ? clean : prefix + clean;
                if (!displayIds.Add(display))
                    throw new ArgumentException($"Expected: distinct executor display names. Actual: more than one executor maps to '{display}'. Suggestions: supply distinct names.", nameof(names));
                map.Add(id, display);
                result.IdMap.Add(id, display);
                if (parent is not null) result.Parents.Add(id, parent);
                if (bindings[id] is MAFWorkflows.AIAgentBinding agent) result._agents.Add((agent.Agent.Id, id));
                if (bindings[id] is MAFWorkflows.RequestPortBinding && portPrefix.Length > 0)
                    result.IdMap.Add(portPrefix + id, display);
            }
            result.ChatIds.UnionWith(MAFWorkflowEventBridge.GetChatProtocolExecutorIds(current));
            var graph = MAFGraphExtractor.ExtractGraph(current, bindings.Keys.ToList(), map);
            nodes.AddRange(graph.Nodes);
            edges.AddRange(graph.Edges);
            foreach (var (id, binding) in bindings)
            {
                if (binding is not MAFWorkflows.SubworkflowBinding nested) continue;
                if (nested.WorkflowInstance.ReflectExecutors()[nested.WorkflowInstance.StartExecutorId] is MAFWorkflows.SubworkflowBinding)
                    throw new NotSupportedException($"Expected: a concrete entry executor inside '{map[id]}'. Actual: its entry is another subworkflow host with a catch-all input protocol. Suggestions: add an entry executor with an explicit input type; MAF cannot forward input through stacked catch-all hosts.");
                result.HostIds.Add(id);
                Visit(nested.WorkflowInstance, id, map[id] + "/", portPrefix + id + ".");
                if (result.ChatIds.Contains(nested.WorkflowInstance.StartExecutorId)) result.ChatIds.Add(id);
            }
            return graph;
        }
    }
}
