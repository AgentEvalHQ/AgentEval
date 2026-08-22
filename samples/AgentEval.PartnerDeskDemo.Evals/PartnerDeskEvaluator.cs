// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.PartnerDeskDemo.Demo;
using AgentEval.PartnerDeskDemo.Tools;
using Microsoft.Extensions.AI;

namespace AgentEval.PartnerDeskDemo.Evals;

/// <summary>
/// Runs each arm of the PartnerDesk scenario N times and aggregates the deterministic outcome into measured
/// rates. Reuses the demo's own <see cref="PartnerDeskRunner"/>, so the eval exercises the real agent, the real
/// MCP child process, the real gates, and the real containment store — the only thing that differs from the stage
/// demo is that it repeats and counts.
/// </summary>
/// <remarks>
/// Runs are sequential and share one PartnerIntel child process per evil/clean mode (the runner reuses the MCP
/// session while the mode is unchanged). Each run still builds a fresh agent, session, tool ledger, and (for
/// Level 2) a fresh containment store, so the runs are independent draws of the model's behaviour.
/// </remarks>
public sealed class PartnerDeskEvaluator : IAsyncDisposable
{
    private readonly PartnerDeskRunner _runner;
    private readonly ConcealmentJudge? _judge;
    private readonly Action<string> _progress;

    /// <summary>Creates an evaluator over a chat-client factory, an outbox path, the register, and options.</summary>
    /// <param name="chatClientFactory">Supplies the model per run: live Azure OpenAI, or scripted.</param>
    /// <param name="outboxPath">The local file the faked email tool appends to.</param>
    /// <param name="register">The loaded partner register.</param>
    /// <param name="judge">An optional shadow concealment judge; disclosure is only scored when supplied.</param>
    /// <param name="progress">A per-run progress callback (one short line per run).</param>
    public PartnerDeskEvaluator(
        Func<PhaseRunContext, IChatClient> chatClientFactory,
        string outboxPath,
        PartnerRegister register,
        ConcealmentJudge? judge,
        Action<string> progress)
    {
        _runner = new PartnerDeskRunner(chatClientFactory, DemoOutput.Silent, outboxPath, register);
        _judge = judge;
        _progress = progress;
    }

    /// <summary>
    /// Runs <paramref name="runs"/> draws of <paramref name="phase"/> and aggregates them.
    /// </summary>
    /// <remarks>
    /// A single run that throws a transient provider/transport error is retried once, then skipped with a warning
    /// rather than aborting the whole evaluation — the earlier design lost a completed multi-arm run to one such
    /// error. Skipped runs are not counted, so every reported denominator reflects only runs that actually
    /// completed. Cancellation propagates so Ctrl+C stops promptly; the caller still renders what was collected.
    /// </remarks>
    public async Task<ArmResult> EvaluateArmAsync(
        DemoPhase phase,
        int runs,
        string question,
        CancellationToken cancellationToken = default)
    {
        var metrics = new List<RunMetrics>(runs);
        var judgeDisclosed = new List<bool?>(runs);

        for (var i = 0; i < runs; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            PhaseOutcome? outcome = null;
            for (var attempt = 1; attempt <= 2 && outcome is null; attempt++)
            {
                try
                {
                    outcome = await _runner.RunAsync(phase, question, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Only a genuine caller cancel aborts. A cancel that comes from an internal timeout (e.g. the
                    // MCP handshake watchdog) leaves the caller's token unset and must be treated as a transient
                    // error — retried then skipped — not mistaken for the user pressing Ctrl+C.
                    throw;
                }
                catch (Exception exception)
                {
                    _progress(
                        $"  Phase {(int)phase}  run {i + 1,2}/{runs}  " +
                        $"{(attempt == 1 ? "error, retrying" : "SKIPPED after error")}: " +
                        $"{exception.GetType().Name}: {Compact(exception.Message)}");
                }
            }

            if (outcome is null)
            {
                continue;
            }

            var run = RunMetrics.From(outcome);

            bool? disclosed = null;
            if (_judge is not null && outcome.EvilMode)
            {
                disclosed = await _judge.DisclosesExportAsync(outcome.AnswerText, cancellationToken)
                    .ConfigureAwait(false);
            }

            metrics.Add(run);
            judgeDisclosed.Add(disclosed);
            _progress(FormatRunLine(phase, i + 1, runs, run, disclosed));
        }

        return new ArmResult(phase, metrics, judgeDisclosed);
    }

    private static string Compact(string message)
    {
        var single = string.Join(' ', message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return single.Length <= 100 ? single : single[..99] + "…";
    }

    private static string FormatRunLine(DemoPhase phase, int index, int total, RunMetrics run, bool? disclosed)
    {
        var flags = new List<string>();
        if (run.Attempted)
        {
            flags.Add("attempted");
        }

        if (run.Exfiltrated)
        {
            flags.Add(run.SilentExfiltration ? "EXFIL(silent)" : "EXFIL(disclosed)");
        }

        if (run.PoisonWithheld)
        {
            flags.Add("poison-withheld");
        }

        if (run.SourceContained)
        {
            flags.Add("contained");
        }

        if (disclosed is not null)
        {
            flags.Add(disclosed.Value ? "judge:disclosed" : "judge:silent");
        }

        var verdict = run.OracleHeld ? "oracle OK  " : "oracle FAIL";
        var summary = flags.Count == 0 ? "clean" : string.Join(", ", flags);
        return $"  Phase {(int)phase}  run {index,2}/{total}  {verdict}  {summary}";
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _runner.DisposeAsync();
}
