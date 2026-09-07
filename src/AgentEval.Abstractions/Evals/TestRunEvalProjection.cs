// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

using System.Globalization;
using System.Text.Json;
using AgentEval.Models;

/// <summary>
/// The join from an agent RUN — a <see cref="TestCase"/> and the <see cref="TestResult"/> it
/// produced — to the <see cref="EvalInput"/> every <see cref="IEval"/> consumes. AE-04's first half.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Three independent evals projects (<c>TravelDemo.Evals</c>,
/// <c>Galaxus.Evals</c>, <c>PartnerDeskDemo.Evals</c> — the third in a different repository) use
/// <see cref="IEval"/> exactly <b>zero</b> times each. Three authors, three codebases, no adoption.
/// The cause is not taste: there was no path from a MAF agent run to an <see cref="EvalInput"/>, so
/// each author wrote a private harness instead. This is the path.
/// </para>
/// <para>
/// 🔴 <b>The projection is <c>(TestCase, TestResult) → EvalInput</c>, NOT
/// <c>TestResult → EvalInput</c>, and the difference is forced by the types.</b>
/// <see cref="EvalInput.Query"/> is a required positional parameter and <see cref="TestResult"/> has
/// no query field at all — the query lives on <see cref="TestCase.Input"/>. A
/// <c>TestResult → EvalInput</c> projection could only be total by inventing a query, defaulting it
/// to empty, or hiding it in <see cref="EvalInput.Metadata"/>; all three make a fabricated stimulus
/// look like a recorded one. So the case is a parameter, and the signature says so.
/// </para>
/// <para>
/// <b>What is carried, and what is deliberately not.</b> A field silently dropped is the same defect
/// as a field silently zeroed, so every non-carry below is a decision with a reason, pinned by a test.
/// </para>
/// <list type="table">
///   <listheader><term>EvalInput field</term><description>source, or why not</description></listheader>
///   <item><term><see cref="EvalInput.Query"/></term><description><see cref="TestCase.Input"/>.</description></item>
///   <item><term><see cref="EvalInput.Response"/></term><description><see cref="TestResult.ActualOutput"/>, verbatim — <see langword="null"/> stays null (nothing recorded), <c>""</c> stays empty (an empty answer was recorded).</description></item>
///   <item><term><see cref="EvalInput.GroundTruth"/></term><description><see cref="TestCase.GroundTruth"/>.</description></item>
///   <item><term><see cref="EvalInput.ToolCalls"/></term><description><see cref="TestResult.ToolUsage"/>, else <see cref="TestResult.Timeline"/>. See <see cref="ProjectToolCalls"/> for the null-vs-empty rule and the ordering contract.</description></item>
///   <item><term><see cref="EvalInput.ExpectedActions"/></term><description><see cref="TestCase.ExpectedTools"/>, one action per tool. This is AE-02's fix: the field every loader writes and no agent-harness path reads becomes readable.</description></item>
///   <item><term><see cref="EvalInput.Metadata"/></term><description><see cref="TestCase.Metadata"/>, copied.</description></item>
///   <item><term><see cref="EvalInput.CaseId"/></term><description><see cref="TestCase.Id"/> and <b>nothing else</b> — see the CaseId note below.</description></item>
///   <item><term><see cref="EvalInput.ToolDefinitions"/></term><description>🔴 <b>NOT CARRIED.</b> See the ToolDefinitions note below — carrying it flatters a real eval.</description></item>
///   <item><term><see cref="EvalInput.Context"/></term><description>NOT CARRIED. Neither type records a retrieval context; there is nothing to read.</description></item>
///   <item><term><see cref="EvalInput.SystemMessage"/></term><description>NOT CARRIED. Neither type records the system message.</description></item>
///   <item><term><see cref="EvalInput.SubjectModel"/></term><description>NOT CARRIED. Neither type records the model the SUBJECT ran on, so <c>JudgeSubjectRelation</c> stays <c>Unknown</c> — which is the honest state, not a defect of this projection.</description></item>
/// </list>
/// <para>
/// Not carried from <see cref="TestCase"/>, because <see cref="EvalInput"/> has no field for them:
/// <c>Name</c>, <c>ExpectedOutputContains</c>, <c>EvaluationCriteria</c>, <c>PassingScore</c>,
/// <c>Tags</c>. Not carried from <see cref="TestResult"/>: <c>TestName</c>, <c>Passed</c>,
/// <c>Score</c>, <c>Details</c>, <c>Suggestions</c>, <c>CriteriaResults</c>,
/// <c>AssertionResults</c>, <c>Error</c>, <c>Performance</c>, <c>MetricResults</c>, <c>Failure</c>,
/// <c>RedTeam</c>. Those are a VERDICT; an <see cref="EvalInput"/> is a STIMULUS. Feeding a prior
/// verdict to the eval that is about to produce one is the gate-self-examination shape.
/// </para>
/// <para>
/// ⚠ <b>CaseId is <see cref="TestCase.Id"/> only, never a fallback to the test's name.</b> A
/// name-shaped fallback would make <see cref="EvalInput.CaseId"/> non-null on every case, which says
/// "this producer declared case identity" when nobody did — and it would join on a DISPLAY string,
/// the exact defect (ADR-030 §4.7, D11) that <see cref="TestCase.Id"/> was added to fix: the flagship
/// sample joined on <c>$"{c.Id} — {c.Group}"</c> and re-pointed the moment anyone edited a label. A
/// caller who genuinely wants the name as identity writes <c>input with { CaseId = result.TestName }</c>
/// and owns that choice explicitly.
/// </para>
/// <para>
/// 🔴 <b>ToolDefinitions is not carried, and the reason is measured, not stylistic.</b>
/// <see cref="ToolUsageReport.AvailableTools"/> is a set of NAMES with no description and no
/// parameter schema. Projecting it to <c>ToolDefinition(name, null, null)</c> would flip
/// <c>ToolInputAccuracyEval</c>'s schema leaf from <b>Skipped</b> ("no tool definitions supplied") to
/// <b>passing every call</b> — <c>ToolInputAccuracyEval.cs</c> reads
/// <c>if (schemaParams is null) { passedCalls++; continue; }</c>, i.e. "no parameter schema → treat
/// as passing". That is a silent lift in the flattering direction, manufactured by this projection,
/// on an eval that was deliberately fixed to skip rather than flatter (ADR-030 Slice 0.3, defect
/// D-c). Leaving it <see langword="null"/> keeps that leaf honestly skipped. Read availability from
/// <see cref="TestResult.ToolUsage"/> directly instead.
/// </para>
/// <para>
/// <b>Pure.</b> No I/O, no clock, no ambient state; the same pair of inputs yields an equal
/// <see cref="EvalInput"/> every time.
/// </para>
/// </remarks>
public static class TestRunEvalProjection
{
    /// <summary>
    /// Key under which a timeline's argument blob is carried when it is not a JSON object, so a
    /// malformed or scalar argument record is preserved rather than dropped.
    /// </summary>
    public const string RawArgumentsKey = "__rawArguments";

    /// <summary>
    /// Prefix marking a tool result synthesised from a FAILURE, because
    /// <see cref="ToolCall.Result"/> has no field for an error and a failed call must not be
    /// indistinguishable from a void-returning successful one.
    /// </summary>
    public const string ToolErrorResultPrefix = "__tool_error__:";

    /// <summary>
    /// Prefix marking a tool result that was PRESENT and could not be converted to text. It is never
    /// <see langword="null"/>: an absence and a value we failed to convert must not look the same.
    /// </summary>
    public const string UnconvertibleResultPrefix = "__tool_result_unconvertible__:";

    // A JSON null, detached from its document by Clone() so it outlives the parse. Used for an
    // argument whose recorded value is null: "the argument was absent" and "the argument was passed
    // as null" are different facts, and IReadOnlyDictionary<string, object> promises a non-null value
    // to every consumer that reads it.
    private static readonly JsonElement s_jsonNull = ParseDetached("null");

    /// <summary>
    /// Projects one executed case into the input every <see cref="IEval"/> consumes.
    /// </summary>
    /// <param name="testCase">The case that was run. Supplies the query, the ground truth, the expected tools and the metadata.</param>
    /// <param name="result">What the run produced. Supplies the response and the tool calls.</param>
    /// <returns>The projected input. See the type's remarks for the full field table.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="testCase"/> or <paramref name="result"/> is null.</exception>
    public static EvalInput ToEvalInput(this TestCase testCase, TestResult result)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        ArgumentNullException.ThrowIfNull(result);

        return new EvalInput(
            Query: testCase.Input,
            Response: result.ActualOutput,
            Context: null,
            GroundTruth: testCase.GroundTruth,
            ToolCalls: ProjectToolCalls(result),
            ToolDefinitions: null,
            ExpectedActions: ProjectExpectedActions(testCase.ExpectedTools),
            SystemMessage: null,
            Metadata: CopyMetadata(testCase.Metadata))
        {
            CaseId = testCase.Id,
        };
    }

    /// <summary>
    /// Reads the run's tool calls, in the chronological order <see cref="EvalInput.ToolCalls"/>
    /// requires.
    /// </summary>
    /// <param name="result">The run.</param>
    /// <returns>
    /// The calls; an EMPTY list when a recorder was present and saw none; <see langword="null"/> when
    /// no recorder ran at all.
    /// </returns>
    /// <remarks>
    /// <para>
    /// ⚠ <b>Null and empty are different facts and stay different.</b> <c>null</c> is "nobody recorded
    /// tool calls" — a <c>NeverCallTool</c>-style question is UNDECIDABLE on it. <c>[]</c> is "a
    /// recorder ran and the agent called nothing" — decidable. Collapsing them to <c>[]</c> would turn
    /// missing evidence into a clean safety result.
    /// </para>
    /// <para>
    /// <b>Ordering.</b> <see cref="EvalInput.ToolCalls"/> is a contract, not a convenience: the
    /// approval gate treats an approval as valid only if it appears at an earlier index than the
    /// sensitive call (BUG-37). So records are ordered by <see cref="ToolCallRecord.Order"/> and
    /// invocations by <see cref="ToolInvocation.SequenceIndex"/>, with LINQ's stable sort preserving
    /// insertion order when a producer left them all at the default.
    /// </para>
    /// <para>
    /// ⚠ <b>Honest scope of that sort.</b> On the <see cref="ToolUsageReport"/> path it is real —
    /// <c>AddCall</c> does not touch <see cref="ToolCallRecord.Order"/>, so a producer can and does
    /// record out of order. On the <see cref="ToolCallTimeline"/> path it is a NO-OP by construction:
    /// <c>AddInvocation</c> overwrites <see cref="ToolInvocation.SequenceIndex"/> with the insertion
    /// position, so no producer using the public API can record an out-of-order timeline. It is kept
    /// because the ordering is a contract rather than a convenience, and the test that pins the
    /// timeline's own stamping is what would go red if that invariant were relaxed.
    /// </para>
    /// <para>
    /// <see cref="TestResult.ToolUsage"/> wins over <see cref="TestResult.Timeline"/> because it
    /// carries structured arguments and the raw result object; the timeline carries pre-stringified
    /// approximations of both.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<ToolCall>? ProjectToolCalls(TestResult result)
    {
        if (result.ToolUsage is { } usage && usage.Calls.Count > 0)
        {
            return usage.Calls.OrderBy(c => c.Order).Select(FromRecord).ToList();
        }

        if (result.Timeline is { } timeline && timeline.Invocations.Count > 0)
        {
            return timeline.Invocations.OrderBy(i => i.SequenceIndex).Select(FromInvocation).ToList();
        }

        // A recorder was attached and saw nothing: a MEASURED zero.
        if (result.ToolUsage is not null || result.Timeline is not null)
        {
            return Array.Empty<ToolCall>();
        }

        // No recorder at all: UNKNOWN. Not a zero.
        return null;
    }

    private static ToolCall FromRecord(ToolCallRecord record) =>
        new(record.Name, ArgumentsOf(record.Arguments), ResultOf(record.Result, record.Exception));

    private static ToolCall FromInvocation(ToolInvocation invocation) =>
        new(
            invocation.ToolName,
            ArgumentsOf(invocation.Arguments),
            invocation.Result ?? (invocation.Succeeded
                ? null
                : Failure(invocation.ErrorMessage ?? "the tool call failed and no message was recorded")));

    /// <summary>
    /// Converts a recorded argument bag, keeping "absent" and "present and null" apart.
    /// </summary>
    /// <param name="arguments">The recorded arguments, or <see langword="null"/> when none were captured.</param>
    /// <returns>The converted bag, or <see langword="null"/> when nothing was captured.</returns>
    private static IReadOnlyDictionary<string, object>? ArgumentsOf(IDictionary<string, object?>? arguments)
    {
        if (arguments is null) return null;

        var converted = new Dictionary<string, object>(arguments.Count);
        foreach (var pair in arguments)
        {
            // A null VALUE is not a missing key. Carrying it as a JSON null keeps the key visible and
            // keeps the declared non-null value type honest.
            converted[pair.Key] = pair.Value ?? s_jsonNull;
        }

        return converted;
    }

    /// <summary>
    /// Converts a timeline's JSON argument blob into a bag, preserving anything that is not a JSON
    /// object under <see cref="RawArgumentsKey"/> rather than dropping it.
    /// </summary>
    /// <param name="json">The recorded blob, or <see langword="null"/>.</param>
    /// <returns>The bag, or <see langword="null"/> when nothing was recorded.</returns>
    private static IReadOnlyDictionary<string, object>? ArgumentsOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return new Dictionary<string, object> { [RawArgumentsKey] = json };
            }

            var converted = new Dictionary<string, object>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                // Clone(): the values must outlive the `using` above.
                converted[property.Name] = property.Value.Clone();
            }

            return converted;
        }
        catch (JsonException)
        {
            // Unparseable is not empty. Keep the bytes where a reader can see them.
            return new Dictionary<string, object> { [RawArgumentsKey] = json };
        }
    }

    /// <summary>
    /// Converts a recorded tool RESULT — <c>object?</c> on the way in, <c>string?</c> on the way out.
    /// </summary>
    /// <param name="value">The recorded result object.</param>
    /// <param name="error">The exception the call threw, when it threw one.</param>
    /// <returns>
    /// The text of the result; a <see cref="ToolErrorResultPrefix"/> line when the call failed with no
    /// result; an <see cref="UnconvertibleResultPrefix"/> line when a value was present and could not
    /// be rendered; <see langword="null"/> ONLY when there was no result and no failure.
    /// </returns>
    /// <remarks>
    /// 🔴 <b>A naive <c>value is string</c> test is almost always FALSE here, and this repository has
    /// paid for that before.</b> <c>AIFunctionFactory</c> marshals a tool's return value through
    /// <see cref="JsonSerializer"/>, so <see cref="ToolCallRecord.Result"/> is typically a
    /// <see cref="JsonElement"/>. A string-only conversion silently produced <see langword="null"/>
    /// on every call, which read downstream as "the tool returned nothing" and floored a metric at
    /// zero. Hence the explicit <see cref="JsonElement"/> arm, the explicit
    /// <see cref="JsonValueKind.Undefined"/> arm (<c>default(JsonElement)</c>, whose
    /// <c>GetRawText()</c> throws), and the rule that a present value never becomes null.
    /// </remarks>
    internal static string? ResultOf(object? value, Exception? error)
    {
        if (value is null)
        {
            // No result. A call that FAILED still has something to say, and "failed" must not read as
            // "returned nothing" — a void-returning tool that succeeded looks exactly like that.
            return error is null ? null : Failure(error.Message);
        }

        if (value is string text) return text;

        if (value is JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Undefined:
                    // default(JsonElement). GetRawText() throws on it; a present-but-unrenderable
                    // value is reported as such, never as null.
                    return Unconvertible("an undefined JsonElement");

                case JsonValueKind.String:
                    return element.GetString() ?? Unconvertible(nameof(JsonElement));

                default:
                    // Includes JsonValueKind.Null, whose raw text is "null" — a RECORDED json null,
                    // which is a value and not the same fact as no result at all.
                    return element.GetRawText();
            }
        }

        try
        {
            return JsonSerializer.Serialize(value, value.GetType());
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            string? rendered = SafeToString(value);
            return string.IsNullOrEmpty(rendered) ? Unconvertible(value.GetType().Name) : rendered;
        }
    }

    private static string? SafeToString(object value)
    {
        try { return value.ToString(); }
        catch (Exception) { return null; }
    }

    private static string Failure(string? message) =>
        string.Create(CultureInfo.InvariantCulture, $"{ToolErrorResultPrefix} {message}");

    private static string Unconvertible(string what) =>
        string.Create(CultureInfo.InvariantCulture, $"{UnconvertibleResultPrefix} {what}");

    /// <summary>
    /// Projects <see cref="TestCase.ExpectedTools"/> — AE-02's write-only field — into the
    /// expectations an eval can actually read.
    /// </summary>
    /// <param name="expectedTools">The declared tools, or <see langword="null"/> when none were declared.</param>
    /// <returns>One <see cref="ExpectedAction"/> per tool, in declared order; <see langword="null"/> when nothing was declared.</returns>
    /// <remarks>
    /// <para>
    /// <b>One action per tool, not one action listing every tool.</b>
    /// <c>TaskNavigationEfficiencyEval</c>'s edit-distance leaf reads
    /// <c>input.ExpectedActions.Select(ea =&gt; ea.Description)</c> as a SEQUENCE and compares it
    /// position-by-position against the tool-call names — its own docs say "one entry per expected
    /// action step". A single action carrying all the tools would present a 4-step expectation as a
    /// 1-step one and score a correct run at 0.
    /// </para>
    /// <para>
    /// Entries are carried verbatim, blanks included. The projection's job is to carry the case, not
    /// to clean it: filtering a blank silently shortens the expected sequence, which moves an
    /// edit-distance score without anyone asking.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<ExpectedAction>? ProjectExpectedActions(IReadOnlyList<string>? expectedTools)
    {
        if (expectedTools is null) return null;

        var actions = new List<ExpectedAction>(expectedTools.Count);
        foreach (string tool in expectedTools)
        {
            actions.Add(new ExpectedAction(tool, new[] { tool }));
        }

        return actions;
    }

    private static IReadOnlyDictionary<string, object>? CopyMetadata(IDictionary<string, object>? metadata)
    {
        if (metadata is null) return null;

        var copy = new Dictionary<string, object>(metadata.Count);
        foreach (var pair in metadata) copy[pair.Key] = pair.Value;
        return copy;
    }

    private static JsonElement ParseDetached(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
