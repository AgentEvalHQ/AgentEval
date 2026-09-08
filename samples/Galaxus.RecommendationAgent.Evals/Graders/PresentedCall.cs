// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Text.Json;

namespace Galaxus.RecommendationAgent.Evals.Graders;

/// <summary>
/// One recommendation as the agent actually emitted it — a <c>PresentRecommendation</c> tool
/// call, never prose (design §0.5 / D-1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Named <c>PresentedCall</c>, not <c>PresentedRecommendation</c>.</b> The demo project
/// already owns a <c>Domain.PresentedRecommendation</c> record and this project global-uses that
/// namespace; a second type with the same name would be ambiguous at every call site that also
/// sees the domain. The names differ on purpose so the two never get confused: the domain record
/// is what the TOOL captured in-process, this one is what the TRACE reports, and it is the trace
/// that a workflow arm or a scripted control can also produce.
/// </para>
/// <para>
/// <b>The graders take a list of these, not a <c>ToolUsageReport</c>.</b> That keeps the real
/// agent, the negative controls and any future workflow arm symmetric at the grader boundary —
/// which is where symmetry actually has to hold — even though their runners differ.
/// </para>
/// </remarks>
/// <param name="Sku">The <c>sku</c> argument, verbatim, trimmed only of surrounding whitespace.</param>
/// <param name="Reason">The <c>reason</c> argument, verbatim.</param>
/// <param name="Evidence">The <c>evidence</c> argument, verbatim.</param>
/// <param name="OutOfStock">The <c>outOfStock</c> argument, parsed defensively (see <see cref="FromToolUsage"/>).</param>
/// <param name="Order">1-based position in the run's tool timeline.</param>
/// <param name="ExecutorId">Workflow executor that made the call, when there is one.</param>
/// <param name="WasExecuted">True when a paired tool RESULT was observed for this call.</param>
/// <param name="OutOfStockArgumentPresent">False when the model omitted the argument entirely.</param>
public readonly record struct PresentedCall(
    string Sku,
    string Reason,
    string Evidence,
    bool OutOfStock,
    int Order,
    string? ExecutorId,
    bool WasExecuted,
    bool OutOfStockArgumentPresent)
{
    /// <summary>The frozen tool name. Hard-coded in exactly one place in this project.</summary>
    public const string ToolName = "PresentRecommendation";

    /// <summary>
    /// Projects every <c>PresentRecommendation</c> call out of a tool-usage report.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two deliberate deviations from the design's code sketch, both in the non-flattering
    /// direction.</b>
    /// </para>
    /// <para>
    /// (1) The sketch filters on <c>c.WasExecuted</c>. This does not, because an emitted-but-
    /// unexecuted call is still an ACT OF PRESENTING as far as a prohibition is concerned, and
    /// dropping it would hide a leak. It is surfaced instead: <see cref="WasExecuted"/> travels
    /// with the record and the report prints how many calls arrived unexecuted, because a
    /// non-zero count on the read-only surface is a harness anomaly worth seeing rather than a
    /// property of the agent. Errored calls are still dropped — a call the tool layer rejected
    /// never reached the customer and the model was told so.
    /// </para>
    /// <para>
    /// (2) The sketch reads <c>GetArgument&lt;string&gt;("outOfStock")</c> and compares to
    /// <c>"true"</c>. A model that sends a JSON boolean (which is what the tool's schema asks
    /// for) lands in <c>JsonSerializer.Deserialize&lt;string&gt;</c> on a <c>true</c> token,
    /// which THROWS — so defect class D2 would have died on an exception rather than firing.
    /// Parsing here is defensive and covers boolean, string and numeric spellings, and an
    /// unreadable argument is treated as ABSENT (i.e. false, i.e. a stock claim), which is the
    /// direction that reports a defect rather than hiding one.
    /// </para>
    /// </remarks>
    /// <param name="report">The trace from one agent turn.</param>
    public static IReadOnlyList<PresentedCall> FromToolUsage(ToolUsageReport? report)
    {
        if (report is null) return [];

        var presented = new List<PresentedCall>();
        foreach (var call in report.Calls
                     .Where(c => string.Equals(c.Name, ToolName, StringComparison.OrdinalIgnoreCase))
                     .Where(c => !c.HasError)
                     .OrderBy(c => c.Order))
        {
            bool present = HasArgument(call, PresentRecommendationArguments.OutOfStock);
            presented.Add(new PresentedCall(
                Sku: ReadString(call, PresentRecommendationArguments.Sku).Trim(),
                Reason: ReadString(call, PresentRecommendationArguments.Reason),
                Evidence: ReadString(call, PresentRecommendationArguments.Evidence).Trim(),
                OutOfStock: ReadBool(call, PresentRecommendationArguments.OutOfStock),
                Order: call.Order,
                ExecutorId: call.ExecutorId,
                WasExecuted: call.WasExecuted,
                OutOfStockArgumentPresent: present));
        }

        return presented;
    }

    /// <summary>True when the tool call carried the named argument at all.</summary>
    /// <param name="call">A tool call record.</param>
    /// <param name="name">The argument name.</param>
    public static bool HasArgument(ToolCallRecord call, string name)
    {
        ArgumentNullException.ThrowIfNull(call);
        return call.Arguments is not null && call.Arguments.ContainsKey(name);
    }

    /// <summary>
    /// Reads a string argument without ever throwing. A model that sends a number where a string
    /// was asked for should produce a DEFECT, not an unhandled <see cref="JsonException"/> that
    /// aborts the suite on exactly the case that was misbehaving.
    /// </summary>
    /// <param name="call">A tool call record.</param>
    /// <param name="name">The argument name.</param>
    public static string ReadString(ToolCallRecord call, string name)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.Arguments is null || !call.Arguments.TryGetValue(name, out var raw) || raw is null)
            return string.Empty;

        if (raw is string s) return s;

        if (raw is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? string.Empty,
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                _ => element.GetRawText(),
            };
        }

        return raw.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Reads a boolean argument without ever throwing, accepting <c>true</c>, <c>"true"</c>,
    /// <c>"True"</c>, <c>1</c> and <c>"yes"</c>. Anything else — including a missing or
    /// unreadable argument — reads as false, which is the direction that REPORTS a stock claim
    /// rather than excusing one.
    /// </summary>
    /// <param name="call">A tool call record.</param>
    /// <param name="name">The argument name.</param>
    public static bool ReadBool(ToolCallRecord call, string name)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.Arguments is null || !call.Arguments.TryGetValue(name, out var raw) || raw is null)
            return false;

        switch (raw)
        {
            case bool b: return b;
            case string s: return IsTruthy(s);
            case int i: return i != 0;
            case long l: return l != 0;
        }

        if (raw is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => IsTruthy(element.GetString()),
                JsonValueKind.Number => element.TryGetDouble(out var d) && d != 0,
                _ => false,
            };
        }

        return IsTruthy(raw.ToString());
    }

    private static bool IsTruthy(string? text) =>
        text is not null
        && (text.Equals("true", StringComparison.OrdinalIgnoreCase)
            || text.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || text.Equals("1", StringComparison.Ordinal));

    /// <summary>The parsed citation, or null when the evidence argument is not a citation at all.</summary>
    public EvidenceRef? Citation => EvidenceRef.TryParse(Evidence, out var reference) ? reference : null;

    /// <summary>A compact one-line rendering for the console report.</summary>
    public override string ToString() =>
        $"#{Order} {Sku} evidence='{Evidence}'{(OutOfStock ? " outOfStock=true" : "")}";
}
