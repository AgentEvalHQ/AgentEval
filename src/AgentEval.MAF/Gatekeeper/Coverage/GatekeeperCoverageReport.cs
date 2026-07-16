// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// The full tool-protection-coverage report for one agent, as produced by <see cref="GatekeeperCoverageAnalyzer"/>.
/// This is the "false assurance" fix the Gatekeeper hardening review calls out as the single highest-leverage
/// item: it makes the true shape of the protection boundary visible instead of implicit in doc comments.
/// </summary>
/// <param name="Tools">One entry per tool exposed to the agent's model.</param>
/// <param name="RegisteredToolGateNames">The <see cref="IToolGate.PolicyName"/> of every tool gate that was registered when this report was produced.</param>
/// <param name="ToolInventoryAvailable">
/// Whether the tool list could actually be read off the agent. <see langword="false"/> for an <see cref="Microsoft.Agents.AI.AIAgent"/>
/// that does not expose <see cref="Microsoft.Extensions.AI.ChatOptions"/> via <c>GetService</c> (e.g. a
/// custom, non-<c>ChatClientAgent</c> agent type) — in that case <see cref="Tools"/> is empty and the
/// coverage percentage is meaningless; call the <c>Analyze(IEnumerable&lt;AITool&gt;, …)</c> overload with an
/// explicit tool list instead. NOTE: <see langword="true"/> does NOT mean the inventory is complete — a tool
/// contributed dynamically by an <see cref="Microsoft.Agents.AI.AIContextProvider"/> (Agent Skills, a memory
/// provider) never appears in <c>ChatOptions.Tools</c> and so is silently absent from <see cref="Tools"/> even
/// when this flag is <see langword="true"/>. See <see cref="GatekeeperCoverageAnalyzer"/> remarks.
/// </param>
public sealed record GatekeeperCoverageReport(
    IReadOnlyList<ToolCoverageEntry> Tools,
    IReadOnlyList<string> RegisteredToolGateNames,
    bool ToolInventoryAvailable)
{
    /// <summary>Tools whose calls are locally interceptable (pass through MAF's function-invocation seam at all).</summary>
    public int InterceptedLocalFunctionCount => Tools.Count(t => t.ExecutionModel == ToolExecutionModel.InterceptedLocalFunction);

    /// <summary>Tools executed entirely by the model provider — never seen by any Gatekeeper tool gate.</summary>
    public int ProviderHostedOpaqueCount => Tools.Count(t => t.ExecutionModel == ToolExecutionModel.ProviderHostedOpaque);

    /// <summary>Tools whose execution model this analyzer could not classify.</summary>
    public int UnknownExecutionModelCount => Tools.Count(t => t.ExecutionModel == ToolExecutionModel.UnknownExecutionModel);

    /// <summary>Tools that pass through at least one registered tool gate.</summary>
    public int ProtectedCount => Tools.Count(t => t.IsGateProtected);

    /// <summary>Tools the heuristic classified as high-risk (mutating / financial / exfiltrating).</summary>
    public int HighRiskCount => Tools.Count(t => t.RiskLevel == ToolRiskLevel.HighRisk);

    /// <summary>High-risk tools with zero protecting gate — the report's headline number.</summary>
    public int HighRiskUnprotectedCount => Tools.Count(t => t.IsUnprotectedHighRisk);

    /// <summary>True when at least one high-risk tool has zero protecting gate.</summary>
    public bool HasUnprotectedHighRiskTools => HighRiskUnprotectedCount > 0;

    /// <summary>
    /// The fraction of tools whose calls pass through at least one registered tool gate, as a percentage.
    /// A STRUCTURAL measure (is the interception seam wired up at all for this tool), not a semantic one (does
    /// some gate specifically defend it) — see <see cref="ToolCoverageEntry.IsGateProtected"/>. 100 when there
    /// are no tools (vacuously fully covered) or when the tool inventory could not be read (see
    /// <see cref="ToolInventoryAvailable"/> — check that flag before trusting this number).
    /// </summary>
    public double EnforcementCoveragePercent => Tools.Count == 0 ? 100.0 : 100.0 * ProtectedCount / Tools.Count;

    /// <summary>Renders a human-readable, terminal-friendly report — the shape shown in the Gatekeeper hardening review's own examples.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        if (!ToolInventoryAvailable)
        {
            sb.AppendLine(
                "Gatekeeper coverage report — tool inventory UNAVAILABLE (this agent does not expose ChatOptions " +
                "via GetService; pass an explicit tool list to GatekeeperCoverageAnalyzer.Analyze instead).");
            return sb.ToString();
        }

        // Local: ProtectedCount (and the other Tools.Count(...) properties) each do a fresh O(n) scan on every
        // access — cache the ones used more than once in this single Render() call instead of re-scanning.
        var protectedCount = ProtectedCount;
        var coveragePercent = Tools.Count == 0 ? 100.0 : 100.0 * protectedCount / Tools.Count;
        sb.AppendLine(FormattableString.Invariant(
            $"Gatekeeper coverage report — {Tools.Count} tool(s), {coveragePercent:F0}% enforcement coverage ({protectedCount}/{Tools.Count} protected), {HighRiskCount} high-risk, {HighRiskUnprotectedCount} high-risk UNPROTECTED"));

        foreach (var t in Tools.OrderBy(t => t.IsUnprotectedHighRisk ? 0 : t.IsGateProtected ? 2 : 1).ThenBy(t => t.ToolName, StringComparer.Ordinal))
        {
            var mark = t.IsUnprotectedHighRisk ? "⚠" : t.IsGateProtected ? "✅" : "•";
            var risk = t.RiskLevel == ToolRiskLevel.HighRisk ? "HighRisk" : "Standard";
            var status = t.IsGateProtected ? "protected" : "UNPROTECTED";
            sb.AppendLine(FormattableString.Invariant(
                $"  {mark} {t.ToolName,-28} [{ModelTag(t.ExecutionModel)}] {risk,-8} {status} — {t.Note}"));
        }

        if (RegisteredToolGateNames.Count > 0)
        {
            sb.AppendLine($"Registered tool gates: {string.Join(", ", RegisteredToolGateNames)}");
        }
        else
        {
            sb.AppendLine("Registered tool gates: (none)");
        }

        return sb.ToString();
    }

    private static string ModelTag(ToolExecutionModel model) => model switch
    {
        ToolExecutionModel.InterceptedLocalFunction => "local:function",
        ToolExecutionModel.ProviderHostedOpaque => "provider:hosted",
        _ => "unknown",
    };
}
