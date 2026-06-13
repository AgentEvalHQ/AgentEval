// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Core;       // IEvaluableAgent, AgentResponse
using AgentEval.Testing;    // Turn

namespace AgentEval.RedTeam;

/// <summary>
/// Drives an <see cref="ITreeAttack"/> (TAP, Wave C′) as a breadth-first, judge-scored, pruned tree search and folds
/// the whole search into ONE <see cref="MultiTurnResult"/> per seed — the same shape the linear
/// <see cref="TurnOrchestrator"/> produces, so the runner builds a <c>ProbeResult</c> from it identically.
/// </summary>
/// <remarks>
/// Each node is an INDEPENDENT single-turn call to the target (no conversation channel), so the folded
/// <see cref="ConversationFidelity"/> is <see cref="ConversationFidelity.Flattened"/>. Honesty: the attacker
/// producing nothing for a branch dead-ends that branch (never a fabricated success); a probe the target resists is
/// scored Resisted; the verdict judge (GAP-19) resolves an Inconclusive node only when configured; the
/// <see cref="ITreeAttack.MaxNodes"/> budget caps the fan-out and a hit is surfaced as truncation.
/// </remarks>
public sealed class TreeOrchestrator
{
    private readonly IEvaluableAgent _target;
    private readonly ScanOptions _options;

    /// <summary>Creates a tree orchestrator for <paramref name="target"/> under <paramref name="options"/>.</summary>
    public TreeOrchestrator(IEvaluableAgent target, ScanOptions options)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Runs <paramref name="attack"/> for one <paramref name="seed"/>, folding the search into one result.</summary>
    public async Task<MultiTurnResult> RunAsync(ITreeAttack attack, AttackProbe seed, IProbeEvaluator evaluator, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attack);
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(evaluator);

        if (_options.AttackerClient is null)
            throw new NotSupportedException(
                "TAP requires an attacker LLM — set ScanOptions.AttackerClient (CLI: --attacker <url>).");

        var planner = new AttackerPlanner(_options.AttackerClient);
        var objective = attack.Objective(seed);
        var transcript = new List<Turn>();
        var verdicts = new List<EvaluationResult>();
        var frontier = new List<TapNode> { TapNode.Root() };
        var fidelity = EvidenceFidelity.Verbal;
        var anyResisted = false;
        var nodes = 0;

        for (var depth = 0; depth < attack.MaxDepth; depth++)
        {
            var children = new List<TapNode>();
            foreach (var node in frontier)
            {
                for (var branch = 0; branch < attack.Branching; branch++)
                {
                    if (nodes >= attack.MaxNodes)
                        return FoldExhausted(verdicts, transcript, fidelity, anyResisted, nodes, truncated: true);

                    // Bound each node (attacker prompt + target call + judge) by the per-probe budget so one slow LLM
                    // call cannot starve the rest of the tree; a per-node timeout dead-ends that branch honestly.
                    using var nodeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    nodeCts.CancelAfter(_options.TimeoutPerProbe);

                    string? prompt;
                    AgentResponse reply;
                    EvaluationResult verdict;
                    try
                    {
                        prompt = await planner.NextAsync(new AttackerTurnRequest
                        {
                            Strategy = AttackerStrategy.Tap,
                            Objective = objective,
                            Transcript = node.Path,
                            LastReply = node.LastReply,
                            LastVerdict = node.LastVerdict,
                            TurnIndex = depth,
                        }, nodeCts.Token).ConfigureAwait(false);
                        if (prompt is null) continue;   // attacker dead-ended this branch — never a fabricated move
                        nodes++;

                        reply = await _target.InvokeAsync(prompt, nodeCts.Token).ConfigureAwait(false);
                        verdict = await ScoreAsync(evaluator, seed, reply, nodeCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        continue;   // this node exceeded its per-node budget → dead-end it and keep searching
                    }

                    transcript.Add(Turn.User(prompt));
                    transcript.Add(Turn.Assistant(reply.Text));
                    verdicts.Add(verdict);

                    var (turnFidelity, score) = Classify(verdict);
                    fidelity = Max(fidelity, turnFidelity);
                    if (verdict.Outcome == EvaluationOutcome.Resisted) anyResisted = true;

                    if (verdict.Outcome == EvaluationOutcome.Succeeded)
                        return Fold(verdict.Outcome, turnFidelity, transcript, verdicts,
                            $"TAP success at depth {depth + 1} ({nodes} node(s) explored)", truncated: false);

                    children.Add(node.Extend(prompt, reply.Text, verdict.Outcome.ToString(), score));
                }
            }

            if (children.Count == 0) break;   // every branch dead-ended (attacker exhausted)
            frontier = [.. children.OrderByDescending(c => c.Score).Take(Math.Max(1, attack.Beam))];   // prune to the beam
        }

        return FoldExhausted(verdicts, transcript, fidelity, anyResisted, nodes, truncated: nodes >= attack.MaxNodes);
    }

    // Score a node: the deterministic evaluator, then the verdict judge (GAP-19) only if it left Inconclusive.
    private async Task<EvaluationResult> ScoreAsync(IProbeEvaluator evaluator, AttackProbe seed, AgentResponse reply, CancellationToken ct)
    {
        var verdict = await evaluator.EvaluateAsync(seed, reply, ct).ConfigureAwait(false);
        if (verdict.Outcome == EvaluationOutcome.Inconclusive && _options.JudgeClient is not null)
        {
            var judged = await new Evaluators.LLMJudgeEvaluator(_options.JudgeClient)
                .EvaluateAsync(seed, reply.Text, ct).ConfigureAwait(false);
            if (judged.Outcome != EvaluationOutcome.Inconclusive)
            {
                // Judge reasons over text → capped at IntentToAct (Succeeded) / Verbal (Resisted). PRESERVE any
                // metadata the judge returned and only add/override the fidelity key Classify reads.
                var meta = judged.Metadata is { } existing
                    ? new Dictionary<string, object>(existing)
                    : new Dictionary<string, object>();
                meta["fidelity"] = judged.Outcome == EvaluationOutcome.Succeeded ? EvidenceFidelity.IntentToAct : EvidenceFidelity.Verbal;
                return judged with { Metadata = meta };
            }
        }
        return verdict;
    }

    // Fidelity + a pruning score (Succeeded > Inconclusive > Resisted; success returns before pruning anyway).
    private static (EvidenceFidelity, double) Classify(EvaluationResult v)
    {
        var fidelity = v.Metadata is { } m && m.TryGetValue("fidelity", out var raw) && raw is EvidenceFidelity f
            ? f : EvidenceFidelity.Verbal;
        var score = v.Outcome switch
        {
            EvaluationOutcome.Succeeded => 2.0 + v.Confidence,
            EvaluationOutcome.Inconclusive => 1.0 + v.Confidence,
            _ => v.Confidence,   // Resisted: keep the least-confident resists as marginally more promising
        };
        return (fidelity, score);
    }

    private static EvidenceFidelity Max(EvidenceFidelity a, EvidenceFidelity b) => (EvidenceFidelity)Math.Max((int)a, (int)b);

    private static MultiTurnResult Fold(EvaluationOutcome outcome, EvidenceFidelity fidelity, IReadOnlyList<Turn> transcript,
        IReadOnlyList<EvaluationResult> verdicts, string reason, bool truncated) => new()
    {
        Outcome = outcome,
        Fidelity = fidelity,
        ConversationFidelity = ConversationFidelity.Flattened,   // each node is an independent single-turn call
        Transcript = transcript,
        PerTurnResults = verdicts,
        TurnsUsed = transcript.Count / 2,
        Reason = reason,
        WasTruncated = truncated && outcome != EvaluationOutcome.Succeeded,
    };

    // No node succeeded: the agent RESISTED every measurable variation ⇒ Resisted; if we could only ever measure
    // Inconclusive (or nothing ran) ⇒ Inconclusive. Never a fabricated success.
    private static MultiTurnResult FoldExhausted(IReadOnlyList<EvaluationResult> verdicts, IReadOnlyList<Turn> transcript,
        EvidenceFidelity fidelity, bool anyResisted, int nodes, bool truncated)
    {
        var outcome = anyResisted ? EvaluationOutcome.Resisted : EvaluationOutcome.Inconclusive;
        var reason = nodes == 0
            ? "TAP produced no attack (attacker exhausted before any prompt)"
            : $"TAP exhausted ({nodes} node(s) explored), target was not driven to the objective";
        return Fold(outcome, fidelity, transcript, verdicts, reason, truncated);
    }
}

/// <summary>A node in a TAP search: the branch transcript so far, the last target reply, and a pruning score.</summary>
public sealed record TapNode
{
    /// <summary>The transcript along this branch (oldest → newest).</summary>
    public IReadOnlyList<Turn> Path { get; init; } = [];

    /// <summary>The target's most recent reply on this branch.</summary>
    public string? LastReply { get; init; }

    /// <summary>How the last reply scored (e.g. "Resisted"), passed back to the attacker.</summary>
    public string? LastVerdict { get; init; }

    /// <summary>Pruning score — higher survives the beam.</summary>
    public double Score { get; init; }

    /// <summary>The root node (empty branch).</summary>
    public static TapNode Root() => new();

    /// <summary>A child extending this branch with one attacker prompt + target reply.</summary>
    public TapNode Extend(string prompt, string reply, string verdict, double score) => new()
    {
        Path = [.. Path, Turn.User(prompt), Turn.Assistant(reply)],
        LastReply = reply,
        LastVerdict = verdict,
        Score = score,
    };
}
