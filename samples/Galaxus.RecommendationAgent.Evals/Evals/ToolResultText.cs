// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Text.Json;

namespace Galaxus.RecommendationAgent.Evals;

/// <summary>
/// Renders a recorded tool RESULT as searchable text, whatever CLR shape the harness stored it in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists: two detectors that could never fire.</b> Both
/// <c>Eval01.DetectOptOutBackstop</c> and <c>Eval06.HasBudgetRefusal</c> were written as
/// <c>call.Result is string json &amp;&amp; json.Contains(code)</c>. On the live path
/// <c>ToolCallRecord.Result</c> is whatever <c>FunctionResultContent.Result</c> carried, and
/// <c>AIFunctionFactory.Create</c> marshals a <c>Task&lt;string&gt;</c> tool's return value through
/// <c>JsonSerializer</c> — so it arrives as a <see cref="JsonElement"/>, never as a
/// <see cref="string"/>. MEASURED by invoking the real <c>AIFunction</c> for
/// <c>GalaxusTools.GetInterestMap</c>: <c>System.Text.Json.JsonElement</c>,
/// <c>result is string</c> → <b>false</b>.
/// </para>
/// <para>
/// ⚠ <b>Both detectors therefore had a chance floor of ZERO on the path that matters</b>, and one
/// of them printed a sentence about it: Eval 01's report said <i>"the tool-layer backstop was never
/// exercised this turn"</i> on the 2026-09-05 opt-out case, and <c>SUITE_SUMMARY</c> §4 recorded
/// that this either meant a containment hole or a blind detector and that <i>"this run does not
/// settle which"</i>. It is the blind detector. The refusal fired; nothing could see it.
/// </para>
/// <para>
/// ⚠ <b>Direction of the error: damning to our own architecture and flattering to the
/// instrument.</b> It made a structural guardrail that works look absent, and it made a detector
/// that cannot fire look like a clean negative — which is §7 rule 6's shape exactly, an extreme
/// value (0 of every opt-out turn ever run) that was a wiring fault all along.
/// </para>
/// <para>
/// A scripted control could not have caught it: the controls build
/// <c>FunctionCallContent</c>/<c>FunctionResultContent</c> pairs by hand and hand-built results are
/// <c>string</c>s, so the stub was kinder than the model here in the precise sense
/// <c>RUN_PROTOCOL.md</c> names. The control that pins this invokes the REAL
/// <c>AIFunctionFactory</c>-created function and asserts the shape it actually returns.
/// </para>
/// </remarks>
public static class ToolResultText
{
    /// <summary>The result rendered as text, or the empty string when there is none.</summary>
    /// <remarks>
    /// A <see cref="JsonElement"/> holding a JSON string is unwrapped to its value; anything else
    /// renders as its raw JSON, which is what the tools emit. Nothing here parses: a detector that
    /// needed the payload to be well-formed would go blind again the moment a tool returned an
    /// error string.
    /// </remarks>
    /// <param name="result">The recorded <c>ToolCallRecord.Result</c>.</param>
    public static string Of(object? result) => result switch
    {
        null => string.Empty,
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
        JsonElement element => element.GetRawText(),
        JsonDocument document => document.RootElement.GetRawText(),
        _ => result.ToString() ?? string.Empty,
    };

    /// <summary>True when any recorded tool RESULT in the trace contains <paramref name="code"/>.</summary>
    /// <remarks>
    /// Results only. Matching the ARGUMENTS as well would let a refusal code the model happened to
    /// echo back into a query count as the architecture having refused something.
    /// </remarks>
    /// <param name="tools">The trace.</param>
    /// <param name="code">A <c>ToolRefusalCodes</c> value.</param>
    public static bool AnyResultContains(ToolUsageReport? tools, string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);

        if (tools is null) return false;

        foreach (var call in tools.Calls)
        {
            if (Of(call.Result).Contains(code, StringComparison.Ordinal)) return true;
        }

        return false;
    }
}
