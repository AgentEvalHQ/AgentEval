// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;

namespace AgentEval.Skills;

/// <summary>
/// The result of walking a <see cref="ToolUsageReport"/>'s skill-tool calls (<c>load_skill</c> /
/// <c>read_skill_resource</c> / <c>run_skill_script</c>) in order and correlating each
/// read/run against a same-skill <c>load_skill</c> that preceded it.
/// </summary>
internal sealed record SkillDisclosureAnalysis(
    int LoadCount,
    int ReadCount,
    int RunCount,
    int UniqueSkillsLoaded,
    int RedundantLoads,
    IReadOnlyList<ToolCallRecord> OrphanedReads,
    IReadOnlyList<ToolCallRecord> OrphanedRuns)
{
    /// <summary>Total orphaned read/run calls (a <c>read_skill_resource</c> / <c>run_skill_script</c> not preceded by a same-skill <c>load_skill</c>).</summary>
    public int OrphanedCount => OrphanedReads.Count + OrphanedRuns.Count;
}

/// <summary>
/// Shared progressive-disclosure analysis used by BOTH <see cref="AgentEval.Assertions.SkillUsageAssertions.HaveDisclosedProgressively"/>
/// and <see cref="AgentEval.Metrics.Agentic.SkillDisclosureEfficiencyMetric"/> — one definition of
/// "orphaned" / "redundant" so the assertion and the metric can never disagree (reuse over reinvent).
/// </summary>
internal static class SkillDisclosureAnalyzer
{
    public static SkillDisclosureAnalysis Analyze(ToolUsageReport? report)
    {
        if (report is null || report.Count == 0)
            return new SkillDisclosureAnalysis(0, 0, 0, 0, 0, [], []);

        var ordered = report.Calls.OrderBy(c => c.Order).ToList();

        // Skills loaded so far (by name) at the current point in the call sequence — used to decide
        // whether a read/run is "satisfied" by a preceding load of the SAME skill.
        var loadedSoFar = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Total times each skill name has been loaded (for redundant-load counting).
        var loadCountsByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var orphanedReads = new List<ToolCallRecord>();
        var orphanedRuns = new List<ToolCallRecord>();
        var anyLoadSeen = false;
        int nLoad = 0, nRead = 0, nRun = 0;

        foreach (var call in ordered)
        {
            if (call.Name.Equals(SkillToolNames.LoadSkill, StringComparison.OrdinalIgnoreCase))
            {
                nLoad++;
                anyLoadSeen = true;
                var name = SkillArgumentExtraction.ExtractSkillName(call.Arguments);
                if (name is not null)
                {
                    loadedSoFar.Add(name);
                    loadCountsByName[name] = loadCountsByName.GetValueOrDefault(name) + 1;
                }
                continue;
            }

            var isRead = call.Name.Equals(SkillToolNames.ReadSkillResource, StringComparison.OrdinalIgnoreCase);
            var isRun = call.Name.Equals(SkillToolNames.RunSkillScript, StringComparison.OrdinalIgnoreCase);
            if (!isRead && !isRun)
                continue;

            if (isRead) nRead++; else nRun++;

            // Correlate by skill name when it can be determined; otherwise fall back to "was ANY skill
            // loaded yet" — a best-effort degradation rather than failing to analyze the call at all.
            var skillName = SkillArgumentExtraction.ExtractSkillName(call.Arguments);
            var satisfied = skillName is not null ? loadedSoFar.Contains(skillName) : anyLoadSeen;

            if (!satisfied)
            {
                if (isRead) orphanedReads.Add(call); else orphanedRuns.Add(call);
            }
        }

        var redundantLoads = loadCountsByName.Values.Where(c => c > 1).Sum(c => c - 1);

        return new SkillDisclosureAnalysis(
            nLoad, nRead, nRun, loadCountsByName.Count, redundantLoads, orphanedReads, orphanedRuns);
    }
}
