// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Security.Cryptography;
using System.Text;

namespace AgentEval.Memory.External.TypedMemEval;

/// <summary>
/// The family's judge templates. Every semantic decision the judge makes is written down here.
/// </summary>
/// <remarks>
/// <para>
/// The precedence rules below are not stylistic guidance to the model — they are the definitions
/// the benchmark's numbers mean, ratified in review before any template was written. Leaving
/// mixed answers ("I'm not sure, but probably X"; "it was a Honda, but you sold it") to template
/// discretion would let two prompt revisions move a system's reported outcome distribution with no
/// system change, which is precisely the drift the pinned fingerprint exists to detect.
/// </para>
/// <para>
/// Templates are pinned by <see cref="Fingerprint"/>, disjoint from the frozen LongMemEval
/// judge-prompt fingerprint. A change here changes the fingerprint, which makes every stored
/// result carrying the old value visibly non-comparable rather than quietly so.
/// </para>
/// </remarks>
internal static class TypedMemEvalJudgePrompts
{
    /// <summary>
    /// The outcome definitions and precedence rules, identical for every question in the family.
    /// </summary>
    internal const string Preamble = """
        You are grading one answer from a memory system against a gold answer. Choose exactly one
        outcome. The outcome describes how the answer deviates from gold — not how it is phrased.

        OUTCOMES
        - correct   : matches gold.
        - wrong     : commits to a value that does not match gold.
        - abstained : declines to commit to any value ("I don't know", "I have no record of that").
        - missed    : confidently asserts that nothing is there ("nothing is due", "you never told
                      me about that") when gold says something is.
        - premature : asserts as already true something gold says has not happened yet.

        The line between abstained and missed is uncertainty versus denial. Both differ from wrong,
        which commits to a value.

        PRECEDENCE RULES — apply these before choosing:
        1. A stated value outranks hedging. "I'm not sure, but probably X" is graded on X: correct
           if X matches gold, wrong if it does not. Never abstained. Abstained requires declining to
           commit to any value at all.
        2. When gold itself is a negative — nothing is due yet, the fact was never recorded — an
           answer that correctly says so is CORRECT, not missed and not abstained. Grade against
           gold, never against the surface form of the sentence.
        3. Extra correct detail never lowers the outcome. Missing detail that gold treats as part of
           the answer does.
        """;

    private const string Closing = """

        QUESTION
        {0}

        GOLD ANSWER
        {1}

        MEMORY SYSTEM'S ANSWER
        {2}
        """;

    private const string StandardBody = """

        Grade this answer.
        """;

    private const string ProspectiveBody = """

        This question is about something located in time: a reminder that falls due, a validity that
        expires, or a change that has not happened yet. The gold answer already accounts for when the
        question was asked, so grade against gold rather than re-deriving the timing yourself.

        Use "premature" when gold says the thing has not happened yet and the answer says it has —
        the reminder fired early, the pass is described as expired before it expired, the move is
        described as done. That is a different failure from being wrong about a date, and it is the
        one this vertical exists to catch.

        PRECEDENCE — "premature" outranks "wrong" HERE, on this vertical only. Every premature
        answer also commits to a value gold does not support, so "wrong" is always literally
        available and the more specific label loses by default: a reminder that fired early was
        being graded "wrong about a date", which is the one distinction this vertical exists to
        draw. When the disagreement is that gold says a thing has NOT happened yet and the answer
        says it HAS, choose "premature". Use "wrong" only when the mismatch is something else.
        """;

    /// <remarks>
    /// Bitemporal shipped in 0.26.0-beta with no body of its own, so it was graded by the shared
    /// preamble alone - and that preamble defines "premature" as asserting as already true
    /// something gold says has not happened yet. Bitemporal golds justify themselves with exactly
    /// that sentence ("the correction had not been recorded yet"), so a capable judge read the
    /// justification as the proposition under test and returned premature where the label says
    /// wrong, in four of 24 cases in every run. The distinction is not pedantic: premature is a
    /// SCHEDULING defect and answering a past belief with the present record is a RETRIEVAL one,
    /// and they route to different repairs.
    /// </remarks>
    private const string BitemporalBody = """

        This question has TWO time coordinates and they are independent:
        - transaction time - the "as of <date>" instant, meaning what the RECORD CONTAINED then;
        - valid time - the period the fact itself is about.

        Gold answers the record as it stood at the as-of instant. Where gold adds a sentence like
        "the correction had not been recorded yet", that sentence is a JUSTIFICATION for gold's
        value. It is not a claim about an event, and it is not the thing being graded. Grade the
        VALUE the answer commits to against the value gold commits to.

        Which label applies depends on WHAT THE QUESTION ASKS FOR. Decide that first.

        - Asks WHICH VALUE the record held at the as-of instant, and the answer gives a
          later-recorded value - the current record, or anything written after that instant: the
          outcome is "wrong". That failure is a transaction-time collapse, the system returning what
          it knows now instead of what it held then. It is not "premature".

        - Asks WHETHER a correction, update or entry had been made by the as-of instant, and gold
          says it had not: an answer asserting that it HAD is "premature". What gold denies here is
          an EVENT, not a value, and asserting an event as already done when gold dates it later is
          exactly what premature means. Do NOT downgrade this to "wrong" on the grounds that yes and
          no are values - the question is about occurrence, not about which value was on file.

        An answer can do both at once: give the right value for the as-of instant AND assert a
        not-yet-made correction as already applied. When gold denies the correction, the premature
        assertion decides the outcome, because a correct value alongside a false claim of occurrence
        is still a claim that the record had moved on when it had not.

        Listing several values, one of which is gold's, is "wrong" and not "abstained" - it commits
        to a set containing a value the record did not hold at that instant.
        """;

    /// <remarks>
    /// Temporal shipped in 0.26.0-beta with no body of its own and fell through to
    /// <c>StandardBody</c>, so the only guidance it had was the shared preamble - which defines
    /// <i>missed</i> as confidently asserting that nothing is there when gold says something is.
    /// "Nothing happened between them" matches that word for word, so the judge returned Missed
    /// where the label says Wrong. But that answer ACCEPTS both anchors ("them") and makes a false
    /// claim about ordering, which is a different defect from not holding the events at all: one is
    /// a sequencing error, the other is a retrieval failure, and they route to different repairs.
    /// </remarks>
    private const string TemporalBody = """

        This question is about the ORDER of events - which came first, what fell between two of
        them, which is most recent. Session order and mention order are not evidence of event order;
        grade against gold.

        Two failures look alike here and are not. Decide which one the answer commits to:

        - The answer ACCEPTS that the events are on record and gets their order, position or
          recency wrong: that is "wrong". This includes denying that an interval contains anything
          ("nothing happened between them") and denying that any candidate is the latest, because
          both accept the events and make a false claim about how they are arranged. An empty
          interval is an ordering claim, not a statement about what the record holds.

        - The answer DENIES that the record holds the events at all ("neither is recorded", "none of
          those appear in the record", "I have no record of the Kessel handover"): that is "missed".
          The defect is that the events are not there to be ordered.

        When an answer does both - accepts one event and denies another the record states - the
        DENIAL decides, and the outcome is "missed". It has not committed to an ordering at all.

        """;

    private const string ArithmeticBody = """

        This question has a derived numeric answer. Grade the ARITHMETIC, not the phrasing.

        The gold derivation is: {3} of {4} = {5} {6}

        NUMERIC NORMALIZATION — apply exactly, do not improvise:
        - Extract the numeric value the answer commits to, ignoring currency symbols, thousands
          separators, and magnitude words ("about", "roughly", "just over").
        - An exactly-stated value is correct only if it equals the gold value.
        - An answer that offers only a rounded value is correct if and only if the gold value rounds
          to the precision the answer offered. "About 130" is correct for a gold of 128.75 to the
          nearest ten; it is not correct for a gold of 141.
        - An answer that states no number at all is abstained if it declines, missed if it denies
          there is anything to count or sum.
        - A correct number reached by visibly wrong reasoning is still correct: this benchmark scores
          the answer, and the reasoning field is where you say you noticed.
        - For a difference, DIRECTION IS PART OF THE VALUE, not phrasing. An answer with the right
          magnitude and the wrong direction — "went up by 46" where gold is 46 lower — is wrong. The
          instruction to grade the arithmetic rather than the phrasing means ignore wording and
          units-of-expression, never ignore a sign.
        """;

    private const string ListOrderBody = """

        This question asks for the ORDER in which things were mentioned across the conversation.

        Score it conditionally on what the answer actually mentions, because a memory system with a
        limited retrieval budget may only have seen some of the items, and grading it on items it
        never saw would measure the budget rather than the ordering:
        - Consider only items that appear in BOTH gold and the answer.
        - Count every pair of those items, and count how many are in the correct relative order.
        - Report both counts in ordered_pairs_total and ordered_pairs_correct.
        - Outcome is "correct" when at least two items are mentioned and every considered pair is in
          the right relative order. Any inversion is "wrong".
        - If the answer mentions fewer than two of the gold items, report zero for both counts and
          grade as "missed" if it denies knowing the items, "abstained" if it declines, otherwise
          "wrong".
        """;

    private const string ForgettingBody = """

        This question is about whether the system knows what it NO LONGER knows. Three states must
        be held apart, and conflating them is the failure this vertical measures:

        - Gold says a fact was superseded: "correct" means the answer says it is no longer the case.
          An answer that recalls the old value WHILE MARKING IT SUPERSEDED — "it was a Honda, but you
          sold it" — is CORRECT. That is ideal memory, not a mistake.
        - "wrong" with stale_value_asserted = true means the answer states the superseded value as
          if it were still current. That is the dangerous failure here; set the flag only for that.
        - Gold says the fact is still valid: an answer claiming it is no longer known is "wrong" —
          the system is forgetting something it should still hold.
        - Gold says the fact was never recorded: an answer that says so is "correct". An answer
          claiming to have once known it and since lost it is "wrong" — it is inventing a history of
          having known something it never did.

        Set stale_value_asserted to false in every case except the one named above.

        Set claimed_no_longer_known to true whenever the response asserts the fact is no longer
        known, no longer valid, or no longer held -- whether or not that assertion is correct. It is
        an observation about what the response said, not a judgement about whether saying it was
        right, and it is what separates a system that forgot the wrong thing from one that simply
        answered wrongly.
        """;

    internal static string Standard(string question, string gold, string answer)
        => string.Format(Preamble + StandardBody + Closing, question, gold, answer);

    internal static string Prospective(string question, string gold, string answer)
        => string.Format(Preamble + ProspectiveBody + Closing, question, gold, answer);

    internal static string Bitemporal(string question, string gold, string answer)
        => string.Format(Preamble + BitemporalBody + Closing, question, gold, answer);

    internal static string Temporal(string question, string gold, string answer)
        => string.Format(Preamble + TemporalBody + Closing, question, gold, answer);

    internal static string ListOrder(string question, string gold, string answer)
        => string.Format(Preamble + ListOrderBody + Closing, question, gold, answer);

    internal static string Forgetting(string question, string gold, string answer)
        => string.Format(Preamble + ForgettingBody + Closing, question, gold, answer);

    internal static string Arithmetic(
        string question, string gold, string answer,
        string operation, string inputs, string value, string unit)
        => string.Format(
            Preamble + ArithmeticBody + Closing,
            question, gold, answer, operation, inputs, value, unit);

    /// <summary>The JSON contract, appended to every prompt.</summary>
    /// <remarks>
    /// Sent whether or not the provider honours a response-format constraint, so an unconstrained
    /// provider still has a chance to comply and the parser still refuses to guess when it does not.
    /// </remarks>
    internal static string ContractFor(TypedMemEvalVerdict.Kind kind) => kind switch
    {
        TypedMemEvalVerdict.Kind.Forgetting => """

            Reply with a single JSON object and nothing else:
            {"outcome": "correct" | "wrong" | "abstained" | "missed" | "premature",
             "reasoning": "<one or two sentences>",
             "stale_value_asserted": true | false,
             "claimed_no_longer_known": true | false}
            """,
        TypedMemEvalVerdict.Kind.ListOrder => """

            Reply with a single JSON object and nothing else:
            {"outcome": "correct" | "wrong" | "abstained" | "missed" | "premature",
             "reasoning": "<one or two sentences>",
             "ordered_pairs_correct": <integer>,
             "ordered_pairs_total": <integer>}
            """,
        TypedMemEvalVerdict.Kind.Bitemporal => """

            Reply with a single JSON object and nothing else:
            {"outcome": "correct" | "wrong" | "abstained" | "missed" | "premature",
             "reasoning": "<one or two sentences>",
             "question_asks": "value" | "occurrence"}

            question_asks is the branch you took above: "value" if the question asks which value the
            record held at the as-of instant, "occurrence" if it asks whether a correction had been
            made by then. Report what you actually decided, not what would justify the outcome.
            """,
        TypedMemEvalVerdict.Kind.Temporal => """

            Reply with a single JSON object and nothing else:
            {"outcome": "correct" | "wrong" | "abstained" | "missed" | "premature",
             "reasoning": "<one or two sentences>",
             "question_asks": "ordering" | "presence"}

            question_asks is the branch you took above: "ordering" if the answer accepts the events
            and misplaces them, "presence" if it denies the record holds them. Report what you
            actually decided, not what would justify the outcome.
            """,
        _ => """

            Reply with a single JSON object and nothing else:
            {"outcome": "correct" | "wrong" | "abstained" | "missed" | "premature",
             "reasoning": "<one or two sentences>"}
            """
    };

    /// <summary>
    /// SHA-256 over every template and contract, newline-normalized.
    /// </summary>
    /// <remarks>
    /// Newline normalization for the same reason the corpus hash uses it: the value must identify
    /// the prompt <i>text</i>, not the line endings of the checkout that built the assembly.
    /// </remarks>
    internal static string Fingerprint { get; } = ComputeFingerprint();

    private static string ComputeFingerprint()
    {
        var builder = new StringBuilder()
            .Append(Preamble).Append('\u001e')
            .Append(StandardBody).Append('\u001e')
            .Append(ProspectiveBody).Append('\u001e')
            .Append(BitemporalBody).Append('\u001e')
            .Append(TemporalBody).Append('\u001e')
            .Append(ArithmeticBody).Append('\u001e')
            .Append(ListOrderBody).Append('\u001e')
            .Append(ForgettingBody).Append('\u001e')
            .Append(Closing).Append('\u001e')
            .Append(ContractFor(TypedMemEvalVerdict.Kind.Base)).Append('\u001e')
            .Append(ContractFor(TypedMemEvalVerdict.Kind.Forgetting)).Append('\u001e')
            .Append(ContractFor(TypedMemEvalVerdict.Kind.ListOrder)).Append('\u001e')
            .Append(ContractFor(TypedMemEvalVerdict.Kind.Bitemporal)).Append('\u001e')
            .Append(ContractFor(TypedMemEvalVerdict.Kind.Temporal));

        var normalized = builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        // Not a security function: this identifies which prompt text produced a verdict.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }
}
