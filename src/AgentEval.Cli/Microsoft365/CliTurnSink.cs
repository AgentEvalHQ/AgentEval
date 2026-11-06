// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Evidence;

namespace AgentEval.Cli.Microsoft365;

/// <summary>
/// The per-case turn sink of <c>--sut microsoft365</c>: receives every <see cref="WorkIqTurnResult"/> the agent produces
/// (completed or not: the agent invokes it before it returns or throws), keeps every result in arrival order, and with
/// <c>--m365-verbose</c> prints one stderr line per turn. The per-case evidence of an <c>eval</c> run reads
/// <see cref="LatestFor"/> (the case's turn since the case started). Thread-safe: the batch harness may run cases
/// concurrently. Nothing is inferred: a missing answer prints its completeness (<c>None</c>), never <c>0 chars</c>; an
/// evidence channel prints <see cref="EvidenceSet{T}.Describe"/> (its availability, and for a provided channel its
/// count, and its validation when not valid), never a bare count; a non-completed turn prints its outcome, ids and the
/// safe failure summary, never partial text.
/// </summary>
internal sealed class CliTurnSink
{
    private readonly object _gate = new();
    private readonly List<WorkIqTurnResult> _all = [];
    private readonly TextWriter _writer;

    /// <summary>Creates the sink.</summary>
    /// <param name="verbose">Whether to print one line per turn.</param>
    /// <param name="writer">Where the lines go; null means <see cref="Console.Error"/>.</param>
    public CliTurnSink(bool verbose, TextWriter? writer = null)
    {
        Verbose = verbose;
        _writer = writer ?? Console.Error;
    }

    /// <summary>Whether a line is printed per turn.</summary>
    public bool Verbose { get; }

    /// <summary>
    /// Whether the command records a turn with several answer candidates and none selected as not measured
    /// (<see cref="Microsoft365Evidence.IsAnswerMeasured(WorkIqTurnResult)"/>), so the warning says so. Set by the
    /// per-case evidence of an <c>eval</c> run before its first case; otherwise the warning says the empty text is graded.
    /// </summary>
    public bool UnselectedAnswerIsNotMeasured { get; set; }

    /// <summary>How many results were recorded.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _all.Count;
            }
        }
    }

    /// <summary>
    /// The last result recorded for <paramref name="caseId"/> at or after arrival index <paramref name="since"/> (the
    /// <see cref="Count"/> taken just before the case started), or null when the case produced no turn result since then
    /// (for example a turn refused before any send). When one case id runs several times (the trials of <c>--runs</c>), a
    /// trial that never reached the service would otherwise be handed the turn of an earlier trial; the cursor rules that
    /// out.
    /// </summary>
    /// <param name="caseId">The case id the turn identity carries (compared trimmed, as the agent records it).</param>
    /// <param name="since">The arrival index the case started at.</param>
    public WorkIqTurnResult? LatestFor(string caseId, int since)
    {
        ArgumentNullException.ThrowIfNull(caseId);
        var key = caseId.Trim();
        lock (_gate)
        {
            for (var i = _all.Count - 1; i >= Math.Max(0, since); i--)
            {
                if (string.Equals(_all[i].Identity.CaseId, key, StringComparison.Ordinal))
                {
                    return _all[i];
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Records one turn result (the <c>onTurn</c> callback of the agent) and, when verbose, prints its line. A turn that
    /// completed with no selected answer prints a warning line even when not verbose (<see cref="FormatNoAnswerWarning"/>):
    /// the response text is then an empty string, and the person reading the run must know what became of the case.
    /// </summary>
    /// <param name="result">The result.</param>
    public void Record(WorkIqTurnResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var line = Verbose ? FormatLine(result) : null;
        if (result.IsCompleted && result.AnswerText is null)
        {
            var warning = FormatNoAnswerWarning(result, UnselectedAnswerIsNotMeasured);
            line = line is null ? warning : line + Environment.NewLine + warning;
        }
        lock (_gate)
        {
            _all.Add(result);
            if (line is null)
            {
                return;
            }

            try
            {
                _writer.WriteLine(line);
                _writer.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // A closed stderr must not fail the run; the result is retained regardless.
            }
        }
    }

    /// <summary>
    /// Formats one turn as the single stderr line: case, turn, outcome, transport, task id, context id, raw task state,
    /// answer completeness (with the length only when text exists), citation-marker and tool-execution evidence
    /// (<see cref="EvidenceSet{T}.Describe"/>), elapsed milliseconds, and the safe failure summary when the turn did not
    /// complete.
    /// </summary>
    /// <param name="result">The result.</param>
    internal static string FormatLine(WorkIqTurnResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var identity = result.Identity;
        var evidence = result.Evidence;
        var line = new StringBuilder(256)
            .Append("[m365 turn] case=").Append(OneLine(identity.CaseId))
            .Append(" turn=").Append(OneLine(identity.TurnId))
            .Append(" outcome=").Append(result.Outcome)
            .Append(" transport=").Append(result.Transport)
            .Append(" task=").Append(IdOrNone(result.Task?.TaskId ?? identity.ProviderTaskId))
            .Append(" ctx=").Append(IdOrNone(result.ContextId ?? identity.ProviderContextId))
            .Append(" state=").Append(IdOrNone(evidence?.TaskState))
            .Append(" answer=").Append(DescribeAnswer(result));

        if (evidence is null)
        {
            line.Append(" evidence=missing");
        }
        else
        {
            line.Append(" citationMarkers=").Append(evidence.CitationMarkers?.Describe("marker(s)") ?? "missing")
                .Append(" toolExecutions=").Append(evidence.ToolExecutions?.Describe("execution(s)") ?? "missing");
        }

        line.Append(" elapsedMs=").Append(result.Timing.Elapsed.TotalMilliseconds.ToString("0.#", CultureInfo.InvariantCulture));

        if (result.Failure is { } failure)
        {
            line.Append(" failure=").Append(failure.Category).Append(": ").Append(OneLine(failure.SafeMessage));
        }

        return line.ToString();
    }

    /// <summary>
    /// The warning printed for a turn that completed with no selected answer (answer <c>Ambiguous</c> or <c>None</c>): the
    /// response carries <c>FinishReason</c> <c>completed-no-answer</c> and an empty text. With several candidates and none
    /// selected (<see cref="Microsoft365Evidence.IsAnswerMeasured(WorkIqTurnResult)"/> is false) the empty text is not the
    /// agent's answer: the warning says the case is recorded as not measured when <paramref name="unselectedAnswerIsNotMeasured"/>,
    /// else that the empty text is graded. With no answer at all (<c>None</c>) the case is graded on the empty text.
    /// </summary>
    /// <param name="result">A completed result without answer text.</param>
    /// <param name="unselectedAnswerIsNotMeasured">Whether the command records a turn with no selected answer as not measured.</param>
    internal static string FormatNoAnswerWarning(WorkIqTurnResult result, bool unselectedAnswerIsNotMeasured)
    {
        ArgumentNullException.ThrowIfNull(result);
        var head = $"[m365] WARNING case={OneLine(result.Identity.CaseId)} task={IdOrNone(result.Task?.TaskId ?? result.Identity.ProviderTaskId)}: " +
            $"the turn COMPLETED but no answer was selected (answer {result.AnswerCompleteness}); ";
        var finishReason = $"(FinishReason '{WorkIqEvaluableAgent.CompletedNoAnswerFinishReason}')";
        if (Microsoft365Evidence.IsAnswerMeasured(result))
        {
            return head + $"the agent gave no answer, so the case is graded on an empty text {finishReason}. " +
                "The evidence document (--aef-content on) and the trace keep the turn.";
        }

        return head + (unselectedAnswerIsNotMeasured
                ? $"the case is recorded as NOT MEASURED, because the empty text is not the agent's answer {finishReason}. "
                : $"the case is graded on an empty text that is not the agent's answer {finishReason}. ") +
            "The evidence document (--aef-content on) and the trace keep the candidates.";
    }

    private static string DescribeAnswer(WorkIqTurnResult result) =>
        result.AnswerText is { } text
            ? result.AnswerCompleteness + "(" + text.Length.ToString(CultureInfo.InvariantCulture) + " chars)"
            : result.AnswerCompleteness.ToString();

    private static string IdOrNone(string? value) => string.IsNullOrEmpty(value) ? "none" : OneLine(value);

    private static string OneLine(string text) =>
        text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');
}
