// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using AgentEval.Core;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Evidence;

namespace AgentEval.Cli.Microsoft365;

/// <summary>
/// The per-case turn sink of <c>--sut microsoft365</c>: receives every <see cref="WorkIqTurnResult"/> the agent produces
/// (completed or not: the agent invokes it before it returns or throws), keeps the last result per case and every result
/// in arrival order, and with <c>--m365-verbose</c> prints one stderr line per turn. The AEF evidence document (M31)
/// reads <see cref="LatestFor"/> (the case's turn since the case started). Thread-safe: the batch harness may run cases concurrently. Nothing is inferred: a
/// missing answer prints its completeness (<c>None</c>), never <c>0 chars</c>; an absent evidence channel prints its
/// availability (<c>NotRequested</c> / <c>NotProvided</c> …), never a count; a non-completed turn prints its outcome,
/// ids and the safe failure summary, never partial text.
/// </summary>
internal sealed class CliTurnSink
{
    /// <summary>Sinks attached to stand-in agents (<see cref="AttachTo"/>); weak keys, so no agent is kept alive.</summary>
    private static readonly ConditionalWeakTable<IEvaluableAgent, CliTurnSink> AttachedSinks = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, WorkIqTurnResult> _lastByCase = new(StringComparer.Ordinal);
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

    /// <summary>The last result recorded for each case id (a snapshot).</summary>
    public IReadOnlyDictionary<string, WorkIqTurnResult> LastByCase
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, WorkIqTurnResult>(_lastByCase, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Every result recorded, in arrival order (a snapshot).</summary>
    public IReadOnlyList<WorkIqTurnResult> All
    {
        get
        {
            lock (_gate)
            {
                return _all.ToArray();
            }
        }
    }

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
    /// (for example a turn refused before any send). <see cref="LastByCase"/> keeps a case's last result for the whole
    /// run, so when one case id runs several times (the trials of <c>--runs</c>) a trial that never reached the service
    /// would otherwise be handed the turn of an earlier trial; the cursor rules that out.
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
    /// Associates this sink with the agent whose <c>onTurn</c> callback feeds it, so a command that holds only the agent
    /// can read the agent's turn results back. Used for a <c>sutOverride</c> stand-in built in code: the credential-free
    /// seam of <see cref="Microsoft365SutTarget.Build"/> returns the stand-in untouched and so creates no sink of its own
    /// (<see cref="Microsoft365SutTarget.SinkFor"/> reads the target's sink first, then this association). The association
    /// is weak (it never keeps the agent alive), and a sink the agent does not feed yields no results, never invented ones.
    /// </summary>
    /// <param name="agent">The agent whose <c>onTurn</c> callback is <see cref="Record"/> of this sink.</param>
    public void AttachTo(IEvaluableAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        AttachedSinks.AddOrUpdate(agent, this);
    }

    /// <summary>The sink attached to <paramref name="agent"/> by <see cref="AttachTo"/>, or null when none was.</summary>
    /// <param name="agent">The agent.</param>
    public static CliTurnSink? AttachedTo(IEvaluableAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return AttachedSinks.TryGetValue(agent, out var sink) ? sink : null;
    }

    /// <summary>
    /// Records one turn result (the <c>onTurn</c> callback of the agent) and, when verbose, prints its line. A turn that
    /// completed with no selected answer prints a warning line even when not verbose (<see cref="FormatNoAnswerWarning"/>):
    /// the case is then graded on an empty string that is not the agent's answer, and the person reading the run must know.
    /// </summary>
    /// <param name="result">The result.</param>
    public void Record(WorkIqTurnResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var line = Verbose ? FormatLine(result) : null;
        if (result.IsCompleted && result.AnswerText is null)
        {
            line = line is null ? FormatNoAnswerWarning(result) : line + Environment.NewLine + FormatNoAnswerWarning(result);
        }
        lock (_gate)
        {
            _all.Add(result);
            _lastByCase[result.Identity.CaseId] = result;
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
    /// answer completeness (with the length only when text exists), citation-marker and tool-execution availability
    /// (with a count only when provided), elapsed milliseconds, and the safe failure summary when the turn did not complete.
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
            line.Append(" citationMarkers=").Append(DescribeSet(evidence.CitationMarkers))
                .Append(" toolExecutions=").Append(DescribeSet(evidence.ToolExecutions));
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
    /// response carries <c>FinishReason</c> <c>completed-no-answer</c> and an empty text that is not the agent's answer.
    /// </summary>
    /// <param name="result">A completed result without answer text.</param>
    internal static string FormatNoAnswerWarning(WorkIqTurnResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return $"[m365] WARNING case={OneLine(result.Identity.CaseId)} task={IdOrNone(result.Task?.TaskId ?? result.Identity.ProviderTaskId)}: " +
            $"the turn COMPLETED but no answer was selected (answer {result.AnswerCompleteness}); the case is graded on an empty text that is " +
            $"not the agent's answer (FinishReason '{WorkIqEvaluableAgent.CompletedNoAnswerFinishReason}'). The evidence document (--aef-content on) and the trace keep the candidates.";
    }

    private static string DescribeAnswer(WorkIqTurnResult result) =>
        result.AnswerText is { } text
            ? result.AnswerCompleteness + "(" + text.Length.ToString(CultureInfo.InvariantCulture) + " chars)"
            : result.AnswerCompleteness.ToString();

    private static string DescribeSet<T>(EvidenceSet<T> set) =>
        set is null ? "missing"
        : set.IsProvided ? "Provided(" + (set.Items.IsDefaultOrEmpty ? 0 : set.Items.Length).ToString(CultureInfo.InvariantCulture) + ")"
        : set.Availability.ToString();

    private static string IdOrNone(string? value) => string.IsNullOrEmpty(value) ? "none" : OneLine(value);

    private static string OneLine(string text) =>
        text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');
}
