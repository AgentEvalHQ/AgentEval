// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using AgentEval.Models;
using AgentEval.Skills;

namespace AgentEval.Assertions;

/// <summary>
/// Fluent assertion sugar for MAF Agent Skills (<c>load_skill</c> / <c>read_skill_resource</c> /
/// <c>run_skill_script</c> — GA'd 2026-07-07). Thin, additive extension methods over the existing
/// public <see cref="ToolUsageAssertions"/> / <see cref="ToolCallAssertion"/> — zero new MAF-type
/// coupling, zero edits to the base assertion classes. Skill activity is ordinary tool calls, already
/// captured name-based by <c>ToolUsageExtractor</c>; these extensions add skill-aware VALUE matching
/// (skill/resource/script name) on top of the existing tool-NAME assertions.
/// </summary>
/// <remarks>
/// <see cref="SkillToolNames"/> is the single place the string contract with MAF (tool + argument
/// names) lives; see its remarks for what was verified against the live MAF 1.13.0 assembly this
/// session vs. what is doc/devblog-confirmed.
/// </remarks>
/// <example>
/// <code>
/// result.ToolUsage!.Should()
///     .HaveLoadedSkill("expense-report")
///     .And().HaveReadSkillResource("expense-report").AfterTool(SkillToolNames.LoadSkill)
///     .And().HaveDisclosedProgressively()
///     .And().NotHaveRunSkillScript(because: "the summary task must not execute skill scripts");
/// </code>
/// </example>
public static class SkillUsageAssertions
{
    /// <summary>
    /// Assert that <c>load_skill</c> was called — optionally for a specific <paramref name="skillName"/>
    /// (value-matched against the <c>skillName</c> argument; case-insensitive). Wraps
    /// <see cref="ToolUsageAssertions.HaveCalledTool"/> when no skill name filter is given.
    /// </summary>
    /// <param name="a">The tool-usage assertions instance.</param>
    /// <param name="skillName">When supplied, only a <c>load_skill</c> call whose <c>skillName</c> argument matches this value satisfies the assertion.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public static ToolCallAssertion HaveLoadedSkill(this ToolUsageAssertions a, string? skillName = null, string? because = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        var filters = BuildFilters((SkillToolNames.SkillNameArg, skillName));
        return AssertSkillToolCall(a, SkillToolNames.LoadSkill, filters, because);
    }

    /// <summary>
    /// Assert that <c>read_skill_resource</c> was called — optionally filtered by
    /// <paramref name="skillName"/> and/or <paramref name="resourceName"/> (value-matched;
    /// case-insensitive). Wraps <see cref="ToolUsageAssertions.HaveCalledTool"/> when no filters are given.
    /// </summary>
    /// <param name="a">The tool-usage assertions instance.</param>
    /// <param name="skillName">When supplied, only a call whose <c>skillName</c> argument matches this value qualifies.</param>
    /// <param name="resourceName">When supplied, only a call whose <c>resourceName</c> argument matches this value qualifies.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public static ToolCallAssertion HaveReadSkillResource(this ToolUsageAssertions a, string? skillName = null, string? resourceName = null, string? because = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        var filters = BuildFilters(
            (SkillToolNames.SkillNameArg, skillName),
            (SkillToolNames.ResourceNameArg, resourceName));
        return AssertSkillToolCall(a, SkillToolNames.ReadSkillResource, filters, because);
    }

    /// <summary>
    /// Assert that <c>run_skill_script</c> was called — optionally filtered by
    /// <paramref name="skillName"/> and/or <paramref name="scriptName"/> (value-matched;
    /// case-insensitive). Wraps <see cref="ToolUsageAssertions.HaveCalledTool"/> when no filters are given.
    /// </summary>
    /// <param name="a">The tool-usage assertions instance.</param>
    /// <param name="skillName">When supplied, only a call whose <c>skillName</c> argument matches this value qualifies.</param>
    /// <param name="scriptName">When supplied, only a call whose <c>scriptName</c> argument matches this value qualifies.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public static ToolCallAssertion HaveRunSkillScript(this ToolUsageAssertions a, string? skillName = null, string? scriptName = null, string? because = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        var filters = BuildFilters(
            (SkillToolNames.SkillNameArg, skillName),
            (SkillToolNames.ScriptNameArg, scriptName));
        return AssertSkillToolCall(a, SkillToolNames.RunSkillScript, filters, because);
    }

    /// <summary>
    /// Safety policy: assert <c>run_skill_script</c> was NEVER called. Maps directly to
    /// <see cref="ToolUsageAssertions.NeverCallTool"/> — use for tasks where skill code execution must
    /// not occur (e.g. a read-only summarization task).
    /// </summary>
    /// <param name="a">The tool-usage assertions instance.</param>
    /// <param name="because">Required reason for the policy (shown in failure message and audit trail).</param>
    [StackTraceHidden]
    public static ToolUsageAssertions NotHaveRunSkillScript(this ToolUsageAssertions a, string because)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(because);
        return a.NeverCallTool(SkillToolNames.RunSkillScript, because);
    }

    /// <summary>
    /// Assert progressive-disclosure ordering: every <c>read_skill_resource</c> / <c>run_skill_script</c>
    /// call must be preceded (by <see cref="ToolCallRecord.Order"/>) by a <c>load_skill</c> call for the
    /// SAME skill — the funnel can't skip a stage. When a call's <c>skillName</c> argument can't be
    /// determined, falls back to requiring only that SOME <c>load_skill</c> call preceded it. Every
    /// orphaned call is reported (not just the first) — matching the multi-violation reporting style of
    /// <see cref="ToolUsageAssertions.NeverPassArgumentMatching"/>.
    /// </summary>
    /// <param name="a">The tool-usage assertions instance.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public static ToolUsageAssertions HaveDisclosedProgressively(this ToolUsageAssertions a, string? because = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        var report = a.Report;
        var analysis = SkillDisclosureAnalyzer.Analyze(report);

        foreach (var call in analysis.OrphanedReads.Concat(analysis.OrphanedRuns).OrderBy(c => c.Order))
        {
            var skillName = SkillArgumentExtraction.ExtractSkillName(call.Arguments);
            var message = skillName is not null
                ? $"Progressive disclosure violated: '{call.Name}' for skill '{skillName}' (call #{call.Order}) was not preceded by '{SkillToolNames.LoadSkill}' for that skill."
                : $"Progressive disclosure violated: '{call.Name}' (call #{call.Order}) was not preceded by any '{SkillToolNames.LoadSkill}' call.";

            AgentEvalScope.FailWith(
                ToolAssertionException.Create(
                    message,
                    toolName: call.Name,
                    calledTools: report.UniqueToolNames.ToList(),
                    expected: skillName is not null
                        ? $"'{SkillToolNames.LoadSkill}' for skill '{skillName}' before '{call.Name}' (#{call.Order})"
                        : $"'{SkillToolNames.LoadSkill}' before '{call.Name}' (#{call.Order})",
                    actual: $"No qualifying '{SkillToolNames.LoadSkill}' call precedes call #{call.Order}",
                    because: because));
        }

        return a;
    }

    // ------------------------------------------------------------------------------------------
    // Shared implementation
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Finds the first call to <paramref name="toolName"/> whose arguments match every
    /// (argKey, expectedValue) pair in <paramref name="filters"/> (value-based, via
    /// <see cref="SkillArgumentExtraction.ValueMatches"/>). With no filters this simply delegates to
    /// <see cref="ToolUsageAssertions.HaveCalledTool"/> (identical failure semantics/messages).
    /// On failure (tool never called, or called but no call matches every filter), records a
    /// <see cref="ToolAssertionException"/> via <see cref="AgentEvalScope.FailWith(AgentEvalAssertionException)"/>
    /// and returns a null-<c>call</c> <see cref="ToolCallAssertion"/> so chained assertions short-circuit
    /// gracefully under scope mode (matching <c>HaveCalledTool</c>'s own soft-failure convention).
    /// </summary>
    [StackTraceHidden]
    private static ToolCallAssertion AssertSkillToolCall(
        ToolUsageAssertions a, string toolName, (string ArgKey, string ExpectedValue)[] filters, string? because)
    {
        if (filters.Length == 0)
            return a.HaveCalledTool(toolName, because);

        var report = a.Report;
        var calls = report.GetCallsByName(toolName).OrderBy(c => c.Order).ToList();
        var match = calls.FirstOrDefault(c =>
            filters.All(f => SkillArgumentExtraction.ValueMatches(c.Arguments, f.ArgKey, f.ExpectedValue)));

        if (match is null)
        {
            var filterDescription = string.Join(", ", filters.Select(f => $"{f.ArgKey}='{f.ExpectedValue}'"));
            AgentEvalScope.FailWith(
                ToolAssertionException.Create(
                    calls.Count == 0
                        ? $"Expected tool '{toolName}' to be called ({filterDescription}), but '{toolName}' was never called."
                        : $"Expected tool '{toolName}' to be called with {filterDescription}, but no matching call was found.",
                    toolName: toolName,
                    calledTools: report.UniqueToolNames.ToList(),
                    expected: $"'{toolName}' called with {filterDescription}",
                    actual: calls.Count == 0
                        ? $"'{toolName}' never called"
                        : $"{calls.Count} call(s) to '{toolName}', none matched {filterDescription}",
                    because: because));
            return new ToolCallAssertion(a, report, null, toolName);
        }

        return new ToolCallAssertion(a, report, match, toolName);
    }

    private static (string ArgKey, string ExpectedValue)[] BuildFilters(params (string ArgKey, string? Value)[] candidates) =>
        candidates.Where(c => c.Value is not null).Select(c => (c.ArgKey, c.Value!)).ToArray();
}
