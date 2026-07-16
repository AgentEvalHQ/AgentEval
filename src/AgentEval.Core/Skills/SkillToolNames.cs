// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Skills;

/// <summary>
/// The three stable tool names MAF's <c>AgentSkillsProvider</c> registers (Agent Skills GA'd
/// 2026-07-07), plus their argument parameter names — the <b>single</b> place the string contract
/// with MAF lives. Referenced by <see cref="AgentEval.Assertions.SkillUsageAssertions"/> and the
/// progressive-disclosure efficiency metric, so a MAF rename is a one-line change here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tool names</b> — doc/devblog-confirmed at GA (Microsoft Learn <c>agent-framework/agents/skills</c>,
/// the <c>AgentSkillsProvider.LoadSkillToolName</c> / <c>ReadSkillResourceToolName</c> /
/// <c>RunSkillScriptToolName</c> public static fields) and independently verified this session against
/// the live <c>Microsoft.Agents.AI 1.13.0</c> assembly.
/// </para>
/// <para>
/// <b>Argument names</b> — the design doc that scoped this feature
/// (<c>strategy/FutureFeatures/Skills/AgentEval-AgentSkills-Evals-Design-and-Plan.md</c>) flagged these as
/// unverified (open item "(a)") with a value-based-matching fallback. They have since been <b>verified
/// against the live MAF 1.13.0 assembly</b> (reflection over <c>AgentSkillsProvider</c>'s private
/// <c>BuildTools</c> method, inspecting the real emitted <c>AIFunction.JsonSchema</c>
/// for all three tools built from a real file-based skill):
/// <code>
/// load_skill:           {"skillName"}
/// read_skill_resource:  {"skillName", "resourceName"}
/// run_skill_script:     {"skillName", "scriptName", "arguments"}
/// </code>
/// The guessed names in the design doc were correct. The assertions and metric in this namespace still
/// use <b>value-based, key-agnostic</b> matching (see <c>SkillArgumentExtraction</c>) as defense-in-depth
/// against a future MAF rename — the whole point of pinning the contract to one file.
/// </para>
/// <para>
/// Also verified this session (open items (c) and (d) from the same design doc): <c>AgentSkillsProvider</c>
/// exposes <b>no</b> provider-level <c>GetSkillsAsync</c> convenience — only the source-level
/// <c>AgentSkillsSource.GetSkillsAsync(AgentSkillsSourceContext, CancellationToken)</c> — so a future P2
/// scanner must enumerate at the source level. And <c>read_skill_resource</c>'s <c>resourceName</c> is a
/// <b>logical name</b> resolved by MAF against the skill's resource list discovered at load time (e.g.
/// <c>"resources/policy.md"</c>), not a live filesystem path: <c>AgentSkill.GetResourceAsync</c> does an
/// exact-string lookup against that pre-enumerated list, so a traversal attempt ("..", an absolute path, a
/// bare filename with the directory prefix stripped) simply returns "not found" rather than escaping to
/// arbitrary content — confirmed empirically against the live assembly with a real skill fixture. A future
/// P3 <c>SkillResourcePathGate</c> would guard no live threat and should be dropped, per the design doc's
/// own documented fallback for that item.
/// </para>
/// </remarks>
public static class SkillToolNames
{
    /// <summary>Loads the full content of a specific skill. Always registered when any skill is present.</summary>
    public const string LoadSkill = "load_skill";

    /// <summary>Reads a resource (reference, asset, or dynamic data) associated with a skill. Registered only when at least one skill has resources.</summary>
    public const string ReadSkillResource = "read_skill_resource";

    /// <summary>Runs a script associated with a skill. Registered only when at least one skill has scripts.</summary>
    public const string RunSkillScript = "run_skill_script";

    /// <summary>All three stable skill tool names, in advertised order (load → read → run).</summary>
    public static readonly IReadOnlyList<string> All = [LoadSkill, ReadSkillResource, RunSkillScript];

    /// <summary>The <c>skillName</c> argument, present on all three tools. Verified against MAF 1.13.0.</summary>
    public const string SkillNameArg = "skillName";

    /// <summary>The <c>resourceName</c> argument on <see cref="ReadSkillResource"/>. Verified against MAF 1.13.0.</summary>
    public const string ResourceNameArg = "resourceName";

    /// <summary>The <c>scriptName</c> argument on <see cref="RunSkillScript"/>. Verified against MAF 1.13.0.</summary>
    public const string ScriptNameArg = "scriptName";

    /// <summary>
    /// The <c>arguments</c> argument on <see cref="RunSkillScript"/> — the nested object holding the
    /// script's own parameters (e.g. <c>{"amount": 200}</c>). Confirmed against a live captured trace
    /// this session (Skills Phase 1 sample, Run 2): <c>run_skill_script({"skillName":...,
    /// "scriptName":...,"arguments":{"amount":200}})</c>.
    /// </summary>
    public const string ScriptArgumentsArg = "arguments";
}
