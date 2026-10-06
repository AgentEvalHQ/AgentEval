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
/// custom, non-<c>ChatClientAgent</c> agent type), and for an agent with a dynamic tool provider and no static
/// tools — in both cases <see cref="Tools"/> is empty and the coverage percentage is meaningless; call the
/// <c>Analyze(IEnumerable&lt;AITool&gt;, …)</c> overload with an explicit tool list instead. NOTE:
/// <see langword="true"/> does NOT mean the inventory is complete — a tool contributed dynamically by an
/// <see cref="Microsoft.Agents.AI.AIContextProvider"/> (Agent Skills, a memory provider) never appears in
/// <c>ChatOptions.Tools</c> and so is absent from <see cref="Tools"/> even when this flag is <see langword="true"/>.
/// When the analyzer detects such a provider (or it is declared through
/// <see cref="AnalyzeOptions.HasDynamicToolProvider"/>), <see cref="Render"/> and <see cref="object.ToString"/> carry a
/// warning that injected tools were not inventoried; an undetected, undeclared provider cannot be warned about. See
/// <see cref="GatekeeperCoverageAnalyzer"/> remarks.
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
    /// some gate specifically defend it) — see <see cref="ToolCoverageEntry.IsGateProtected"/>.
    /// <para><b>100 is not always a measurement.</b> When <see cref="Tools"/> is empty — no tools in the inventory, or
    /// the inventory could not be read (<see cref="ToolInventoryAvailable"/> is <see langword="false"/>) — this
    /// returns 100 by convention, kept for compatibility, although nothing was measured. Check
    /// <see cref="ToolInventoryAvailable"/> and <c>Tools.Count</c> before reading it. <see cref="Render"/> and
    /// <see cref="ToString"/> print "not measured" in those cases instead of a percentage, and say so when tools a
    /// dynamic tool provider injects at invocation time were not inventoried (the figure then covers the static tools
    /// only).</para>
    /// </summary>
    public double EnforcementCoveragePercent => Tools.Count == 0 ? 100.0 : 100.0 * ProtectedCount / Tools.Count;

    /// <summary>
    /// Set by <see cref="GatekeeperCoverageAnalyzer"/> when the agent has a dynamic tool provider (an
    /// <see cref="Microsoft.Agents.AI.AIContextProvider"/> detected on the agent, or declared through
    /// <see cref="AnalyzeOptions.HasDynamicToolProvider"/>). The tools such a provider injects at invocation time are
    /// never in <see cref="Tools"/>, so every figure in this report covers the static tools only. Internal so the
    /// frozen public surface is unchanged; <see cref="Render"/> and <see cref="ToString"/> print it as a warning.
    /// </summary>
    internal bool DynamicToolsNotInventoried { get; init; }

    /// <summary>
    /// Renders a human-readable, terminal-friendly report — the shape shown in the Gatekeeper hardening review's own
    /// examples. Never prints a coverage percentage that was not measured: an empty inventory reads "not measurable",
    /// an unreadable one reads "UNAVAILABLE", a partial percentage is rounded down (so 299 of 300 protected never
    /// prints as 100%), and a report that could not inventory dynamically injected tools carries a warning line.
    /// </summary>
    public string Render()
    {
        var sb = new StringBuilder();
        if (!ToolInventoryAvailable)
        {
            sb.AppendLine(DynamicToolsNotInventoried
                ? "Gatekeeper coverage report — tool inventory UNAVAILABLE (a dynamic tool provider, e.g. an " +
                  "AIContextProvider, is present and no static tools are declared, so the tools it injects at " +
                  "invocation time cannot be listed). Coverage not measured."
                : "Gatekeeper coverage report — tool inventory UNAVAILABLE (this agent does not expose ChatOptions " +
                  "via GetService; pass an explicit tool list to GatekeeperCoverageAnalyzer.Analyze instead). " +
                  "Coverage not measured.");
            return sb.ToString();
        }

        // Local: ProtectedCount (and the other Tools.Count(...) properties) each do a fresh O(n) scan on every
        // access — cache the ones used more than once in this single Render() call instead of re-scanning.
        var protectedCount = ProtectedCount;
        if (Tools.Count == 0)
        {
            sb.AppendLine("Gatekeeper coverage report — no tools in the inventory: enforcement coverage not measurable (nothing to measure)");
        }
        else
        {
            var (toolsLabel, scope) = DynamicToolsNotInventoried ? ("static tool(s)", " of the static tools only") : ("tool(s)", string.Empty);
            sb.AppendLine(FormattableString.Invariant(
                $"Gatekeeper coverage report — {Tools.Count} {toolsLabel}, {MeasuredPercentFloor(protectedCount, Tools.Count)}% enforcement coverage{scope} ({protectedCount}/{Tools.Count} protected), {HighRiskCount} high-risk, {HighRiskUnprotectedCount} high-risk UNPROTECTED"));
        }

        if (DynamicToolsNotInventoried)
        {
            sb.AppendLine(DynamicToolsWarning);
        }

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

    private const string DynamicToolsWarning =
        "⚠ WARNING: a dynamic tool provider (e.g. an AIContextProvider) is present — tools it injects at invocation " +
        "time were NOT inventoried, so these figures cover the static tools only.";

    // Rounded DOWN: a rendered coverage figure must never claim more than was measured (F0 printed 299/300 as 100%).
    private static long MeasuredPercentFloor(int protectedCount, int toolCount)
        => 100L * protectedCount / toolCount;

    // Replaces the compiler-generated member list behind ToString(), which printed EnforcementCoveragePercent = 100
    // for an empty or unreadable inventory and could not show DynamicToolsNotInventoried (an internal property).
    // Same members, except a coverage figure that was not measured prints as "not measured".
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"ToolInventoryAvailable = {ToolInventoryAvailable}, ");
        builder.Append(CultureInfo.InvariantCulture, $"Tools = {Tools.Count} tool(s), ");
        builder.Append("RegisteredToolGateNames = [").Append(string.Join(", ", RegisteredToolGateNames)).Append("], ");
        builder.Append(CultureInfo.InvariantCulture, $"InterceptedLocalFunctionCount = {InterceptedLocalFunctionCount}, ");
        builder.Append(CultureInfo.InvariantCulture, $"ProviderHostedOpaqueCount = {ProviderHostedOpaqueCount}, ");
        builder.Append(CultureInfo.InvariantCulture, $"UnknownExecutionModelCount = {UnknownExecutionModelCount}, ");
        builder.Append(CultureInfo.InvariantCulture, $"ProtectedCount = {ProtectedCount}, ");
        builder.Append(CultureInfo.InvariantCulture, $"HighRiskCount = {HighRiskCount}, ");
        builder.Append(CultureInfo.InvariantCulture, $"HighRiskUnprotectedCount = {HighRiskUnprotectedCount}, ");
        builder.Append(CultureInfo.InvariantCulture, $"HasUnprotectedHighRiskTools = {HasUnprotectedHighRiskTools}, ");
        builder.Append("EnforcementCoveragePercent = ").Append(
            !ToolInventoryAvailable ? "not measured (tool inventory unavailable)"
            : Tools.Count == 0 ? "not measured (no tools in the inventory)"
            : (10_000L * ProtectedCount / Tools.Count / 100.0).ToString("0.##", CultureInfo.InvariantCulture));   // rounded down, as in Render()
        if (DynamicToolsNotInventoried)
        {
            builder.Append(", Warning = dynamically injected tools were NOT inventoried; the figures cover the static tools only");
        }

        return true;
    }

    private static string ModelTag(ToolExecutionModel model) => model switch
    {
        ToolExecutionModel.InterceptedLocalFunction => "local:function",
        ToolExecutionModel.ProviderHostedOpaque => "provider:hosted",
        _ => "unknown",
    };
}
