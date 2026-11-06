// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;

namespace AgentEval.Microsoft365Demo;

/// <summary>
/// The <c>onTurn</c> sink of the live agent (<see cref="Microsoft365AgentFactory.BuildLive"/>): for every turn result,
/// completed or not, it appends one metadata line to <c>turns.jsonl</c> (ids, outcome, timing, failure category, codes
/// and request-id; never the answer text), writes the evidence document to <c>evidence/&lt;caseId&gt;.json</c> under the
/// run's content policy, shows one line per turn, and keeps the result for the benchmark's verdicts. It never throws:
/// the agent rethrows a sink's exception, which would turn a recording problem into a failed turn.
/// </summary>
internal sealed class TurnRecorder
{
    /// <summary>The schema id of a <c>turns.jsonl</c> line.</summary>
    public const string TurnSchema = "agenteval.m365.demo-turn/v1";

    private readonly RunFolder _folder;
    private readonly DemoConsole _console;
    private readonly EvidenceContentPolicy _policy;
    private readonly List<WorkIqTurnResult> _turns = [];
    private readonly Dictionary<WorkIqTurnResult, string> _evidenceFiles = new(ReferenceEqualityComparer.Instance);
    private readonly object _gate = new();

    /// <summary>Creates the recorder.</summary>
    /// <param name="folder">The run folder.</param>
    /// <param name="console">The demo console.</param>
    /// <param name="policy">The run's content policy (applied to the evidence files).</param>
    public TurnRecorder(RunFolder folder, DemoConsole console, EvidenceContentPolicy policy)
    {
        _folder = folder ?? throw new ArgumentNullException(nameof(folder));
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _policy = policy;
    }

    /// <summary>How many turns were recorded.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _turns.Count;
            }
        }
    }

    /// <summary>The latest turn of <paramref name="caseId"/> recorded at or after position <paramref name="since"/>; null when none.</summary>
    /// <param name="caseId">The case id.</param>
    /// <param name="since">The recorder's <see cref="Count"/> before the case started.</param>
    public WorkIqTurnResult? LatestFor(string caseId, int since = 0)
    {
        lock (_gate)
        {
            for (var i = _turns.Count - 1; i >= Math.Max(0, since); i--)
            {
                if (string.Equals(_turns[i].Identity.CaseId, caseId, StringComparison.Ordinal))
                {
                    return _turns[i];
                }
            }

            return null;
        }
    }

    /// <summary>The run-folder-relative evidence file of a recorded turn; null when it was not written.</summary>
    /// <param name="turn">The turn.</param>
    public string? EvidenceFileOf(WorkIqTurnResult turn)
    {
        lock (_gate)
        {
            return _evidenceFiles.TryGetValue(turn, out var path) ? path : null;
        }
    }

    /// <summary>The <c>onTurn</c> callback.</summary>
    /// <param name="result">The turn result.</param>
    public void OnTurn(WorkIqTurnResult result)
    {
        try
        {
            if (result is null)
            {
                return;
            }

            lock (_gate)
            {
                _turns.Add(result);
            }

            _folder.TryAppendJsonLine(_folder.Turns, ToLine(result));

            var evidenceFile = _folder.EvidenceFileFor(result.Identity.CaseId);
            string document;
            try
            {
                document = Indent(Microsoft365EvidenceProjector.ToAefDocument(result.Evidence, _policy));
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
            {
                document = "";
                _folder.RecordError($"{_folder.Relative(evidenceFile.FullName)}: the evidence document could not be serialized ({ex.GetType().Name}); no substitute was written");
            }

            if (document.Length > 0 && _folder.TryWriteText(evidenceFile, document))
            {
                lock (_gate)
                {
                    _evidenceFiles[result] = _folder.Relative(evidenceFile.FullName);
                }
            }

            _console.Line(DemoVerbosity.Normal, TurnLine(result), result.IsCompleted ? Tint.None : Tint.Bad);
            _console.Line(DemoVerbosity.Detailed, TurnDetail(result), Tint.Dim);
        }
        catch (Exception ex)
        {
            // The agent would rethrow this and turn a recording problem into a failed turn; record it instead.
            _folder.RecordError($"turn sink: {ex.GetType().Name}: {WorkIqRedaction.Redact(ex.Message)}");
        }
    }

    /// <summary>One line per turn: case, outcome, elapsed, answer length, ids, and the failure when the turn did not complete.</summary>
    /// <param name="r">The turn.</param>
    public static string TurnLine(WorkIqTurnResult r)
    {
        var line = new StringBuilder(200)
            .Append("  turn  ").Append(r.Identity.CaseId)
            .Append("  ").Append(r.Outcome)
            .Append("  ").Append(Seconds(r.Timing.Elapsed))
            .Append("  answer ").Append(DescribeAnswer(r));
        var task = r.Task?.TaskId ?? r.Identity.ProviderTaskId;
        var context = r.ContextId ?? r.Identity.ProviderContextId;
        line.Append("  task=").Append(task ?? "(none)").Append(" ctx=").Append(context ?? "(none)");
        if (r.Failure is { } f)
        {
            line.Append("  ").Append(FailureCodes(f));
        }

        return line.ToString();
    }

    /// <summary>The detailed view of a turn: transport, telemetry, timing, evidence availability and the failure message.</summary>
    /// <param name="r">The turn.</param>
    public static string TurnDetail(WorkIqTurnResult r)
    {
        var detail = new StringBuilder(300)
            .Append("        transport ").Append(r.Transport)
            .Append(", telemetry ").Append(r.Telemetry)
            .Append(", task state ").Append(r.Evidence?.TaskState ?? "(none)")
            .Append(", recovery requests ").Append(r.Timing.RecoveryRequests.ToString(CultureInfo.InvariantCulture))
            .Append(", turn ").Append(r.Identity.TurnId)
            .Append('\n')
            .Append("        evidence: ").Append(EvidenceAvailability(r.Evidence));
        if (r.Failure is { } f)
        {
            detail.Append('\n').Append("        failure: ").Append(f.Category).Append(" during ").Append(f.Operation)
                .Append(" (").Append(f.Certainty).Append("): ").Append(IssueText.Flatten(f.SafeMessage));
        }

        if (r.PendingInteraction is { } pending)
        {
            detail.Append('\n').Append("        paused: ").Append(pending.Kind).Append(", ")
                .Append(pending.ProposedCalls.IsDefaultOrEmpty ? 0 : pending.ProposedCalls.Length).Append(" proposed call(s); nothing was approved");
        }

        return detail.ToString();
    }

    /// <summary>
    /// The availability of every evidence channel, as one line (<c>answer Present; citation markers Provided (2 marker(s)); …</c>).
    /// Each channel is described by <see cref="EvidenceSet{T}.Describe"/>, so an observed-empty channel and a provided
    /// channel that is not valid never print alike.
    /// </summary>
    /// <param name="e">The evidence document.</param>
    public static string EvidenceAvailability(Microsoft365TurnEvidence? e)
    {
        if (e is null)
        {
            return "no evidence document";
        }

        return string.Join("; ", new[]
        {
            "answer " + (e.Answer is null ? "none" : e.Answer.RawText.State.ToString()),
            "citation markers " + e.CitationMarkers.Describe("marker(s)"),
            "citation sources " + e.CitationSources.Describe("source(s)"),
            "tool selections " + e.ToolSelections.Describe("selection(s)"),
            "tool executions " + e.ToolExecutions.Describe("execution(s)"),
            "approval intents " + e.ApprovalIntents.Describe("intent(s)"),
            "retrieval executions " + e.RetrievalExecutions.Describe("execution(s)"),
            "skill activations " + e.SkillActivations.Describe("activation(s)"),
        });
    }

    /// <summary>
    /// Why a turn did not complete, in one line: the outcome, the failure's category, codes, request-id and safe message
    /// when there is one, and the pause (its kind and how many calls it proposed; nothing is ever approved).
    /// </summary>
    /// <param name="r">The turn.</param>
    public static string NotCompletedSummary(WorkIqTurnResult r)
    {
        var text = new StringBuilder(200).Append("outcome ").Append(r.Outcome);
        if (r.Failure is { } f)
        {
            text.Append(", ").Append(FailureCodes(f)).Append(": ").Append(IssueText.Flatten(f.SafeMessage));
        }

        if (r.PendingInteraction is { } p)
        {
            text.Append(", paused for ").Append(p.Kind).Append(" (")
                .Append((p.ProposedCalls.IsDefaultOrEmpty ? 0 : p.ProposedCalls.Length).ToString(CultureInfo.InvariantCulture))
                .Append(" proposed call(s)); nothing was approved");
        }

        return text.ToString();
    }

    /// <summary>The HTTP status, JSON-RPC code and request-id of a failure, as one segment.</summary>
    /// <param name="f">The failure.</param>
    public static string FailureCodes(WorkIqFailure f)
    {
        var parts = new List<string> { f.Category.ToString() };
        if (f.HttpStatus is { } http)
        {
            parts.Add("HTTP " + http.ToString(CultureInfo.InvariantCulture));
        }

        if (f.RpcErrorCode is { } rpc)
        {
            parts.Add("JSON-RPC " + rpc.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(f.RequestId))
        {
            parts.Add("request-id=" + f.RequestId);
        }

        return string.Join(" ", parts);
    }

    /// <summary>The answer as the turn line describes it: a length, or why there is none (never the text).</summary>
    /// <param name="r">The turn.</param>
    public static string DescribeAnswer(WorkIqTurnResult r) => r.AnswerText is { } text
        ? text.Length.ToString(CultureInfo.InvariantCulture) + " chars" + (r.AnswerCompleteness == OutputCompleteness.Complete ? "" : " (" + r.AnswerCompleteness + ")")
        : "none (" + r.AnswerCompleteness + ")";

    /// <summary>Seconds with one decimal (<c>2.3 s</c>).</summary>
    /// <param name="elapsed">The duration.</param>
    public static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    private static TurnLineRecord ToLine(WorkIqTurnResult r)
    {
        var timing = r.Timing;
        return new TurnLineRecord(
            TurnSchema,
            r.Identity.RunId,
            r.Identity.CaseId,
            r.Identity.TurnId,
            r.Identity.TurnIndex,
            r.Outcome.ToString(),
            r.Transport.ToString(),
            r.AnswerCompleteness.ToString(),
            r.Telemetry.ToString(),
            r.Task?.TaskId ?? r.Identity.ProviderTaskId,
            r.ContextId ?? r.Identity.ProviderContextId,
            r.Evidence?.TaskState,
            r.AnswerText?.Length,
            timing.StartedAt,
            timing.EndedAt,
            Math.Round(timing.Elapsed.TotalMilliseconds, 1),
            timing.FirstEventAt is { } first ? Math.Round((first - timing.StartedAt).TotalMilliseconds, 1) : null,
            timing.FirstAnswerAt is { } answer ? Math.Round((answer - timing.StartedAt).TotalMilliseconds, 1) : null,
            timing.RecoveryRequests,
            EvidenceAvailability(r.Evidence),
            r.Failure is { } f
                ? new TurnFailureRecord(
                    f.Category.ToString(),
                    f.Operation.ToString(),
                    f.Certainty.ToString(),
                    f.HttpStatus,
                    f.RpcErrorCode,
                    f.RemoteTaskState,
                    f.RequestId,
                    f.RpcRequestId,
                    WorkIqRedaction.Redact(f.SafeMessage))
                : null,
            r.PendingInteraction is { } p ? p.Kind.ToString() : null);
    }

    private static string Indent(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            document.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>One <c>turns.jsonl</c> line (metadata only).</summary>
    private sealed record TurnLineRecord(
        string Schema,
        string RunId,
        string CaseId,
        string TurnId,
        int TurnIndex,
        string Outcome,
        string Transport,
        string AnswerCompleteness,
        string Telemetry,
        string? TaskId,
        string? ContextId,
        string? TaskState,
        int? AnswerLength,
        DateTimeOffset StartedAt,
        DateTimeOffset EndedAt,
        double ElapsedMs,
        double? FirstEventMs,
        double? FirstAnswerMs,
        int RecoveryRequests,
        string Evidence,
        TurnFailureRecord? Failure,
        string? PendingInteraction);

    /// <summary>The failure of a turn that did not complete (safe message, codes and ids only).</summary>
    private sealed record TurnFailureRecord(
        string Category,
        string Operation,
        string Certainty,
        int? HttpStatus,
        int? RpcErrorCode,
        string? RemoteTaskState,
        string? RequestId,
        string? RpcRequestId,
        string Message);
}
