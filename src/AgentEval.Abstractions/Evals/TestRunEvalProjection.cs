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
    /// <remarks>
    /// EVERY failed call carries it, including one that also recorded a payload — the payload is
    /// appended after the reason rather than replacing it, so
    /// <c>Result.StartsWith(ToolErrorResultPrefix)</c> is a reliable "this call failed" test and
    /// nothing the tool wrote can suppress it.
    /// </remarks>
    public const string ToolErrorResultPrefix = "__tool_error__:";

    /// <summary>
    /// Prefix marking a tool result that was PRESENT and could not be converted to text. It is never
    /// <see langword="null"/>: an absence and a value we failed to convert must not look the same.
    /// </summary>
    public const string UnconvertibleResultPrefix = "__tool_result_unconvertible__:";

    /// <summary>
    /// Prefix marking a call the run records as NOT KNOWN TO HAVE EXECUTED — rejected at the
    /// approval gate, or gated with no paired result observed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 Without it, a call that never ran is byte-identical to a void-returning call that
    /// succeeded: both are <c>ToolCall(name, args, null)</c>, and the two records compare EQUAL.
    /// <see cref="ToolCallRecord.WasExecuted"/> exists to keep them apart and says so — "a void- or
    /// null-returning tool still executes, so absence of a result value does NOT mean
    /// non-execution".
    /// </para>
    /// <para>
    /// ⚠ Read from <see cref="ToolCallRecord.ApprovalState"/> and <b>never from
    /// <see cref="ToolCallRecord.WasExecuted"/> alone.</b> That flag defaults to <c>false</c> and is
    /// set only by an extractor that matched a paired result, so on a hand-built or non-approval
    /// record <c>false</c> means UNKNOWN, not "did not execute" — keying off it would mark almost
    /// every existing producer's calls as unexecuted. <see cref="ToolCallRecord.ApprovalState"/> is
    /// <see langword="null"/> unless approval-aware extraction ran, so a value there is a positive
    /// declaration and <see cref="ToolCallRecord.WasExecuted"/> can be trusted beside it.
    /// </para>
    /// </remarks>
    public const string ToolNotExecutedResultPrefix = "__tool_not_executed__:";

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
    /// <exception cref="ArgumentException">
    /// <paramref name="testCase"/> carries a null <see cref="TestCase.Input"/>, so there is no query.
    /// </exception>
    public static EvalInput ToEvalInput(this TestCase testCase, TestResult result)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        ArgumentNullException.ThrowIfNull(result);

        // 🔴 The fourth way this projection could fail to be total, and the only one that used to pass
        // silently. TestCase.Input is `required string`, but `required` is not `non-null`: a `null!`,
        // a nullable-oblivious caller or a deserialiser handed an explicit JSON null all reach here,
        // and EvalInput.Query is non-nullable. The null used to be carried straight through, so the
        // first eval to read Query threw a NullReferenceException with nothing naming the case.
        // Defaulting to "" is refused for the reason stated above: it makes a fabricated stimulus
        // look like a recorded one.
        if (testCase.Input is null)
        {
            throw new ArgumentException(
                $"TestCase '{Describe(testCase)}' carries a null Input, so this run has no query to project and "
                + $"{nameof(EvalInput)}.{nameof(EvalInput.Query)} cannot be filled. This projection will not "
                + "invent one — an empty query is a fabricated stimulus wearing a recorded one's clothes. Give "
                + "the case its input, or (if the run genuinely had no prompt) say so with an explicit empty "
                + "string so the fabrication is the caller's, recorded and deliberate.",
                nameof(testCase));
        }

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
    /// 🔴 <b>A report that DROPPED approval-gated calls is not a record, and is never returned as
    /// one.</b> <see cref="ToolUsageReport.DroppedApprovalRequestCount"/> is the library's own
    /// statement that <see cref="ToolUsageReport.Calls"/> is INCOMPLETE — its remarks say in as many
    /// words that "any absence-based policy (<c>NeverCallTool</c>) evaluated on this report has a
    /// chance floor of 1.0", because MAF wraps an approval-required call in a
    /// <c>ToolApprovalRequestContent</c> that the default extractor cannot see. Handing such a report
    /// to an eval as a list makes an unseeable call look like one that never happened, and an empty
    /// one manufactures a MEASURED ZERO out of a recorder that was blind — the flattering direction,
    /// on a safety question. So a dropping report is skipped: the timeline is consulted if it has
    /// invocations, and otherwise the answer is <see langword="null"/>, UNKNOWN.
    /// </para>
    /// <para>
    /// ⚠ <b>What this does NOT fix.</b> When a dropping report also carries recorded calls and no
    /// timeline is present, those calls are not returned either — the list would be a partial record
    /// presented as a whole one, and <see cref="EvalInput"/> has no per-input channel for "this list
    /// is partial". Recording that partiality without discarding evidence needs new API surface
    /// (a projection-authored <see cref="EvalInput.Metadata"/> key changes what
    /// <see cref="EvalInput.Metadata"/> means), which is an owner's decision, not this projection's.
    /// Until then the honest answer is the undecidable one. Opt into approval-aware extraction
    /// (<c>ToolUsageExtractor.Extract(rawMessages, includeApprovalGatedCalls: true)</c>) and the
    /// report stops dropping.
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
    /// approximations of both — and only the record path can say whether a call actually EXECUTED
    /// (see <see cref="ToolNotExecutedResultPrefix"/>); <see cref="ToolInvocation"/> has no such
    /// field, so a timeline invocation is taken at face value.
    /// </para>
    /// <para>
    /// ⚠ <b>A dropping report is blind, timeline or no timeline.</b> When
    /// <see cref="ToolUsageReport.DroppedApprovalRequestCount"/> is non-zero the projection returns
    /// <see langword="null"/> even if a <see cref="ToolCallTimeline"/> is attached, because the only
    /// producer derives that timeline from the same report
    /// (<c>MAFEvaluationHarness.cs:129 → PopulateTimelineFromToolUsage, :599</c>). Reading it as a
    /// second recorder turned an absence into <c>[]</c> — a MEASURED zero — on precisely the
    /// absence-based safety questions that cannot survive one. So: <see langword="null"/> means no
    /// recorder, or none that could see the whole run; an empty list means a complete recorder saw
    /// nothing.
    /// </para>
    /// <para>
    /// <b>Declared residual.</b> <c>MAFEvaluationHarness.RunEvaluationStreamingAsync</c> (<c>:257</c>)
    /// never records a drop count at all, so a streamed run cannot report this blindness. It has 0
    /// callers in <c>src/</c> and <c>samples/</c> and is not fixed here.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<ToolCall>? ProjectToolCalls(TestResult result)
    {
        // A report that admits it dropped approval-gated calls is not a record of the run, so it is
        // neither read as one nor counted as "a recorder was present". See the remarks.
        var usage = result.ToolUsage is { DroppedApprovalRequestCount: 0 } complete ? complete : null;

        if (usage is not null && usage.Calls.Count > 0)
        {
            return usage.Calls.OrderBy(c => c.Order).Select(ToToolCall).ToList();
        }

        // A dropping report is a BLIND recorder, and a timeline beside it is not a second opinion:
        // MAFEvaluationHarness.cs:129 builds that timeline from this very report
        // (PopulateTimelineFromToolUsage, :599), so it inherits the same blindness.
        if (result.ToolUsage is { DroppedApprovalRequestCount: > 0 })
        {
            return null;
        }

        if (result.Timeline is { } timeline && timeline.Invocations.Count > 0)
        {
            return timeline.Invocations.OrderBy(i => i.SequenceIndex).Select(FromInvocation).ToList();
        }

        // A recorder was attached and saw nothing: a MEASURED zero.
        if (usage is not null || result.Timeline is not null)
        {
            return Array.Empty<ToolCall>();
        }

        // No recorder at all, or none that could see the whole run: UNKNOWN. Not a zero.
        return null;
    }

    /// <summary>
    /// Projects one recorded tool call into the shape an <see cref="IEval"/> sees. Public so a
    /// consumer with its own runner gets the same marker rules without owning a <see cref="TestResult"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The three-way contract on <see cref="ToolCall.Result"/>.</b> <see langword="null"/> means
    /// the call ran, returned nothing and did not fail — the only honest empty. A string beginning
    /// <see cref="ToolNotExecutedResultPrefix"/> means the run says the call is not known to have
    /// executed. A string beginning <see cref="ToolErrorResultPrefix"/> means it ran and threw.
    /// </para>
    /// <para>
    /// <b>Composition order, and why.</b> Non-execution outranks failure, and failure outranks a
    /// recorded payload. Whatever was recorded about a call that may never have run is weaker than
    /// the fact that it may never have run, so the not-executed marker leads and the rest is appended
    /// after <c>|</c> rather than discarded. A recorded payload never outranks a failure: a call that
    /// threw and also left a partial result is a FAILED call, and reading the payload first turned a
    /// thrown call into one that worked.
    /// </para>
    /// <para>
    /// This is a rendering of facts already on the record, never an inference: a call with no
    /// exception, no payload and no non-execution reason projects <see langword="null"/>, which the
    /// caller must not read as an empty success.
    /// </para>
    /// </remarks>
    /// <param name="record">The recorded call.</param>
    /// <returns>The projected call.</returns>
    public static ToolCall ToToolCall(ToolCallRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new(record.Name, ArgumentsOf(record.Arguments), ResultOfRecord(record));
    }

    /// <summary>
    /// Renders one record's result, leading with the fact that it is not known to have executed when
    /// the run says so.
    /// </summary>
    /// <param name="record">The recorded call.</param>
    /// <returns>The text, or <see langword="null"/> only for a call that ran, returned nothing and did not fail.</returns>
    private static string? ResultOfRecord(ToolCallRecord record)
    {
        string? rendered = ResultOf(record.Result, record.Exception);
        string? notExecuted = NotExecutedReason(record);

        // Non-execution outranks both, because it is the stronger fact: whatever is below was
        // recorded ABOUT a call that may never have run.
        return notExecuted is null
            ? rendered
            : string.IsNullOrEmpty(rendered)
                ? string.Create(CultureInfo.InvariantCulture, $"{ToolNotExecutedResultPrefix} {notExecuted}")
                : string.Create(CultureInfo.InvariantCulture, $"{ToolNotExecutedResultPrefix} {notExecuted} | {rendered}");
    }

    /// <summary>
    /// Why this call is not known to have executed, or <see langword="null"/> when the run does not
    /// say it failed to.
    /// </summary>
    /// <param name="record">The recorded call.</param>
    /// <returns>The reason, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Every arm requires a non-null <see cref="ToolCallRecord.ApprovalState"/>, so a producer that
    /// does not use approval-aware extraction is untouched — see
    /// <see cref="ToolNotExecutedResultPrefix"/> for why <see cref="ToolCallRecord.WasExecuted"/>
    /// alone is not a usable signal.
    /// </remarks>
    private static string? NotExecutedReason(ToolCallRecord record) => record.ApprovalState switch
    {
        // "It never executed; a 'failed' result generated for the rejection does not count as one."
        ToolCallRecord.ApprovalRejected =>
            "the call was rejected at the approval gate and never executed",

        ToolCallRecord.ApprovalRequested when !record.WasExecuted =>
            "the call is awaiting human approval; no decision and no paired result were observed",

        // "It executed only if a paired result was also observed."
        ToolCallRecord.ApprovalApproved when !record.WasExecuted =>
            "the call was approved but no paired result was observed, so it is not known to have executed",

        _ => null,
    };

    private static ToolCall FromInvocation(ToolInvocation invocation) =>
        new(
            invocation.ToolName,
            ArgumentsOf(invocation.Arguments),
            invocation.Succeeded
                ? invocation.Result
                // ⚠ The result does NOT win over the failure. A failed invocation that also recorded a
                // payload used to project as that payload alone, so `Succeeded = false` and the error
                // message vanished and the call read as a successful one.
                : Failure(
                    invocation.ErrorMessage ?? "the tool call failed and no message was recorded",
                    invocation.Result));

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
        string? rendered = Render(value);

        // A call that FAILED still has something to say, and "failed" must not read as "returned
        // nothing" — a void-returning tool that succeeded looks exactly like that. ⚠ Nor may a
        // recorded payload outrank the failure: a tool that threw AFTER writing a partial result
        // used to project as that payload alone, with the exception dropped, so the call read as a
        // successful one. The marker leads; whatever was rendered follows it.
        return error is null ? rendered : Failure(error.Message, rendered);
    }

    /// <summary>Renders a recorded result object as text, or <see langword="null"/> when there was none.</summary>
    /// <param name="value">The recorded result object.</param>
    /// <returns>The text, or <see langword="null"/> only when <paramref name="value"/> is null.</returns>
    private static string? Render(object? value)
    {
        if (value is null) return null;

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

    /// <summary>
    /// Renders a failure, keeping anything the call did record beside the reason it failed.
    /// </summary>
    /// <param name="message">Why the call failed.</param>
    /// <param name="recorded">What the call recorded before or while failing, when it recorded anything.</param>
    /// <returns>A line that always begins with <see cref="ToolErrorResultPrefix"/>.</returns>
    private static string Failure(string? message, string? recorded = null) =>
        string.IsNullOrEmpty(recorded)
            ? string.Create(CultureInfo.InvariantCulture, $"{ToolErrorResultPrefix} {message}")
            : string.Create(CultureInfo.InvariantCulture, $"{ToolErrorResultPrefix} {message} | recorded result: {recorded}");

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

    /// <summary>Names a case for a fault message, without assuming any of its strings is non-null.</summary>
    /// <param name="testCase">The case.</param>
    /// <returns>Its id, else its name, else a placeholder.</returns>
    private static string Describe(TestCase testCase)
    {
        if (!string.IsNullOrWhiteSpace(testCase.Id)) return testCase.Id!;
        return string.IsNullOrWhiteSpace(testCase.Name) ? "(unnamed)" : testCase.Name;
    }

    private static JsonElement ParseDetached(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
