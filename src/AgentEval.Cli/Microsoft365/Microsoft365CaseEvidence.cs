// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Cli.Commands;
using AgentEval.Cli.Commands.Targets;
using AgentEval.Core;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Models;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Writing;

namespace AgentEval.Cli.Microsoft365;

/// <summary>
/// The <c>--sut microsoft365</c> side of an <c>eval</c> run's AEF evidence: the content-free provenance the target adds
/// to the run's config settings (with the agent's content policy), the not-measured rules (a turn that completed with
/// several answer candidates and none selected, and criteria that no judge graded), and, with content capture on, each
/// case's evidence document. All three
/// read the turn result the agent's turn sink recorded (the sink <see cref="Microsoft365SutTarget.Build"/> wired into the
/// live agent, or <see cref="Microsoft365SutTarget.TurnSinkForTests"/> for a stand-in). A case is matched to its turn by
/// its AEF case id: before the case (or trial) runs, the agent is told to record that id in its turn identity
/// (<see cref="WorkIqEvaluableAgent.NextCaseId"/>, which also makes the case ids in the trace and issue files the AEF case
/// ids), and the case's turn is the last one the sink recorded for that id since then. A case whose turn produced no
/// result (refused before any send) is left as the harness recorded it and gets no document: nothing is inferred,
/// nothing is borrowed from another case.
/// </summary>
internal sealed class Microsoft365CaseEvidence : ISutCaseEvidence
{
    private readonly CliTurnSink? _sink;
    private readonly WorkIqEvaluableAgent? _agent;
    private readonly EvidenceContentPolicy? _policy;
    private int _since;

    private Microsoft365CaseEvidence(JsonObject? runSettings, CliTurnSink? sink, WorkIqEvaluableAgent? agent, EvidenceContentPolicy? policy)
    {
        RunSettings = runSettings;
        _sink = sink;
        _agent = agent;
        _policy = policy;
    }

    /// <inheritdoc/>
    public string SubjectProvider => Microsoft365SutTarget.SutName;

    /// <summary>The provenance keys for the run's config settings (<see cref="Microsoft365SutTarget.AefRunSettings"/>); null when the target loaded no config.</summary>
    public JsonObject? RunSettings { get; }

    /// <summary>
    /// The evidence of an <c>eval</c> run against <paramref name="agent"/>. The documents are projected under the policy
    /// the agent was built with (what its evidence may retain), and the turn sink's warning says that a turn with no
    /// selected answer is recorded as not measured.
    /// </summary>
    /// <param name="target">The target the command validated and built the agent with.</param>
    /// <param name="agent">The agent the target returned.</param>
    internal static Microsoft365CaseEvidence For(Microsoft365SutTarget target, IEvaluableAgent agent)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(agent);
        var workIq = agent as WorkIqEvaluableAgent;
        var policy = workIq?.Options.ContentPolicy;
        var sink = target.TurnSink;
        if (sink is not null)
        {
            sink.UnselectedAnswerIsNotMeasured = true;
        }

        return new(target.AefRunSettings(policy), sink, workIq, policy);
    }

    /// <summary>Marks the start of a case or trial: remembers where the sink stands and tells the agent the case id.</summary>
    /// <param name="caseId">The AEF case id.</param>
    public void BeginCase(string caseId)
    {
        _since = _sink?.Count ?? 0;
        if (_agent is not null)
        {
            _agent.NextCaseId = caseId;
        }
    }

    /// <summary>
    /// Records a case the run did not measure as not measured, the way the command records a turn that did not
    /// complete: an error row with no score, so it is neither a pass nor a failure. A case qualifies when its turn
    /// completed with several answer candidates and none selected
    /// (<see cref="Microsoft365Evidence.IsAnswerMeasured(WorkIqTurnResult)"/> is false). The harness graded the empty text
    /// that stands in for the answer. That grade is not a measurement of the agent, so it is dropped, and the empty text
    /// is not recorded as the agent's output. The reason is <see cref="Microsoft365Evidence.NoAnswerSelectedReason"/>.
    /// A result that already carries an error is left as it is. The AEF run records the case as <c>not_measured</c>.
    /// Criteria no judge graded are the command's rule for every target (<see cref="NotMeasuredCases.ReviewCriteria"/>).
    /// </summary>
    /// <param name="result">The case's (or trial's) result.</param>
    /// <param name="caseId">The AEF case id given to <see cref="BeginCase"/>.</param>
    /// <returns>The case's AEF absence when it was marked not measured; otherwise null.</returns>
    public AefCaseAbsence? ReviewResult(TestResult result, string caseId)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(caseId);
        if (result.Error is not null)
        {
            return null;
        }

        if (_sink?.LatestFor(caseId, _since) is { } turn && !Microsoft365Evidence.IsAnswerMeasured(turn))
        {
            // The empty text is not the agent's answer: no output is recorded for it (the evidence document keeps the candidates).
            result.ActualOutput = null;
            NotMeasuredCases.Mark(result, new Microsoft365NoAnswerSelectedException(turn));
            return new AefCaseAbsence(AefState.NotMeasured, Microsoft365Evidence.NoAnswerSelectedReason);
        }

        return null;
    }

    /// <summary>The case's evidence document under the agent's content policy, or null when the case left no turn result.</summary>
    /// <param name="caseId">The AEF case id given to <see cref="BeginCase"/>.</param>
    public string? DocumentFor(string caseId)
    {
        if (_sink is null || _policy is not { } policy || _sink.LatestFor(caseId, _since) is not { Evidence: { } turnEvidence })
        {
            return null;
        }

        try
        {
            return Microsoft365EvidenceProjector.ToAefDocument(turnEvidence, policy);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            // The verdict stands; only the document is missing. Say so, and never write a substitute.
            Console.Error.WriteLine(
                $"  Warning: the Microsoft 365 evidence document of case '{caseId}' could not be serialized ({ex.GetType().Name}); " +
                "the case is recorded without it. The m365 trace file still records the turn's outcome and evidence states.");
            return null;
        }
    }
}

/// <summary>
/// The not-measured mark of a case whose Work IQ turn completed with several answer candidates and none selected: the
/// response text was an empty string that is not the agent's answer. <c>eval</c> sets it as the case's
/// <see cref="TestResult.Error"/> (an error row, not a failure). A <c>bench</c> run's agent
/// (<see cref="Microsoft365BenchAgent"/>) throws it, so the family records an error instead of grading the empty text.
/// </summary>
internal sealed class Microsoft365NoAnswerSelectedException : Exception
{
    /// <summary>Creates the mark for <paramref name="turn"/>.</summary>
    /// <param name="turn">The completed turn with no selected answer.</param>
    public Microsoft365NoAnswerSelectedException(WorkIqTurnResult turn)
        : base(MessageFor(turn))
    {
    }

    /// <summary>Creates the mark for a response whose turn result is not at hand (the <c>bench</c> path).</summary>
    public Microsoft365NoAnswerSelectedException()
        : base("Not measured: the Work IQ turn completed with several answer candidates and none selected, so its empty text is not the agent's answer and was not graded.")
    {
    }

    private static string MessageFor(WorkIqTurnResult turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var taskId = turn.Task?.TaskId ?? turn.Identity.ProviderTaskId;
        return $"Not measured: the Work IQ turn completed with no selected answer (answer {turn.AnswerCompleteness}, " +
            $"task={(string.IsNullOrEmpty(taskId) ? "none" : taskId)}), so its empty text is not the agent's answer. " +
            "The evidence document (--aef-content on) and the m365 trace keep the candidates.";
    }
}

