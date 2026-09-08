// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Microsoft.Extensions.AI;

namespace Galaxus.RecommendationAgent.Guardrails;

/// <summary>
/// The read-only tool surface, asserted at agent construction (§F.1). A positive allow-list of
/// the ELEVEN sanctioned tool names; anything else — and any name missing — throws
/// <see cref="InvalidOperationException"/> and the application fails to start.
/// </summary>
/// <remarks>
/// <para>
/// <b>Eleven, and why the count is written down three times.</b> Three semantic tools, seven
/// structured tools, and <c>PresentRecommendation</c>. The first draft of this design shipped a
/// TEN-name registration against a NINE-name allow-list and would have thrown on its first run
/// (§0.5 / A-1). The fix is not to be more careful: the counts are constants, the lists are
/// built from the same constants, and a static constructor refuses to load the type if they
/// disagree. Drift becomes a load failure instead of a first-run surprise.
/// </para>
/// <para>
/// <b>Zero of the eleven mutate anything.</b> No <c>AddToCart</c>, no <c>PlaceOrder</c>, no
/// <c>SaveProfile</c>, no <c>ApplyVoucher</c>. This is not a prompt instruction the model can be
/// argued out of — it is the absence of a capability. Adding a write tool later cannot be done
/// accidentally, because the app stops starting.
/// </para>
/// <para>
/// <b>The paradox, and the two factories that resolve it (§0.5 / D-5).</b> An agent that has no
/// <c>PlaceOrder</c> makes <c>NeverCallTool("PlaceOrder")</c> a check with a chance floor of
/// 1.0 — it proves nothing, because the prohibition was never tempting. So there are two
/// configurations, and two assertions:
/// </para>
/// <list type="bullet">
///   <item>
///     <see cref="AssertReadOnly"/> — the SHIPPED surface. Exactly the eleven names, nothing
///     else. Read-only is a property of the configuration the demo runs.
///   </item>
///   <item>
///     <see cref="AssertReadOnlyWithApprovalGatedCommitTools"/> — the TESTED surface, used only
///     by the two eval cases that exercise the human-confirmation gate. The eleven names plus
///     <c>AddToCart</c> and <c>PlaceOrder</c>, and each of those two must be wrapped as an
///     <see cref="ApprovalRequiredAIFunction"/> — an unwrapped commit tool is a violation, not
///     a permitted extra. The approval gate becomes a property of the tested configuration, and
///     both claims become true at once.
///   </item>
/// </list>
/// </remarks>
public static class ToolSurfaceInvariant
{
    /// <summary>The one sanctioned recommendation channel (§0.5 / D-1). Prose is not a channel.</summary>
    public const string PresentRecommendationToolName = "PresentRecommendation";

    /// <summary>How many semantic tools the surface carries. Recall-oriented; may be wrong by design.</summary>
    public const int SemanticToolCount = 3;

    /// <summary>How many structured tools the surface carries. Facts; a wrong answer here is a bug.</summary>
    public const int StructuredToolCount = 7;

    /// <summary>The size of the read-only allow-list: <see cref="SemanticToolCount"/> + <see cref="StructuredToolCount"/> + <c>PresentRecommendation</c>.</summary>
    public const int ReadOnlyToolCount = SemanticToolCount + StructuredToolCount + 1;

    /// <summary>
    /// Dense + lexical fusion over the product index. These return CANDIDATES with scores, and
    /// they are permitted to be wrong — the model filters them and the structured leg confirms.
    /// </summary>
    public static readonly IReadOnlyList<string> SemanticToolNames =
    [
        "SearchProductsByMeaning",
        "FindSimilarProducts",
        "FindComplements"
    ];

    /// <summary>
    /// Direct lookups in the seed data. These return FACTS: the guardrails verify the model
    /// against them, so a wrong answer here is a bug, not a ranking artefact.
    /// </summary>
    public static readonly IReadOnlyList<string> StructuredToolNames =
    [
        "GetUserProfile",
        "GetPurchaseHistory",
        "GetProductDetails",
        "CheckStockAndPrice",
        "BrowseCategory",
        "GetReviewDigest",
        "GetInterestMap"
    ];

    /// <summary>
    /// The two tools the §F.6 personalization opt-out forbids: the raw history and the interest map
    /// DERIVED from that history.
    /// </summary>
    /// <remarks>
    /// Both return a typed <c>personalization_disabled</c> refusal when the profile carries the
    /// opt-out — enforced in the tool, never in the prompt. Named here so a reporter can ask the
    /// question the backstop's own report needs: was the backstop TEMPTED at all? An agent that
    /// never called either one and an architecture that refused both look identical in a boolean,
    /// and the first is a chance floor of 1.0.
    /// </remarks>
    public static readonly IReadOnlyList<string> BehaviouralHistoryToolNames =
    [
        "GetPurchaseHistory",
        "GetInterestMap"
    ];

    /// <summary>
    /// The complete allow-list: the eleven names the shipped agent may register, and no others.
    /// </summary>
    public static readonly IReadOnlyList<string> ReadOnlyToolNames =
    [
        .. SemanticToolNames,
        .. StructuredToolNames,
        PresentRecommendationToolName
    ];

    /// <summary>
    /// The two mutating tools, registered ONLY by <c>RecommendationAgentFactory.CreateWithCommitTools()</c>
    /// and only behind <see cref="ApprovalRequiredAIFunction"/>. They exist so the prohibition
    /// can be tempting; a prohibition that cannot be violated is not evidence of restraint.
    /// </summary>
    public static readonly IReadOnlyList<string> CommitToolNames =
    [
        "AddToCart",
        "PlaceOrder"
    ];

    static ToolSurfaceInvariant()
    {
        // §0.5 / A-1 made concrete: the allow-list and the counts cannot silently disagree.
        if (SemanticToolNames.Count != SemanticToolCount)
        {
            throw new InvalidOperationException(
                $"ToolSurfaceInvariant is inconsistent: SemanticToolNames has {SemanticToolNames.Count} entries but SemanticToolCount is {SemanticToolCount}.");
        }

        if (StructuredToolNames.Count != StructuredToolCount)
        {
            throw new InvalidOperationException(
                $"ToolSurfaceInvariant is inconsistent: StructuredToolNames has {StructuredToolNames.Count} entries but StructuredToolCount is {StructuredToolCount}.");
        }

        if (ReadOnlyToolNames.Count != ReadOnlyToolCount)
        {
            throw new InvalidOperationException(
                $"ToolSurfaceInvariant is inconsistent: ReadOnlyToolNames has {ReadOnlyToolNames.Count} entries but ReadOnlyToolCount is {ReadOnlyToolCount}.");
        }

        if (ReadOnlyToolNames.Distinct(StringComparer.Ordinal).Count() != ReadOnlyToolNames.Count)
        {
            throw new InvalidOperationException("ToolSurfaceInvariant is inconsistent: ReadOnlyToolNames contains a duplicate.");
        }
    }

    /// <summary>True when <paramref name="name"/> is one of the eleven read-only tools (ordinal).</summary>
    /// <param name="name">A registered tool name.</param>
    public static bool IsReadOnlyToolName(string? name) =>
        name is not null && ReadOnlyToolNames.Contains(name, StringComparer.Ordinal);

    /// <summary>True when <paramref name="name"/> is one of the two mutating tools (ordinal).</summary>
    /// <param name="name">A registered tool name.</param>
    public static bool IsCommitToolName(string? name) =>
        name is not null && CommitToolNames.Contains(name, StringComparer.Ordinal);

    /// <summary>The names of <paramref name="tools"/>, in registration order.</summary>
    /// <param name="tools">The tools an agent registers.</param>
    public static IReadOnlyList<string> ToolNames(IEnumerable<AITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return tools.Select(t => t.Name).ToList();
    }

    /// <summary>
    /// Asserts the SHIPPED read-only surface: exactly the eleven allow-listed names, each
    /// registered once, and nothing else.
    /// </summary>
    /// <param name="tools">The tools about to be handed to <c>ChatOptions.Tools</c>.</param>
    /// <exception cref="InvalidOperationException">The registered set differs from the allow-list in any way.</exception>
    public static void AssertReadOnly(IEnumerable<AITool> tools)
    {
        var violations = FindViolations(tools, allowCommitTools: false);
        if (violations.Count == 0) return;

        throw new InvalidOperationException(
            "The agent's tool surface violates the read-only invariant (§F.1). The application will not start."
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations.Select(v => "  • " + v)));
    }

    /// <summary>
    /// Asserts the TESTED surface used by the human-confirmation eval cases: the eleven
    /// read-only names PLUS <c>AddToCart</c> and <c>PlaceOrder</c>, each of the latter wrapped
    /// as an <see cref="ApprovalRequiredAIFunction"/>.
    /// </summary>
    /// <remarks>
    /// An unwrapped commit tool fails this assertion. Registering <c>PlaceOrder</c> without an
    /// approval gate would make the eval case pass for the wrong reason: the agent could call
    /// it, the call would succeed, and only the model's good manners would stand between the
    /// customer and an order.
    /// </remarks>
    /// <param name="tools">The tools about to be handed to <c>ChatOptions.Tools</c>.</param>
    /// <exception cref="InvalidOperationException">A name is unknown, missing, duplicated, or a commit tool is not approval-gated.</exception>
    public static void AssertReadOnlyWithApprovalGatedCommitTools(IEnumerable<AITool> tools)
    {
        var violations = FindViolations(tools, allowCommitTools: true);
        if (violations.Count == 0) return;

        throw new InvalidOperationException(
            "The agent's commit-gated tool surface is not what the human-confirmation eval requires (§0.5 / D-5). The application will not start."
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations.Select(v => "  • " + v)));
    }

    /// <summary>
    /// The mirror-image invariant, and the name the agent factory calls it by: asserts that this
    /// configuration carries BOTH commit tools and that BOTH are approval-gated, alongside the
    /// eleven read-only names. Identical to
    /// <see cref="AssertReadOnlyWithApprovalGatedCommitTools"/>.
    /// </summary>
    /// <param name="tools">The tools about to be handed to <c>ChatOptions.Tools</c>.</param>
    /// <exception cref="InvalidOperationException">A name is unknown, missing, duplicated, or a commit tool is not approval-gated.</exception>
    public static void AssertCommitToolsApprovalGated(IEnumerable<AITool> tools) =>
        AssertReadOnlyWithApprovalGatedCommitTools(tools);

    /// <summary>
    /// Every way the registered set differs from the expected surface, as human-readable lines.
    /// Empty means the surface is exactly right.
    /// </summary>
    /// <remarks>
    /// Both directions are checked. An UNEXPECTED name is the obvious violation; a MISSING one
    /// matters just as much, because a silently shrinking surface is how a demo starts passing
    /// checks it is no longer performing.
    /// </remarks>
    /// <param name="tools">The tools an agent registers.</param>
    /// <param name="allowCommitTools">True to additionally require the two approval-gated commit tools.</param>
    public static IReadOnlyList<string> FindViolations(IEnumerable<AITool> tools, bool allowCommitTools = false)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var registered = tools.ToList();
        var violations = new List<string>();

        var expected = allowCommitTools
            ? ReadOnlyToolNames.Concat(CommitToolNames).ToList()
            : ReadOnlyToolNames.ToList();

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tool in registered)
        {
            var name = tool.Name;

            if (string.IsNullOrWhiteSpace(name))
            {
                violations.Add($"a tool of type {tool.GetType().Name} has no name, so it cannot be allow-listed");
                continue;
            }

            if (!seen.Add(name))
            {
                violations.Add($"'{name}' is registered more than once");
                continue;
            }

            if (IsCommitToolName(name))
            {
                if (!allowCommitTools)
                {
                    violations.Add(
                        $"'{name}' MUTATES state and is not part of the shipped read-only surface. " +
                        "If a commit tool is genuinely needed, register it through RecommendationAgentFactory.CreateWithCommitTools(), " +
                        "wrapped with .RequiresApproval() so it fails closed to a human");
                }
                else if (tool is not ApprovalRequiredAIFunction)
                {
                    violations.Add(
                        $"'{name}' is a commit tool but is not wrapped as an ApprovalRequiredAIFunction. " +
                        "An ungated commit tool makes the human-confirmation case pass for the wrong reason");
                }

                continue;
            }

            if (!expected.Contains(name, StringComparer.Ordinal))
            {
                violations.Add($"'{name}' is not on the allow-list of {expected.Count} sanctioned tools");
            }
        }

        foreach (var name in expected)
        {
            if (seen.Contains(name)) continue;
            violations.Add(
                $"'{name}' is on the allow-list but was not registered. A surface that silently shrinks is how a demo " +
                "starts passing checks it is no longer performing");
        }

        return violations;
    }
}
