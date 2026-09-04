// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Models;

/// <summary>
/// Report of all tool usage during an agent run.
/// </summary>
public class ToolUsageReport
{
    private readonly List<ToolCallRecord> _calls = [];
    private HashSet<string>? _availableTools;

    /// <summary>
    /// The tools the agent could have called, when the harness knows them; <see langword="null"/>
    /// when the inventory was never declared.
    /// </summary>
    /// <remarks>
    /// This is what separates "the agent was offered a dangerous tool and refrained" from "the tool
    /// was never on the table". Without it a negative policy such as <c>NeverCallTool(X)</c> cannot
    /// fail and so carries no information — see <see cref="WasToolAvailable"/>.
    /// </remarks>
    public IReadOnlyCollection<string>? AvailableTools => _availableTools;

    /// <summary>
    /// Declares which tools the agent was offered, making negative tool policies decidable.
    /// </summary>
    /// <param name="toolNames">The tool names registered with the agent. Matched case-insensitively.</param>
    public void DeclareAvailableTools(IEnumerable<string> toolNames)
    {
        ArgumentNullException.ThrowIfNull(toolNames);
        _availableTools ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in toolNames)
        {
            if (!string.IsNullOrWhiteSpace(name)) _availableTools.Add(name);
        }
    }

    /// <summary>
    /// Whether the agent could have called <paramref name="toolName"/>:
    /// <see langword="true"/> if it was declared available (or was in fact called),
    /// <see langword="false"/> if an inventory was declared and does not contain it, and
    /// <see langword="null"/> when no inventory was declared, i.e. <b>unknown</b>.
    /// </summary>
    /// <param name="toolName">The tool name to look up (case-insensitive).</param>
    /// <remarks>
    /// A <see langword="null"/> here is not a "no". Treat it as missing evidence: an assertion that
    /// depends on availability is undecidable, not passing.
    /// </remarks>
    public bool? WasToolAvailable(string toolName)
    {
        if (WasToolCalled(toolName)) return true;
        return _availableTools?.Contains(toolName);
    }


    /// <summary>
    /// How many approval-gated tool calls the extractor <b>saw and did not record</b> because
    /// approval-aware extraction was not opted into (or the gated call was not a function call).
    /// <c>0</c> when nothing was dropped.
    /// </summary>
    /// <remarks>
    /// ADR-030 Slice 0.5 (defect D-e). MAF wraps an approval-required call in a
    /// <c>ToolApprovalRequestContent</c> and emits no <c>FunctionCallContent</c> for it, so the default
    /// extractor is blind to it — and a blind report is indistinguishable from an empty one. This count
    /// is what makes the blindness loud: a non-zero value means <see cref="Calls"/> is incomplete and any
    /// absence-based policy (<c>NeverCallTool</c>) evaluated on this report has a chance floor of 1.0.
    /// Opt in with <c>ToolUsageExtractor.Extract(rawMessages, includeApprovalGatedCalls: true)</c> or
    /// <c>EvaluationOptions.IncludeApprovalGatedToolCalls</c> to record them instead.
    /// </remarks>
    public int DroppedApprovalRequestCount { get; init; }

    /// <summary>All tool calls in order of invocation.</summary>
    public IReadOnlyList<ToolCallRecord> Calls => _calls;
    
    /// <summary>Number of tool calls made.</summary>
    public int Count => _calls.Count;
    
    /// <summary>Names of all tools called (in order, may have duplicates).</summary>
    public IEnumerable<string> ToolNames => _calls.Select(c => c.Name);
    
    /// <summary>Unique tool names called.</summary>
    public IEnumerable<string> UniqueToolNames => _calls.Select(c => c.Name).Distinct();
    
    /// <summary>Whether any tool call resulted in an error.</summary>
    public bool HasErrors => _calls.Any(c => c.HasError);
    
    /// <summary>Total time spent in tool execution (for calls with timing).</summary>
    public TimeSpan TotalToolTime => TimeSpan.FromTicks(
        _calls.Where(c => c.HasTiming).Sum(c => c.Duration!.Value.Ticks));
    
    /// <summary>Add a tool call to the report.</summary>
    public void AddCall(ToolCallRecord call) => _calls.Add(call);
    
    /// <summary>Get all calls to a specific tool (case-insensitive).</summary>
    public IEnumerable<ToolCallRecord> GetCallsByName(string toolName) =>
        _calls.Where(c => c.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase));
    
    /// <summary>Check if a tool was called (case-insensitive).</summary>
    public bool WasToolCalled(string toolName) =>
        _calls.Any(c => c.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase));
    
    /// <summary>Get the order position of a tool's first call (1-based, 0 if not called).</summary>
    public int GetToolOrder(string toolName) =>
        _calls.FirstOrDefault(c => c.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase))?.Order ?? 0;
    
    public override string ToString()
    {
        if (Count == 0)
            return "No tools called";
        return $"{Count} tool(s): {string.Join(" → ", ToolNames)}";
    }
}
