// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Tracing;

namespace AgentEval.Benchmarks;

/// <summary>
/// Reconciles an agent-boundary trace against a chat-boundary trace and emits the six Trace Fidelity
/// discrepancy classes (Glass Box). The chat-boundary trace is ground truth at the model interface; the
/// agent-boundary trace is the framework's self-report. Pure code — no LLM tokens (CostTier.Free).
/// </summary>
/// <remarks>
/// Reconciliation reads tool <em>calls</em> (<see cref="TraceEntry.ToolCalls"/>), per-turn finish reasons,
/// and token usage — never tool <em>definition schemas</em>, so tool-definition de-dup never affects fidelity.
/// Argument comparison is by serialized-string equality (a documented v1 heuristic). Finish reasons are compared
/// as reported strings, case-insensitively, so a framework that reports the same reason under another label is
/// counted as not reporting it.
/// </remarks>
public sealed class TraceFidelityRunner
{
    // Provider-side interventions on a model turn (filtered / cut off) that suppressed_finish_reason checks for.
    private static readonly string[] InterventionFinishReasons = { "content_filter", "length" };

    private readonly SamplePreset _preset;

    /// <summary>Creates a runner. The preset is informational for reconciliation (it does not change scoring).</summary>
    public TraceFidelityRunner(SamplePreset preset = SamplePreset.Standard) => _preset = preset;

    /// <summary>The capture preset this runner was configured with.</summary>
    public SamplePreset Preset => _preset;

    /// <summary>Reconciles an agent-boundary trace against a chat-boundary trace into a structured report.</summary>
    public TraceFidelityReport Reconcile(AgentTrace agentTrace, AgentTrace chatTrace)
    {
        ArgumentNullException.ThrowIfNull(agentTrace);
        ArgumentNullException.ThrowIfNull(chatTrace);

        var chatCalls = ToolCalls(chatTrace, chatBoundary: true);
        var agentCalls = ToolCalls(agentTrace, chatBoundary: false);

        var chatByName = chatCalls.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.ToList());
        var agentByName = agentCalls.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.ToList());

        var discrepancies = new List<TraceFidelityDiscrepancy>(TraceFidelityRubric.Classes.Count);
        discrepancies.Add(MissingToolCalls(chatByName, agentByName));
        discrepancies.Add(PhantomToolCalls(chatByName, agentByName));
        discrepancies.Add(ArgumentDrift(chatByName, agentByName));
        discrepancies.Add(HiddenRetries(chatByName, agentByName));
        discrepancies.Add(TokenUnderReporting(agentTrace, chatTrace));
        discrepancies.Add(SuppressedFinishReason(agentTrace, chatTrace));

        var root = 1.0 - discrepancies.Sum(d => TraceFidelityRubric.Weight(d.ClassKey) * (1.0 - d.Score));
        return new TraceFidelityReport(discrepancies, Math.Clamp(root, 0.0, 1.0));
    }

    /// <summary>
    /// Reconciles and projects the report onto the unified <see cref="EvalResult"/> tree — one SubResult per
    /// discrepancy class, scores normalized 0–1 with the 0–100 figure in <c>Dimensions["score100"]</c>.
    /// </summary>
    public EvalResult ReconcileToEvalResult(AgentTrace agentTrace, AgentTrace chatTrace)
    {
        var report = Reconcile(agentTrace, chatTrace);

        var subResults = report.Discrepancies.Select(d => new EvalResult(
            Metric: new EvalMetadata(Key: $"trace_fidelity.{d.ClassKey}", Name: d.ClassKey, Category: "TraceFidelity", Version: "1.0"),
            Score: new EvalScore(
                Value: d.Score, Ordinal: null,
                Label: d.Score >= 0.99 ? "pass" : d.Score >= 0.8 ? "warn" : "fail",
                Passed: d.Score >= 0.8, Threshold: 0.8, Severity: d.Severity, Confidence: null),
            Details: new EvalDetails(
                Dimensions: new Dictionary<string, double> { ["count"] = d.Count, ["score100"] = d.Score * 100 },
                Evidence: d.Examples.Select(x => new EvalEvidence(Source: "chat-vs-agent", Reference: d.ClassKey, Message: x)).ToList(),
                Recommendations: null, SubResults: null, AggregationStrategy: null),
            Provenance: new EvalProvenance(Type: "code", JudgeModel: null, PromptId: null, PromptHash: null, TokensUsed: null, EstimatedCost: 0.0, CacheHit: false),
            EvaluatedAt: DateTimeOffset.UtcNow)).ToList();

        return new EvalResult(
            Metric: new EvalMetadata(Key: "trace_fidelity", Name: "Trace Fidelity", Category: "TraceFidelity", Version: "1.0"),
            Score: new EvalScore(
                Value: report.OverallScore, Ordinal: null,
                Label: report.OverallScore >= 0.99 ? "pass" : report.OverallScore >= 0.8 ? "warn" : "fail",
                Passed: report.OverallScore >= 0.8, Threshold: 0.8,
                Severity: report.OverallScore >= 0.8 ? "Low" : report.OverallScore >= 0.5 ? "Medium" : "High", Confidence: null),
            Details: new EvalDetails(
                Dimensions: new Dictionary<string, double> { ["score100"] = report.OverallScore * 100 },
                Evidence: null, Recommendations: null, SubResults: subResults, AggregationStrategy: "severity-weighted"),
            Provenance: new EvalProvenance(Type: "code", JudgeModel: null, PromptId: null, PromptHash: null, TokensUsed: null, EstimatedCost: 0.0, CacheHit: false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }

    // ── discrepancy detectors ──
    private static TraceFidelityDiscrepancy MissingToolCalls(
        Dictionary<string, List<ToolCallRef>> chat, Dictionary<string, List<ToolCallRef>> agent)
    {
        var missing = chat.Keys.Where(n => !agent.ContainsKey(n)).ToList();
        return Build(TraceFidelityRubric.MissingToolCalls, missing.Count,
            missing.Select(n => $"tool '{n}' called at chat boundary but absent from the agent's account"));
    }

    private static TraceFidelityDiscrepancy PhantomToolCalls(
        Dictionary<string, List<ToolCallRef>> chat, Dictionary<string, List<ToolCallRef>> agent)
    {
        var phantom = agent.Keys.Where(n => !chat.ContainsKey(n)).ToList();
        return Build(TraceFidelityRubric.PhantomToolCalls, phantom.Count,
            phantom.Select(n => $"agent reported tool '{n}' which the model never requested"));
    }

    private static TraceFidelityDiscrepancy ArgumentDrift(
        Dictionary<string, List<ToolCallRef>> chat, Dictionary<string, List<ToolCallRef>> agent)
    {
        var examples = new List<string>();
        foreach (var name in chat.Keys.Where(agent.ContainsKey))
        {
            // Compare the DISTINCT arg sets — not ordered lists — so a retry (same args N times) is counted
            // by hidden_retries, not double-counted here. Drift = the two layers used genuinely different args.
            var chatArgs = chat[name].Select(c => c.Arguments ?? string.Empty).ToHashSet(StringComparer.Ordinal);
            var agentArgs = agent[name].Select(c => c.Arguments ?? string.Empty).ToHashSet(StringComparer.Ordinal);
            if (!chatArgs.SetEquals(agentArgs))
            {
                examples.Add($"tool '{name}' args differ: chat={{{string.Join(", ", chatArgs)}}} vs agent={{{string.Join(", ", agentArgs)}}}");
            }
        }

        return Build(TraceFidelityRubric.ArgumentDrift, examples.Count, examples);
    }

    private static TraceFidelityDiscrepancy HiddenRetries(
        Dictionary<string, List<ToolCallRef>> chat, Dictionary<string, List<ToolCallRef>> agent)
    {
        var examples = new List<string>();
        var count = 0;
        foreach (var name in chat.Keys.Where(agent.ContainsKey))
        {
            var extra = chat[name].Count - agent[name].Count;
            if (extra > 0)
            {
                count += extra;
                examples.Add($"tool '{name}' invoked {chat[name].Count}× at chat boundary but reported {agent[name].Count}× by the agent");
            }
        }

        return Build(TraceFidelityRubric.HiddenRetries, count, examples);
    }

    private static TraceFidelityDiscrepancy TokenUnderReporting(AgentTrace agentTrace, AgentTrace chatTrace)
    {
        var chatTokens = chatTrace.Entries
            .Where(e => e.EffectiveScope == TraceEntryScope.ChatTurn && e.Type == TraceEntryType.Response)
            .Sum(e => e.TokenUsage?.TotalTokens ?? 0);
        var agentTokens = agentTrace.Performance?.TotalTokens
            ?? agentTrace.Entries.Where(e => e.Type == TraceEntryType.Response).Sum(e => e.TokenUsage?.TotalTokens ?? 0);

        if (chatTokens == 0 && agentTokens == 0)
        {
            return Build(TraceFidelityRubric.TokenUnderReporting, 0, Array.Empty<string>());
        }

        // Directional: the class is "token UNDER-reporting", so flag only when the agent boundary reports
        // FEWER tokens than were actually consumed at the chat boundary (chat > agent). Over-reporting
        // (agent > chat) is a different, non-deceptive discrepancy and is not penalised here.
        var denom = Math.Max(chatTokens, 1);
        var shortfall = (chatTokens - agentTokens) / (double)denom;
        var count = shortfall > TraceFidelityRubric.TokenToleranceFraction ? 1 : 0;
        var examples = count > 0
            ? new[] { $"agent under-reports tokens: chat={chatTokens}, agent={agentTokens} ({shortfall:P1} short, tolerance {TraceFidelityRubric.TokenToleranceFraction:P0})" }
            : Array.Empty<string>();
        return Build(TraceFidelityRubric.TokenUnderReporting, count, examples);
    }

    private static TraceFidelityDiscrepancy SuppressedFinishReason(AgentTrace agentTrace, AgentTrace chatTrace)
    {
        var chatTurns = chatTrace.Entries
            .Where(e => e.EffectiveScope == TraceEntryScope.ChatTurn && e.Type == TraceEntryType.Response)
            .ToList();

        // Agent boundary: the finish reason(s) the framework reported, read from any Response entry (the same
        // entries the agent-side tool calls are read from). A missing reason stays null — it reported none.
        var agentReasons = agentTrace.Entries
            .Where(e => e.Type == TraceEntryType.Response)
            .Select(e => e.FinishReason)
            .ToList();

        // A chat turn that ended in content_filter/length is suppressed only when the agent boundary did NOT report
        // that same reason (it reported stop, another reason, or none). Turns cannot be paired across the layers by
        // index (agent entries are per invocation, chat entries per model round-trip), so reconcile by count per
        // reason: each agent-boundary report of a reason accounts for one chat turn that ended with it. Directional,
        // like token_under_reporting — the agent reporting a reason the chat boundary never saw is not counted here.
        var count = 0;
        var examples = new List<string>();
        foreach (var reason in InterventionFinishReasons)
        {
            var turns = chatTurns
                .Where(e => string.Equals(e.FinishReason, reason, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Index)
                .ToList();
            var reported = agentReasons.Count(r => string.Equals(r, reason, StringComparison.OrdinalIgnoreCase));
            var unreflected = turns.Count - reported;
            if (unreflected > 0)
            {
                count += unreflected;
                examples.Add($"'{reason}' ended {turns.Count} chat turn(s) (turn {string.Join(", ", turns)}) but the agent boundary reported it {reported}× (agent reported: {AgentFinishLabel(agentReasons)})");
            }
        }

        return Build(TraceFidelityRubric.SuppressedFinishReason, count, examples);
    }

    // Renders the agent boundary's reported finish reasons for evidence. A missing reason is shown as <null>, not
    // an empty string, because "reported none" is itself the suppression signal.
    private static string AgentFinishLabel(IReadOnlyList<string?> agentReasons)
        => agentReasons.Count == 0
            ? "no response entries"
            : string.Join(", ", agentReasons.Select(r => r is null ? "<null>" : $"'{r}'").Distinct(StringComparer.Ordinal));

    private static TraceFidelityDiscrepancy Build(string classKey, int count, IEnumerable<string> examples)
        => new(classKey, TraceFidelityRubric.Severity(classKey), count, TraceFidelityRubric.ChildValue(classKey, count), examples.ToList());

    private static List<ToolCallRef> ToolCalls(AgentTrace trace, bool chatBoundary)
    {
        // Chat boundary: only ChatTurn responses. Agent boundary: any response entry's reported tool calls.
        var entries = trace.Entries.Where(e => e.Type == TraceEntryType.Response);
        if (chatBoundary)
        {
            entries = entries.Where(e => e.EffectiveScope == TraceEntryScope.ChatTurn);
        }

        return entries
            .Where(e => e.ToolCalls is not null)
            .SelectMany(e => e.ToolCalls!)
            .Select(c => new ToolCallRef(c.Name, c.Arguments))
            .ToList();
    }

    private readonly record struct ToolCallRef(string Name, string? Arguments);
}
