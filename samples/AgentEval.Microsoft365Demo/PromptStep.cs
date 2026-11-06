// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Evidence;

namespace AgentEval.Microsoft365Demo;

/// <summary>
/// The prompt step: one turn with the <c>--prompt</c> text in a fresh context. PASS when it completed with an answer;
/// FAIL when it completed with no answer text or did not complete; NOT MEASURED when it completed with several answer
/// candidates and none selected (the provider's rule, <see cref="Microsoft365Evidence.IsAnswerMeasured(AgentEval.Core.AgentResponse)"/>),
/// when it was canceled, or when the turn cap refused it. Shows the answer when there is one.
/// </summary>
internal static class PromptStep
{
    /// <summary>Runs the prompt and records its stage row and turn.</summary>
    /// <param name="context">The run's context.</param>
    /// <param name="agent">The live agent.</param>
    /// <param name="record">Where the row and the turn go.</param>
    /// <param name="cancellationToken">Stops the turn.</param>
    /// <returns>The step's result.</returns>
    public static async Task<StageResult> RunAsync(DemoContext context, WorkIqEvaluableAgent agent, RunRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(record);
        var console = context.Console;
        var prompt = context.Settings.Prompt;
        var turns = context.Turns;
        console.Line(DemoVerbosity.Normal, "Prompt (one turn, fresh context)", Tint.Accent);
        console.Line(DemoVerbosity.Normal, "  > " + IssueText.Flatten(prompt));
        var since = turns.Count;
        agent.NextCaseId = DemoDataset.PromptCaseId;
        StageRow row;
        try
        {
            var response = await agent.InvokeAsync(prompt, cancellationToken).ConfigureAwait(false);
            var turn = turns.LatestFor(DemoDataset.PromptCaseId, since);
            record.PromptTurn = turn;
            var elapsed = turn is null ? "" : $" in {TurnRecorder.Seconds(turn.Timing.Elapsed)}";
            if (!Microsoft365Evidence.IsAnswerMeasured(response))
            {
                row = new StageRow(StageNames.Prompt, StageResult.NotMeasured, $"Completed{elapsed} with several answer candidates and none selected (the evidence keeps them all)");
            }
            else if (string.IsNullOrWhiteSpace(response.Text))
            {
                row = new StageRow(StageNames.Prompt, StageResult.Fail, $"Completed{elapsed} with no answer text ({turn?.AnswerCompleteness.ToString() ?? "no turn result"})");
            }
            else
            {
                row = new StageRow(StageNames.Prompt, StageResult.Pass, $"Completed{elapsed}: {response.Text.Length.ToString(CultureInfo.InvariantCulture)}-character answer");
                ShowAnswer(console, response.Text, turn);
            }
        }
        catch (WorkIqTurnCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            record.PromptTurn = ex.TurnResult;
            row = new StageRow(StageNames.Prompt, StageResult.NotMeasured, $"canceled: the turn ended {ex.TurnResult.Outcome}; the remote task was not canceled");
        }
        catch (WorkIqTurnNotCompletedException ex)
        {
            record.PromptTurn = ex.TurnResult;
            row = new StageRow(StageNames.Prompt, StageResult.Fail, "the turn did not complete: " + TurnRecorder.NotCompletedSummary(ex.TurnResult));
        }
        catch (WorkIqProtocolException ex)
        {
            row = new StageRow(StageNames.Prompt, StageResult.Fail, $"the agent failed before a turn result: {TurnRecorder.FailureCodes(ex.Failure)}: {IssueText.Flatten(ex.Failure.SafeMessage)}");
        }
        catch (WorkIqTurnBudgetExceededException ex)
        {
            row = new StageRow(StageNames.Prompt, StageResult.NotMeasured, "not sent: " + IssueText.Flatten(ex.Message));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            row = new StageRow(StageNames.Prompt, StageResult.NotMeasured, "canceled before the turn returned");
        }
        finally
        {
            agent.NextCaseId = null;
        }

        record.Stages.Add(row);
        console.Line(row.Result == StageResult.Fail ? DemoVerbosity.Quiet : DemoVerbosity.Normal,
            $"  {Summary.Symbol(row.Result)} {StageNames.Prompt.PadRight(22)} {row.Detail}",
            row.Result switch { StageResult.Pass => Tint.Good, StageResult.Fail => Tint.Bad, _ => Tint.Warn });
        if (record.PromptTurn is { } recorded && turns.EvidenceFileOf(recorded) is { } file)
        {
            console.Line(DemoVerbosity.Normal, "    evidence: " + file + " — " + TurnRecorder.EvidenceAvailability(recorded.Evidence), Tint.Dim);
        }

        console.Blank();
        return row.Result;
    }

    private static void ShowAnswer(DemoConsole console, string raw, WorkIqTurnResult? turn)
    {
        var display = turn?.Evidence.Answer?.DisplayText is { IsPresent: true, Value: { } shown } ? shown : raw;
        var full = console.Shows(DemoVerbosity.Detailed);
        var text = full ? display : IssueText.Truncate(display, 600);
        var answer = new StringBuilder(text.Length + 64).Append("  Answer").Append(full || display.Length <= 600 ? "" : " (first 600 characters; --verbosity detailed shows all)").Append(":\n");
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            answer.Append("    ").Append(line).Append('\n');
        }

        console.Line(DemoVerbosity.Normal, answer.ToString().TrimEnd('\n'));
    }
}
