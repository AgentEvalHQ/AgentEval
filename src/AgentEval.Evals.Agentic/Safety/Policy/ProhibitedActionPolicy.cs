// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals.Agentic.Safety.Policy;

/// <summary>
/// Immutable policy record that declares which agent actions are prohibited,
/// which require prior approval, and what content patterns must never appear.
/// <para>
/// Consumed by <c>ProhibitedActionsEval</c> as the primary (deterministic)
/// enforcement layer — policy violations are detected without an LLM call.
/// </para>
/// <para>
/// Source: plan-05 §8.5 (ProhibitedActionsEvaluator implementation card).
/// No direct Foundry prompty equivalent; Foundry describes policy inline in the
/// LLM prompt. Here policy is code-first; the LLM is a fallback for nuance only.
/// </para>
/// </summary>
/// <param name="ForbiddenTools">
/// Names of tools that must never be called by the agent under any circumstances.
/// A match here produces a <c>severity: critical</c> violation deterministically.
/// Comparison is case-insensitive.
/// </param>
/// <param name="ForbiddenToolCallPatterns">
/// Pattern-based prohibitions: a tool call is forbidden when its name matches
/// <see cref="ToolPattern.ToolName"/> AND its serialised arguments match
/// <see cref="ToolPattern.ArgumentRegex"/>. The severity of each pattern is
/// declared by <see cref="ToolPattern.FailureSeverity"/>.
/// </param>
/// <param name="RequiredApprovalTools">
/// Tools that must be accompanied by an explicit approval call before invocation.
/// If the agent invokes any of these tools without a preceding approval, the
/// evaluator reports a <c>severity: high</c> violation.
/// Comparison is case-insensitive.
/// </param>
/// <param name="ForbiddenContent">
/// Keywords or regex patterns that must not appear in the agent's response or
/// tool-call arguments. The LLM fallback path handles nuanced ForbiddenContent
/// checks (e.g., paraphrased prohibited information); simple keyword matches are
/// handled deterministically.
/// </param>
/// <param name="ApprovalToolNames">
/// Exact tool names (case-insensitive) that count as an "approval" call when they
/// appear before a <see cref="RequiredApprovalTools"/> tool in the call sequence.
/// When null/empty, defaults to <c>["approve", "confirm"]</c> matched as exact
/// names (NOT as substrings — a tool named <c>disapprove_log</c> does NOT count
/// as approval). Set explicitly to enumerate the approval primitives your policy
/// recognises (e.g. <c>["request_user_approval", "manager_sign_off"]</c>).
/// </param>
public sealed record ProhibitedActionPolicy(
    IReadOnlyList<string> ForbiddenTools,
    IReadOnlyList<ToolPattern> ForbiddenToolCallPatterns,
    IReadOnlyList<string> RequiredApprovalTools,
    IReadOnlyList<string> ForbiddenContent,
    IReadOnlyList<string>? ApprovalToolNames = null);

/// <summary>
/// A pattern-based prohibition that matches a specific combination of tool name
/// and argument content.
/// </summary>
/// <param name="ToolName">
/// The tool name to match. Case-insensitive exact match.
/// </param>
/// <param name="ArgumentRegex">
/// A .NET regular expression applied to the JSON-serialised arguments of the tool
/// call. A match means the pattern is triggered. Use <c>.*</c> to match any call
/// to the named tool regardless of arguments (same as adding the tool to
/// <see cref="ProhibitedActionPolicy.ForbiddenTools"/>).
/// </param>
/// <param name="FailureSeverity">
/// The severity to emit when this pattern matches.
/// Accepted values: <c>"low"</c>, <c>"medium"</c>, <c>"high"</c>, <c>"critical"</c>.
/// </param>
public sealed record ToolPattern(
    string ToolName,
    string ArgumentRegex,
    string FailureSeverity);
