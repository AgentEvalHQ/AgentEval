// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365.Evidence;

namespace AgentEval.Microsoft365.Conversation;

/// <summary>
/// The local state of one conversation: the server context, the active and the last task, the last outcome and
/// capture, the task the service confirmed as canceled, the turn counter and the epoch. It also tracks the turn that is
/// running: whether it recorded its result yet, and the task it works on once the service named one. A reset or an
/// abandonment starts a new epoch, so a turn that ran across it cannot write its context back. Thread-safe.
/// </summary>
internal sealed class ConversationState
{
    private readonly object _gate = new();
    private string? _contextId;
    private WorkIqTaskHandle? _activeTask;
    private WorkIqTaskHandle? _lastTask;
    private WorkIqTurnOutcome? _lastOutcome;
    private RawCaptureRef? _lastCapture;
    private string? _confirmedCanceledTaskId;
    private WorkIqTaskHandle? _runningTask;
    private bool _turnRunning;
    private int _turnIndex;
    private int _epoch;

    /// <summary>The server context the conversation holds, or null.</summary>
    public string? ContextId
    {
        get
        {
            lock (_gate)
            {
                return _contextId;
            }
        }
    }

    /// <summary>The task that can still be addressed (running or paused), or null.</summary>
    public WorkIqTaskHandle? ActiveTask
    {
        get
        {
            lock (_gate)
            {
                return _activeTask;
            }
        }
    }

    /// <summary>The task the service confirmed as <c>TASK_STATE_CANCELED</c> through this conversation, when any.</summary>
    public string? ConfirmedCanceledTaskId => Volatile.Read(ref _confirmedCanceledTaskId);

    /// <summary>Starts a turn: its index (counted from 0), the current epoch and the context the turn may continue.</summary>
    public (int TurnIndex, int Epoch, string? ContextId) StartTurn()
    {
        lock (_gate)
        {
            _turnRunning = true;
            _runningTask = null;
            return (_turnIndex++, _epoch, _contextId);
        }
    }

    /// <summary>
    /// Records the task the running turn works on, as soon as the service names it. An abandonment that the turn does
    /// not wait out can then still name that task. A turn from an older epoch changes nothing.
    /// </summary>
    /// <param name="epoch">The turn's epoch.</param>
    /// <param name="task">The task handle.</param>
    public void NoteRunningTask(int epoch, WorkIqTaskHandle task)
    {
        lock (_gate)
        {
            if (epoch == _epoch && _turnRunning)
            {
                _runningTask = task;
            }
        }
    }

    /// <summary>The context a send continues: the current one under <see cref="ConversationPolicy.ContinueContext"/>, else none.</summary>
    /// <param name="policy">The conversation policy.</param>
    public string? ContextToContinue(ConversationPolicy policy)
    {
        lock (_gate)
        {
            return policy == ConversationPolicy.ContinueContext ? _contextId : null;
        }
    }

    /// <summary>
    /// What an abandonment reports, as the state stands now: the context, the task (the running turn's, else the last,
    /// else the active one), the last outcome (<see cref="WorkIqTurnOutcome.Indeterminate"/> while a turn is running and
    /// has not recorded its result) and the last capture.
    /// </summary>
    public (string? ContextId, WorkIqTaskHandle? Task, WorkIqTurnOutcome? Outcome, RawCaptureRef? Capture) Peek()
    {
        lock (_gate)
        {
            return Describe();
        }
    }

    /// <summary>Drops the local context, the tasks, the last outcome and the last capture, and starts a new epoch.</summary>
    /// <returns>The context id that was dropped, or null.</returns>
    public string? Reset() => ResetAndDescribe().ContextId;

    /// <summary>
    /// Takes what an abandonment reports (<see cref="Peek"/>) and drops it in one step, under one lock, so no turn can
    /// record a result in between. Call it after the abandoned turn had its chance to stop: a turn that stopped recorded
    /// its own task and outcome, and a turn that did not stop is named by its running task with an indeterminate outcome.
    /// </summary>
    public (string? ContextId, WorkIqTaskHandle? Task, WorkIqTurnOutcome? Outcome, RawCaptureRef? Capture) ResetAndDescribe()
    {
        lock (_gate)
        {
            var described = Describe();
            _epoch++;
            _contextId = null;
            _activeTask = null;
            _lastTask = null;
            _lastOutcome = null;
            _lastCapture = null;
            _runningTask = null;
            _turnRunning = false;
            return described;
        }
    }

    // Runs under _gate.
    private (string? ContextId, WorkIqTaskHandle? Task, WorkIqTurnOutcome? Outcome, RawCaptureRef? Capture) Describe() =>
        (_contextId, _runningTask ?? _lastTask ?? _activeTask, _turnRunning ? WorkIqTurnOutcome.Indeterminate : _lastOutcome, _lastCapture);

    /// <summary>Records that the service confirmed the task as canceled.</summary>
    /// <param name="taskId">The task id.</param>
    public void ConfirmCanceled(string taskId) => Volatile.Write(ref _confirmedCanceledTaskId, taskId);

    /// <summary>
    /// Records a finished turn: its context, its task and whether that task can still be addressed, its outcome and
    /// its last capture. A turn that started in an older epoch changes nothing.
    /// </summary>
    /// <param name="ctx">The turn.</param>
    /// <param name="result">Its result.</param>
    public void RecordTurn(TurnContext ctx, WorkIqTurnResult result)
    {
        lock (_gate)
        {
            if (ctx.Epoch != _epoch)
            {
                return; // The context was reset or abandoned while this turn ran: it must not bring it back.
            }

            _turnRunning = false;
            _runningTask = null;
            if (ctx.ContextId is not null)
            {
                _contextId = ctx.ContextId;
            }

            if (result.Task is not null)
            {
                _lastTask = result.Task;
            }

            var addressable = ctx.Snapshot is { } s && (s.State is WorkIqTaskState.Submitted or WorkIqTaskState.Working || s.IsSuspended);
            _activeTask = addressable ? result.Task : null;
            _lastOutcome = result.Outcome;
            if (ctx.LastCapture is not null)
            {
                _lastCapture = ctx.LastCapture;
            }
        }
    }

    /// <summary>
    /// Records an explicit task read: a confirmed cancellation, the read's capture, and the end of the active task
    /// when the read shows it in a final state.
    /// </summary>
    /// <param name="snapshot">The task as read.</param>
    /// <param name="capture">The read's capture.</param>
    public void RecordExplicitRead(WorkIqTaskSnapshot snapshot, RawCaptureRef capture)
    {
        if (snapshot.State == WorkIqTaskState.Canceled)
        {
            ConfirmCanceled(snapshot.Handle.TaskId);
        }

        lock (_gate)
        {
            _lastCapture = capture;
            if (_activeTask is not null && string.Equals(_activeTask.TaskId, snapshot.Handle.TaskId, StringComparison.Ordinal) && snapshot.IsTerminal)
            {
                _activeTask = null;
            }
        }
    }
}
