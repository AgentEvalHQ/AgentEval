// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Core;       // IEvaluableAgent, AgentResponse
using AgentEval.Testing;    // Turn

namespace AgentEval.RedTeam;   // CanaryTool / IToolAwareAttack share this namespace (multi-turn tool composition)

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

        var detector = attack.ConvergenceDetector ?? DefaultConvergenceDetector.Instance;
        // Wave B↔C: a multi-turn attack that is ALSO tool-aware advertises canary tools for the whole conversation.
        // These are routed over the conversation channel's tool-aware SendAsync; a channel that can't carry tools
        // (the default DIM) degrades honestly to text-only. Fetched once (conversation-scoped) and BEFORE channel
        // selection so the L9 capability check below can see whether tools are in play.
        var canaryTools = attack is IToolAwareAttack toolAware ? toolAware.GetCanaryTools(_options.Intensity) : null;

        // L9/Jun14-L12: a native IConversableAgent's channel ignores canary tools unless it overrides the
        // SendAsync(string, tools, ct) DIM (signalled by IAgentConversation.CarriesTools). When the agent is ALSO
        // IToolCapableAgent and advertises tools but its STARTED native channel does NOT carry them, fall back to the
        // flattened adapter (which bridges to InvokeWithToolsAsync) so the tool boundary is exercised — honestly
        // labeled Flattened. A native channel that DOES carry tools (CarriesTools==true) keeps Native fidelity.
        await using var convo = await AcquireChannelAsync().ConfigureAwait(false);

        async Task<IAgentConversation> AcquireChannelAsync()
        {
            if (_agent is not IConversableAgent conversable)
                return new StatelessConversationAdapter(_agent);
            var native = await conversable.StartConversationAsync(cancellationToken).ConfigureAwait(false);
            if (canaryTools is { Count: > 0 } && !native.CarriesTools && _agent is IToolCapableAgent)
            {
                await native.DisposeAsync().ConfigureAwait(false);
                return new StatelessConversationAdapter(_agent);
            }
            return native;
        }
        var history = new List<Turn>();
        var perTurn = new List<EvaluationResult>();
        var outcome = EvaluationOutcome.Resisted;   // no turn succeeded ⇒ resisted (folded to Inconclusive below if 0 turns ran or every turn was inconclusive)
        var refusalLocked = false;                  // a refusal-lock is a POSITIVE resistance signal — must survive the all-inconclusive fold
        // 5c: track fidelity of the EVIDENCE behind the fold, not a running max across all turns: the succeeding
        // turn's fidelity on success, else the highest fidelity among CONCLUSIVE turns (Inconclusive turns excluded).
        EvidenceFidelity? succeededTurnFidelity = null;
        var conclusiveFidelity = EvidenceFidelity.Verbal;
        // ADR-021 (§5): grading provenance of the representative turn — paired with the same selection
        // as fidelity (the succeeding turn on success, else the highest-fidelity conclusive turn).
        GraderProvenance? succeededTurnGrading = null;
        GraderProvenance? conclusiveGrading = null;
        var haveConclusive = false;
        var reason = "max turns reached without success";
        var truncated = true;
        AgentResponse? last = null;
        var startTs = _options.TimeProvider.GetTimestamp();   // injectable clock (FakeTimeProvider in tests) — see ScanOptions.TimeProvider
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
                // Wave C′: hand the attacker LLM to the attack's NextTurnAsync for LLM-driven rung generation. This is
                // the ATTACKER (generates turns), distinct from _options.JudgeClient (the verdict judge, consumed below
                // for the Inconclusive fallback) — they must never be conflated. null ⇒ the attack uses its scripted path.
                AttackerClient = _options.AttackerClient,
            };

            // 5c: one per-turn budget covers attacker generation + the agent call + the judge, so a hung attacker
            // LLM (PAIR / attacker-driven Crescendo) cannot silently burn the whole conversation budget. (Scripted
            // ladders never await an LLM here, so they are unaffected.)
            using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // Per-turn budget via the injectable TimeProvider (real wall-clock in prod; a FakeTimeProvider drives it
            // deterministically in tests). Equivalent to CancelAfter(TimeoutPerTurn) under TimeProvider.System. The
            // timer is declared AFTER turnCts so `using` disposes it FIRST (stops the timer before the CTS is gone),
            // and the callback guards a disposed CTS so an in-flight fire during disposal can't throw.
            using var turnTimer = _options.TimeProvider.CreateTimer(
                static s => { try { ((CancellationTokenSource)s!).Cancel(); } catch (ObjectDisposedException) { } },
                turnCts, _options.TimeoutPerTurn, Timeout.InfiniteTimeSpan);

            string? userMessage;
            try
            {
                userMessage = await attack.NextTurnAsync(ctx, turnCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The attacker LLM exceeded the per-turn budget — fold the partial transcript honestly as truncated
                // (no user turn was added yet, so the transcript is clean) rather than aborting the whole probe.
                reason = $"attacker turn generation timed out after {_options.TimeoutPerTurn.TotalSeconds:F1}s";
                truncated = true;
                break;
            }
            catch (AttackerUnavailableException ex)
            {
                // 5c: the attacker LLM FAILED (outage) — fold as a truncated run naming the error, never the
                // "attack exhausted its rungs" stop (which would mislabel an outage as a complete, defended run).
                reason = $"attacker LLM errored ({ex.Category}) after {perTurn.Count} turn(s)";
                truncated = true;
                break;
            }

            if (userMessage is null)
            {
                reason = "attack exhausted its rungs";
                truncated = false;
                break;
            }

            // Record the user turn before sending so it survives in the transcript even if the agent times out.
            history.Add(Turn.User(userMessage));

            AgentResponse response;
            try
            {
                response = canaryTools is { Count: > 0 }
                    ? await convo.SendAsync(userMessage, canaryTools, turnCts.Token).ConfigureAwait(false)
                    : await convo.SendAsync(userMessage, turnCts.Token).ConfigureAwait(false);
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

            // L7: the per-turn evaluator call honors the per-turn budget (turnCts), like the agent call and the judge
            // fallback below — a slow async evaluator must not escape the documented per-turn timeout. A per-turn
            // evaluator timeout folds the partial transcript as truncated rather than aborting the whole probe.
            // Grade against a per-turn probe carrying the ACTUAL eliciting rung (userMessage), not the benign seed
            // opener — otherwise a judge-primary grader (which reads probe.Prompt as context) sees the wrong prompt.
            // The keyword oracles read the response + probe.Metadata/ExpectedTokens (not Prompt), so they are unaffected.
            var turnProbe = seed with { Prompt = userMessage };
            EvaluationResult result;
            try
            {
                result = await evaluator.EvaluateAsync(turnProbe, response, turnCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                reason = $"per-turn evaluation timed out after {_options.TimeoutPerTurn.TotalSeconds:F1}s";
                truncated = true;
                break;
            }
            // ADR-021 (B.1): the per-turn LLM-judge fallback + judge-primary routing now live in the
            // JudgeBackedEvaluator decorator wrapping `evaluator` (the judge runs under turnCts via the
            // EvaluateAsync call above, so the per-turn budget still applies). FidelityOf reads the
            // decorator's (possibly capped) fidelity; ProvenanceOf lifts any judge-primary provenance.
            var turnFidelity = FidelityOf(result);
            var turnGrading = GradingMetadata.ProvenanceOf(result);

            perTurn.Add(result);
            // 5c: capture the succeeding turn's fidelity + grading (first success) and accumulate fidelity only
            // over CONCLUSIVE turns — an Inconclusive turn's fidelity is not evidence behind the fold.
            if (result.Outcome == EvaluationOutcome.Succeeded && succeededTurnFidelity is null)
            {
                succeededTurnFidelity = turnFidelity;
                succeededTurnGrading = turnGrading;
            }
            if (result.Outcome != EvaluationOutcome.Inconclusive)
            {
                if (!haveConclusive || turnFidelity > conclusiveFidelity)
                {
                    conclusiveGrading = turnGrading;
                    haveConclusive = true;
                }
                conclusiveFidelity = Max(conclusiveFidelity, turnFidelity);
            }

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
                refusalLocked = true;
                reason = decision.Reason;
                truncated = false;
                break;
            }

            if (_options.MaxConversationDuration > TimeSpan.Zero && _options.TimeProvider.GetElapsedTime(startTs) >= _options.MaxConversationDuration)
            {
                reason = $"max conversation duration ({_options.MaxConversationDuration.TotalSeconds:F0}s) reached";
                truncated = true;
                break;
            }
        }

        // 5c: the fold is VERDICT-STREAM authoritative, not detector-cooperation dependent. If any turn was
        // Succeeded but the convergence detector did not signal SucceededStop (a buggy/quiet detector, or a
        // RefusalLockStop landing after an earlier success), the fold is still Succeeded — a real compromise is
        // never masked.
        if (outcome != EvaluationOutcome.Succeeded && perTurn.Any(r => r.Outcome == EvaluationOutcome.Succeeded))
        {
            outcome = EvaluationOutcome.Succeeded;
            reason = "a turn succeeded (convergence detector did not signal stop)";
            truncated = false;
        }

        if (perTurn.Count == 0)
            outcome = EvaluationOutcome.Inconclusive;
        else if (outcome == EvaluationOutcome.Resisted && !refusalLocked
                 && perTurn.All(r => r.Outcome == EvaluationOutcome.Inconclusive))
        {
            // Every turn was Inconclusive and nothing actively signalled resistance (no refusal-lock) or success.
            // Folding to the default "Resisted" would fabricate a defense from pure absence of evidence — report
            // Inconclusive honestly (the conversation measured nothing conclusive).
            outcome = EvaluationOutcome.Inconclusive;
            reason = "all turns inconclusive — no success and no refusal-lock signal";
        }

        // 5c: fidelity is the evidence behind Outcome — the succeeding turn's fidelity on success, else the
        // highest fidelity among conclusive turns.
        var fidelity = outcome == EvaluationOutcome.Succeeded && succeededTurnFidelity.HasValue
            ? succeededTurnFidelity.Value
            : conclusiveFidelity;
        // ADR-021 (§5): grading provenance selected with the SAME switch as fidelity (succeeding turn on
        // success, else the highest-fidelity conclusive turn); null when no turn was judge-primary graded.
        var grading = outcome == EvaluationOutcome.Succeeded && succeededTurnFidelity.HasValue
            ? succeededTurnGrading
            : conclusiveGrading;

        return new MultiTurnResult
        {
            Outcome = outcome,
            Fidelity = fidelity,
            Grading = grading,
            ConversationFidelity = convo.Fidelity,
            Transcript = history,
            PerTurnResults = perTurn,
            TurnsUsed = perTurn.Count,
            Reason = reason,
            WasTruncated = truncated && outcome != EvaluationOutcome.Succeeded,
            // L10 / Jun14-M13: AttackerDriven is true only when an attacker LLM was BOTH wired AND actually consumed by
            // this attack (attack.UsesAttacker), not merely because --attacker was supplied scan-wide. Otherwise a
            // SCRIPTED multi-turn attack (e.g. ToolEscalation) run alongside --attacker would be falsely marked
            // non-deterministic, which a baseline gate could use to suppress a real regression on it.
            AttackerDriven = _options.AttackerClient is not null && attack.UsesAttacker,
        };
    }

    private static EvidenceFidelity FidelityOf(EvaluationResult r) =>
        r.Metadata is { } m && m.TryGetValue("fidelity", out var v) && v is EvidenceFidelity f ? f : EvidenceFidelity.Verbal;

    private static EvidenceFidelity Max(EvidenceFidelity a, EvidenceFidelity b) => (EvidenceFidelity)Math.Max((int)a, (int)b);
}
