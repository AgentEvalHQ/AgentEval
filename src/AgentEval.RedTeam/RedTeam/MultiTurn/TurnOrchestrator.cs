// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Diagnostics;
using AgentEval.Core;       // IEvaluableAgent, AgentResponse
using AgentEval.Testing;    // Turn

namespace AgentEval.RedTeam;

/// <summary>
/// Drives an <see cref="IMultiTurnAttack"/> against an agent as a conversation (Wave C, Pillar 2), evaluating per
/// turn and folding to one verdict. Uses a Native channel when the agent is <see cref="IConversableAgent"/>, else a
/// flattened-transcript channel (<see cref="ConversationFidelity.Flattened"/>, honestly labeled). Deterministic given
/// deterministic inputs: no RNG or wall-clock control flow except the opt-in <see cref="ScanOptions.MaxConversationDuration"/>.
/// </summary>
public sealed class TurnOrchestrator
{
    private readonly IEvaluableAgent _agent;
    private readonly ScanOptions _options;

    /// <summary>Creates an orchestrator for <paramref name="agent"/> under <paramref name="options"/>.</summary>
    public TurnOrchestrator(IEvaluableAgent agent, ScanOptions options)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Runs <paramref name="attack"/> against the agent for one <paramref name="seed"/>, folding the
    /// conversation into a single <see cref="MultiTurnResult"/>.</summary>
    public async Task<MultiTurnResult> RunAsync(IMultiTurnAttack attack, AttackProbe seed, IProbeEvaluator evaluator, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attack);
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(evaluator);

        await using var convo = _agent is IConversableAgent conversable
            ? await conversable.StartConversationAsync(cancellationToken).ConfigureAwait(false)
            : new StatelessConversationAdapter(_agent);

        var detector = attack.ConvergenceDetector ?? DefaultConvergenceDetector.Instance;
        var history = new List<Turn>();
        var perTurn = new List<EvaluationResult>();
        var outcome = EvaluationOutcome.Resisted;   // no turn succeeded ⇒ resisted (only flips to Inconclusive if 0 turns ran)
        var fidelity = EvidenceFidelity.Verbal;
        var reason = "max turns reached without success";
        var truncated = true;
        AgentResponse? last = null;
        var sw = Stopwatch.StartNew();
        var maxTurns = Math.Max(1, attack.MaxTurns);

        for (var i = 0; i < maxTurns; i++)
        {
            var ctx = new MultiTurnContext
            {
                Seed = seed,
                History = history,
                TurnIndex = i,
                LastResponse = last,
                Fidelity = convo.Fidelity,
                JudgeClient = _options.JudgeClient,
            };

            var userMessage = await attack.NextTurnAsync(ctx, cancellationToken).ConfigureAwait(false);
            if (userMessage is null)
            {
                reason = "attack exhausted its rungs";
                truncated = false;
                break;
            }

            // Record the user turn before sending so it survives in the transcript even if the agent times out.
            history.Add(Turn.User(userMessage));

            using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            turnCts.CancelAfter(_options.TimeoutPerTurn);
            AgentResponse response;
            try
            {
                response = await convo.SendAsync(userMessage, turnCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A per-turn timeout (TimeoutPerTurn) — NOT a scan-cancel or conversation-budget overrun (those fire
                // the passed token and propagate to the runner). End the conversation and fold the partial transcript
                // honestly as truncated rather than aborting the whole probe.
                reason = $"turn timed out after {_options.TimeoutPerTurn.TotalSeconds:F1}s";
                truncated = true;
                break;
            }
            last = response;
            history.Add(Turn.Assistant(response.Text));

            var result = await evaluator.EvaluateAsync(seed, response, cancellationToken).ConfigureAwait(false);
            perTurn.Add(result);
            fidelity = Max(fidelity, FidelityOf(result));

            var decision = detector.Evaluate(ctx with { History = history, LastResponse = response }, response, result);
            if (decision.Signal == ConvergenceSignal.SucceededStop)
            {
                outcome = EvaluationOutcome.Succeeded;
                reason = decision.Reason;
                truncated = false;
                break;
            }
            if (decision.Signal == ConvergenceSignal.RefusalLockStop)
            {
                outcome = EvaluationOutcome.Resisted;
                reason = decision.Reason;
                truncated = false;
                break;
            }

            if (_options.MaxConversationDuration > TimeSpan.Zero && sw.Elapsed >= _options.MaxConversationDuration)
            {
                reason = $"max conversation duration ({_options.MaxConversationDuration.TotalSeconds:F0}s) reached";
                truncated = true;
                break;
            }
        }

        if (perTurn.Count == 0)
            outcome = EvaluationOutcome.Inconclusive;

        return new MultiTurnResult
        {
            Outcome = outcome,
            Fidelity = fidelity,
            ConversationFidelity = convo.Fidelity,
            Transcript = history,
            PerTurnResults = perTurn,
            TurnsUsed = perTurn.Count,
            Reason = reason,
            WasTruncated = truncated && outcome != EvaluationOutcome.Succeeded,
        };
    }

    private static EvidenceFidelity FidelityOf(EvaluationResult r) =>
        r.Metadata is { } m && m.TryGetValue("fidelity", out var v) && v is EvidenceFidelity f ? f : EvidenceFidelity.Verbal;

    private static EvidenceFidelity Max(EvidenceFidelity a, EvidenceFidelity b) => (EvidenceFidelity)Math.Max((int)a, (int)b);
}
