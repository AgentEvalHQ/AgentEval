// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper (Phase 1, P0-1) — enumerates the tools exposed to an agent's model, classifies each by
/// <see cref="ToolExecutionModel"/> and <see cref="ToolRiskLevel"/>, and reports what fraction is actually
/// covered by a registered tool gate. This is the single highest-leverage fix the Gatekeeper hardening review
/// identifies: it converts "Gatekeeper is installed" (which developers read as "my tools are protected") into
/// an honest, itemized answer.
/// <para><b>Why gates are a separate parameter, not read off the agent:</b> a tool gate list is closed over by
/// the <c>UseAgentEvalToolGate</c> middleware delegate — MAF's <see cref="AIAgentBuilder"/> does not expose it
/// back out of a built <see cref="AIAgent"/>. Pass the SAME list you registered (mirrors how <c>ShadowJudgePump</c>
/// is caller-owned elsewhere in Gatekeeper).</para>
/// <para><b>Coverage is structural, not semantic.</b> Every registered <see cref="IToolGate"/> sees every
/// local <see cref="AIFunction"/> call (there is no per-tool gate targeting yet — see the review's finding #8),
/// so "protected" here means "at least one tool gate is wired into the pipeline for this tool's execution
/// model," not "a gate specifically defends this exact tool." That is still the honest, useful question: it
/// answers "is the interception seam even reachable for this tool," which is exactly what today's silent gaps
/// (provider-hosted tools, an agent with zero gates registered) hide.</para>
/// <para><b>Blind to tools an <see cref="Microsoft.Agents.AI.AIContextProvider"/> injects at invocation time.</b>
/// <see cref="Analyze(AIAgent,IReadOnlyList{IToolGate}?,AnalyzeOptions?)"/> reads <c>ChatOptions.Tools</c> off
/// the agent — the STATIC, build-time tool list. A tool an <c>AIContextProvider</c> (e.g. Agent Skills, a memory
/// provider) contributes via <c>AIContext.Tools</c> is merged into a transient, per-invocation copy of
/// <c>ChatOptions</c> and is never written back to the property this analyzer reads. An agent that exposes tools
/// ONLY through such a provider (no <c>ChatOptions.Tools</c> set at all) reports <c>Tools.Count == 0</c> and
/// <c>EnforcementCoveragePercent == 100</c> — vacuously "fully covered" — even though the model genuinely sees
/// and can call those tools at runtime. There is no fix for this short of hooking the live invocation path (a
/// larger change); until then, treat a coverage report as ONLY about statically-declared tools, and do not use
/// it to conclude an <see cref="AIContextProvider"/>-driven agent has no unprotected high-risk tools.</para>
/// </summary>
public static class GatekeeperCoverageAnalyzer
{
    /// <summary>
    /// Analyzes the tools exposed by <paramref name="agent"/>'s model. Reads the tool list via
    /// <c>agent.GetService(typeof(ChatOptions))</c> — the same MAF extensibility seam <see cref="ChatClientAgent"/>
    /// uses to answer "what are my effective ChatOptions," which forwards correctly through any
    /// <see cref="AIAgentBuilder"/> middleware chain (a <c>DelegatingAIAgent</c> always forwards <c>GetService</c>
    /// to its inner agent). Returns a report with <see cref="GatekeeperCoverageReport.ToolInventoryAvailable"/>
    /// <see langword="false"/> (and an empty tool list) for an agent type that does not support this query —
    /// use the <see cref="Analyze(IEnumerable{AITool},IReadOnlyList{IToolGate}?,AnalyzeOptions?)"/> overload
    /// with an explicit tool list in that case.
    /// </summary>
    /// <param name="agent">The (possibly gate-wrapped) agent to inspect.</param>
    /// <param name="toolGates">The tool gates registered via <c>UseAgentEvalToolGate</c> — pass the same list. Null/empty means no tool gate is registered anywhere.</param>
    /// <param name="options">Analysis options (risk heuristic override). Defaults to <see cref="AnalyzeOptions.Default"/>.</param>
    /// <remarks>Only sees the static <c>ChatOptions.Tools</c> list — a tool contributed dynamically by an
    /// <see cref="Microsoft.Agents.AI.AIContextProvider"/> is invisible here. See the class remarks.</remarks>
    public static GatekeeperCoverageReport Analyze(AIAgent agent, IReadOnlyList<IToolGate>? toolGates = null, AnalyzeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(agent);

        if (agent.GetService(typeof(ChatOptions)) is not ChatOptions chatOptions)
        {
            return new GatekeeperCoverageReport(Array.Empty<ToolCoverageEntry>(), GateNames(toolGates), ToolInventoryAvailable: false);
        }

        return AnalyzeCore(chatOptions.Tools ?? Array.Empty<AITool>(), toolGates, options, toolInventoryAvailable: true);
    }

    /// <summary>Analyzes an explicit tool list (the same list you passed to <see cref="ChatOptions.Tools"/>).</summary>
    /// <param name="tools">The tools exposed to the model.</param>
    /// <param name="toolGates">The tool gates registered via <c>UseAgentEvalToolGate</c> — pass the same list.</param>
    /// <param name="options">Analysis options (risk heuristic override). Defaults to <see cref="AnalyzeOptions.Default"/>.</param>
    public static GatekeeperCoverageReport Analyze(IEnumerable<AITool> tools, IReadOnlyList<IToolGate>? toolGates = null, AnalyzeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return AnalyzeCore(tools, toolGates, options, toolInventoryAvailable: true);
    }

    /// <summary>
    /// Like <see cref="Analyze(AIAgent,IReadOnlyList{IToolGate}?,AnalyzeOptions?)"/>, but throws
    /// <see cref="UnprotectedHighRiskToolException"/> if any high-risk tool has zero protecting gate, or
    /// <see cref="ToolInventoryUnavailableException"/> if the tool inventory could not be read at all (an
    /// unverifiable agent must fail the SAME direction as a verified-bad one — see that exception's remarks).
    /// Call this right after <c>.Build()</c> to refuse to start an agent with an unprotected high-risk tool.
    /// </summary>
    public static GatekeeperCoverageReport AnalyzeOrThrow(AIAgent agent, IReadOnlyList<IToolGate>? toolGates = null, AnalyzeOptions? options = null)
        => ThrowIfUnprotected(Analyze(agent, toolGates, options));

    /// <summary>Like <see cref="Analyze(IEnumerable{AITool},IReadOnlyList{IToolGate}?,AnalyzeOptions?)"/>, but throws <see cref="UnprotectedHighRiskToolException"/> on an unprotected high-risk tool.</summary>
    public static GatekeeperCoverageReport AnalyzeOrThrow(IEnumerable<AITool> tools, IReadOnlyList<IToolGate>? toolGates = null, AnalyzeOptions? options = null)
        => ThrowIfUnprotected(Analyze(tools, toolGates, options));

    private static GatekeeperCoverageReport ThrowIfUnprotected(GatekeeperCoverageReport report) => report switch
    {
        { ToolInventoryAvailable: false } => throw new ToolInventoryUnavailableException(report),
        { HasUnprotectedHighRiskTools: true } => throw new UnprotectedHighRiskToolException(report),
        _ => report,
    };

    private static GatekeeperCoverageReport AnalyzeCore(
        IEnumerable<AITool> tools, IReadOnlyList<IToolGate>? toolGates, AnalyzeOptions? options, bool toolInventoryAvailable)
    {
        options ??= AnalyzeOptions.Default;
        var hasToolGate = toolGates is { Count: > 0 };   // every registered gate sees every local-function call (structural, see class doc)

        var entries = new List<ToolCoverageEntry>();
        foreach (var tool in tools)
        {
            var model = Classify(tool);
            var risk = options.IsHighRisk(tool) || IsArbitraryCapabilityOpaque(tool, options)
                ? ToolRiskLevel.HighRisk : ToolRiskLevel.Standard;
            var isProtected = model == ToolExecutionModel.InterceptedLocalFunction && hasToolGate;
            entries.Add(new ToolCoverageEntry(tool.Name, tool.Description, model, risk, isProtected, NoteFor(model, isProtected)));
        }

        return new GatekeeperCoverageReport(entries, GateNames(toolGates), toolInventoryAvailable);
    }

    private static string NoteFor(ToolExecutionModel model, bool isProtected) => model switch
    {
        ToolExecutionModel.InterceptedLocalFunction when isProtected => "local AIFunction — seen by every registered tool gate",
        ToolExecutionModel.InterceptedLocalFunction => "local AIFunction — interceptable, but no tool gate is registered",
        ToolExecutionModel.ProviderHostedOpaque => "provider-executed, not locally interceptable",
        _ => "execution model unrecognized — treat as unprotected until classified",
    };

#pragma warning disable MEAI001 // HostedToolSearchTool is an evaluation-only MAF/MEAI API — deliberately classified, not adopted as a runtime dependency.
    private static ToolExecutionModel Classify(AITool tool) => tool switch
    {
        null => throw new ArgumentException("tool list contains a null element.", nameof(tool)),
        AIFunction => ToolExecutionModel.InterceptedLocalFunction,
        HostedMcpServerTool or HostedCodeInterpreterTool or HostedWebSearchTool
            or HostedFileSearchTool or HostedImageGenerationTool or HostedToolSearchTool => ToolExecutionModel.ProviderHostedOpaque,
        _ => ToolExecutionModel.UnknownExecutionModel,
    };

    // A provider-hosted opaque tool that is ALSO arbitrary-capability (runs arbitrary code, or fronts an
    // arbitrary MCP tool surface) — the class that is both uninterceptable AND maximally dangerous. Narrowed to
    // these two on purpose: hosted web/file/image search are opaque too but far narrower, so they stay on the
    // keyword heuristic and don't over-trip AnalyzeOrThrow.
    private static bool IsArbitraryCapabilityOpaque(AITool tool, AnalyzeOptions options)
        => options.TreatArbitraryCapabilityOpaqueToolsAsHighRisk && tool is HostedCodeInterpreterTool or HostedMcpServerTool;
#pragma warning restore MEAI001

    private static IReadOnlyList<string> GateNames(IReadOnlyList<IToolGate>? toolGates)
        => toolGates is null ? Array.Empty<string>() : toolGates.Select(g => g.PolicyName).Distinct(StringComparer.Ordinal).ToArray();
}
