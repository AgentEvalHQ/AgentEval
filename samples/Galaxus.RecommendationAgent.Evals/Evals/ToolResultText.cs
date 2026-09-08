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

    /// <summary>
    /// True when any recorded tool RESULT in the trace contains <paramref name="code"/> ANYWHERE in
    /// its text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>NOT code-precise, and nothing that ships reads a verdict off it any more.</b> Use
    /// <see cref="AnyResultHasRefusalCode"/> for a refusal code. This overload stays because it is
    /// the loose matcher the collision control needs on one side of its comparison — a control that
    /// wrote its own loose matcher would be asserting against a copy rather than against the thing
    /// the two detectors used to be.
    /// </para>
    /// <para>
    /// Results only. Matching the ARGUMENTS as well would let a refusal code the model happened to
    /// echo back into a query count as the architecture having refused something.
    /// </para>
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

    /// <summary>
    /// The refusal code a tool result DECLARES — its <c>code</c> member — or <see langword="null"/>
    /// when the result is not a JSON object carrying one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>Why the field and not the text.</b> MEASURED on the live Eval 06 run of 2026-09-06:
    /// <c>ToolJson.SearchCapExhausted</c> serialises <c>status = "budget_exhausted"</c> while its
    /// <c>code</c> is <c>search_cap_exhausted</c> — the only such collision in
    /// <c>ToolRefusalCodes</c>, and the two caps it conflates are 24 refusable calls and 8 distinct
    /// searches. So a bare
    /// <c>Of(result).Contains(ToolRefusalCodes.BudgetExhausted)</c> answered <b>true</b> for a turn
    /// that spent 16 of its 24 calls and hit the SEARCH cap three times, and Eval 06 failed case
    /// T-03 with <i>"the turn asked for more calls than its budget allowed"</i> beside its own
    /// printed <c>budget 16/24</c>. Two numbers in one line that contradict each other, and a
    /// reader cannot tell which is the measurement.
    /// </para>
    /// <para>
    /// ⚠ <b>This was invisible until the detector could see at all.</b> Before
    /// <see cref="ToolResultText"/> existed the test was <c>Result is string</c>, false on every
    /// marshalled result, so the budget claim passed VACUOUSLY on every case of every run — chance
    /// floor 1.0 in the other direction. Fixing the blindness is what made the conflation
    /// reachable; it did not create it.
    /// </para>
    /// <para>
    /// <b>Unparseable is null, not a match.</b> A tool result that is not a JSON object declares no
    /// refusal code, and guessing one from loose text is the defect above.
    /// </para>
    /// </remarks>
    /// <param name="result">The recorded <c>ToolCallRecord.Result</c>.</param>
    public static string? RefusalCodeOf(object? result)
    {
        string text = Of(result);
        if (text.Length == 0) return null;

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                    ? code.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when any recorded tool RESULT in the trace DECLARES <paramref name="code"/> as its
    /// refusal code.
    /// </summary>
    /// <remarks>
    /// Results only, and the <c>code</c> member only — see <see cref="RefusalCodeOf"/> for the
    /// live measurement that separates this from <see cref="AnyResultContains"/>.
    /// </remarks>
    /// <param name="tools">The trace.</param>
    /// <param name="code">A <c>ToolRefusalCodes</c> value.</param>
    public static bool AnyResultHasRefusalCode(ToolUsageReport? tools, string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);

        if (tools is null) return false;

        foreach (var call in tools.Calls)
        {
            if (string.Equals(RefusalCodeOf(call.Result), code, StringComparison.Ordinal)) return true;
        }

        return false;
    }
}
