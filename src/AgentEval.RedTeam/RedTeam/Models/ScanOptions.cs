// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using Microsoft.Extensions.AI;

namespace AgentEval.RedTeam;

/// <summary>
/// Configuration options for a red-team scan.
/// </summary>
public class ScanOptions
{
    /// <summary>
    /// Attack types to run. If null or empty, uses all built-in attacks.
    /// </summary>
    public IReadOnlyList<IAttackType>? AttackTypes { get; init; }

    /// <summary>
    /// Scan intensity - controls probe count and difficulty.
    /// Default: Moderate.
    /// </summary>
    public Intensity Intensity { get; init; } = Intensity.Moderate;

    /// <summary>
    /// Maximum probes per attack type. 0 = no limit.
    /// Default: 0 (use all probes for the intensity level).
    /// </summary>
    public int MaxProbesPerAttack { get; init; } = 0;

    /// <summary>
    /// Timeout per probe execution.
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan TimeoutPerProbe { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Delay between probes (for rate limiting).
    /// Default: Zero.
    /// </summary>
    public TimeSpan DelayBetweenProbes { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Timeout per individual agent turn within a multi-turn conversation (Wave C). Defaults to
    /// <see cref="TimeoutPerProbe"/>. A per-turn timeout ends that turn gracefully and folds the partial conversation
    /// as truncated (it does not abort the probe). Unlike a single-turn probe — which is bounded by
    /// <see cref="TimeoutPerProbe"/> — a multi-turn conversation's hard outer bound is sized for the whole
    /// conversation: <see cref="MaxConversationDuration"/> if set, otherwise <c>MaxTurns × TimeoutPerTurn</c>. (So set
    /// <see cref="TimeoutPerTurn"/>, not <see cref="TimeoutPerProbe"/>, to bound multi-turn scans.)
    /// </summary>
    public TimeSpan TimeoutPerTurn
    {
        get => _timeoutPerTurn ?? TimeoutPerProbe;
        init => _timeoutPerTurn = value;
    }
    private readonly TimeSpan? _timeoutPerTurn;

    /// <summary>
    /// Soft ceiling on total wall-clock for a single multi-turn conversation (Wave C). Enforced as an elapsed-time
    /// check <i>between</i> turns (never a hard mid-turn cancel), so it is deterministic-friendly — but a conversation
    /// can therefore overshoot this ceiling by up to one in-flight turn (worst case <c>MaxConversationDuration +
    /// TimeoutPerTurn</c>). <see cref="TimeSpan.Zero"/> (the default) means no soft cap — only <c>MaxTurns</c> and the
    /// hard <c>MaxTurns × TimeoutPerTurn</c> bound apply.
    /// </summary>
    public TimeSpan MaxConversationDuration { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Maximum number of probes executed concurrently within an attack (RA3-05). Values &lt;= 1 are
    /// clamped to 1 and run sequentially (the default — byte-for-byte the prior behavior). Higher values
    /// use a bounded <see cref="System.Threading.SemaphoreSlim"/> worker pool; results are reordered to the
    /// original probe order so reports stay deterministic regardless of completion order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Agent thread-safety (required for &gt; 1):</b> the same <c>IEvaluableAgent</c> instance is
    /// invoked from multiple worker threads concurrently. <c>IEvaluableAgent</c> declares no thread-safety
    /// contract, so an agent that mutates per-call state (e.g. an accumulating conversation history, a
    /// non-reentrant SDK client) MUST keep <c>Parallelism = 1</c>; only supply a higher value when the agent and
    /// its evaluator are safe for concurrent invocation.
    /// </para>
    /// <para>
    /// <b>FailFast:</b> stops scheduling NEW probes once a success is seen but cannot cancel probes already in
    /// flight — those complete and are recorded. A consequence is that under <c>Parallelism &gt; 1</c> the exact
    /// executed/skipped probe count (and hence <c>RedTeamResult.WasTruncated</c>) is timing-dependent. For strict,
    /// deterministic probe-level fail-fast semantics, keep <c>Parallelism = 1</c>.
    /// </para>
    /// <para>
    /// <b>Rate limiting:</b> <see cref="DelayBetweenProbes"/> is honored under parallelism as inter-dispatch
    /// spacing (the semaphore caps concurrency, the delay caps dispatch rate). Progress callbacks fire after each
    /// probe completes, so the reported "current probe" is the just-completed one rather than the next to run.
    /// </para>
    /// </remarks>
    public int Parallelism
    {
        get => _parallelism;
        init => _parallelism = value < 1 ? 1 : value;
    }
    private readonly int _parallelism = 1;

    /// <summary>
    /// Whether to stop on first failure.
    /// Default: false (run all probes).
    /// </summary>
    public bool FailFast { get; init; } = false;

    /// <summary>
    /// Whether to include prompt/response in failure details.
    /// Set to false for sensitive content.
    /// Default: true.
    /// </summary>
    public bool IncludeEvidence { get; init; } = true;

    /// <summary>
    /// Optional <see cref="IChatClient"/> for LLM-judge fallback evaluation.
    /// </summary>
    /// <remarks>
    /// RC-1 / GAP-19: when set, the runner invokes <c>LLMJudgeEvaluator</c> for any probe whose
    /// deterministic verdict is <see cref="EvaluationOutcome.Inconclusive"/>, adopting the judge's
    /// verdict when conclusive. Judge-derived "Succeeded" verdicts are recorded at
    /// <see cref="EvidenceFidelity.IntentToAct"/> fidelity (text reasoning, not a tool trace).
    /// </remarks>
    public IChatClient? JudgeClient { get; init; }

    /// <summary>
    /// Time source for multi-turn timing — the per-turn timeout (<see cref="TimeoutPerTurn"/>) and the
    /// conversation-duration clock (<see cref="MaxConversationDuration"/>) in <c>TurnOrchestrator</c>. Defaults to
    /// <see cref="System.TimeProvider.System"/> (real wall-clock); tests inject a <c>FakeTimeProvider</c> to drive
    /// those timeouts deterministically (no real waits, no load-sensitive flakes). Like <see cref="JudgeClient"/>
    /// this is a runtime object, never serialized.
    /// </summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// ADR-021 (Phase B.1) / ADR-023 (B.3 flip, 2026-06-22): how the <see cref="JudgeClient"/> participates in grading.
    /// <see cref="JudgeMode.Primary"/> (NOW the default) routes Semantic + Verbal-evidence probes to the judge — the
    /// Composite Judges (<see cref="DecomposedGraders"/>) where one exists, else the single judge — as the primary grader,
    /// with the keyword verdict advisory. <see cref="JudgeMode.Fallback"/> is the legacy Inconclusive-gated behavior.
    /// Inert without a <see cref="JudgeClient"/> — a run with no judge is byte-identical to the keyword oracle regardless
    /// of mode. (B.3: flipped Fallback→Primary after the readiness review met its gate — κ=1.000, 0 directional
    /// fabrications on the pinned gold; Composite Judges are honest-by-construction.)
    /// </summary>
    public JudgeMode Mode { get; init; } = JudgeMode.Primary;

    /// <summary>
    /// ADR-021 (Phase B.1): the judge grading rubric (a prompt variant). <see cref="JudgeRubric.EvidenceAnchored"/>
    /// (the default — R4 readiness alignment) grounds every conclusive verdict in a verbatim quote from the response,
    /// which suppresses BOTH error directions and carries the per-oracle discriminators (e.g. the Misinformation
    /// confabulation discriminator); it is the rubric the published κ/fabrication numbers were measured under and the
    /// one ADR-021 recommends. <see cref="JudgeRubric.Strict"/> is precision-oriented (the legacy default, NO
    /// discriminators); <see cref="JudgeRubric.Lenient"/> is recall-oriented. Consulted when <see cref="Mode"/> is
    /// <see cref="JudgeMode.Primary"/> (and, in Fallback, for the inconclusive-adjudication judge).
    /// </summary>
    public JudgeRubric Rubric { get; init; } = JudgeRubric.EvidenceAnchored;

    /// <summary>
    /// ADR-021 (Phase B.1): grading timeout for judge-primary grading. When set, grading runs under its own linked
    /// <c>CancellationTokenSource</c>. On the SINGLE-judge / fallback path it is a per-judge-call timeout — on timeout the
    /// decorator adopts the advisory keyword verdict. On the judge-primary COMPOSITE path (ADR-024) it bounds the TOTAL
    /// grading wall-clock via <c>TimeoutEvaluator</c> and abstains (<see cref="EvaluationOutcome.Inconclusive"/>) on
    /// timeout, since a composite has no advisory keyword verdict to adopt. Either way it never fabricates or aborts the
    /// scan. <c>null</c> = no separate grading timeout (grading shares the per-probe budget).
    /// </summary>
    public TimeSpan? JudgeTimeout { get; init; }

    /// <summary>
    /// When true and <see cref="JudgeClient"/> is set, attach an LLM-generated rationale (narrating the verdict +
    /// evidence fidelity) to Succeeded/Inconclusive findings (the <c>--explain</c> feature). Best-effort and opt-in:
    /// it adds one judge call per explained finding and never changes a verdict. Default false (no extra LLM cost).
    /// </summary>
    public bool ExplainFindings { get; init; }

    /// <summary>
    /// Optional <b>attacker</b>-LLM client for LLM-driven multi-turn attacks (Wave C′: Crescendo / PAIR / TAP).
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="JudgeClient"/> (the verdict judge): the attacker <em>generates</em> the next
    /// adversarial turn, the judge <em>scores</em> the target's reply — they must never be conflated (an attack must
    /// not score itself). When <c>null</c>, Crescendo falls back to its scripted ladder and PAIR/TAP report a clear
    /// "requires an attacker" error. An attacker-LLM run is <b>non-deterministic</b> in production (reproducible only
    /// with a deterministic attacker client, e.g. in tests).
    /// </remarks>
    public IChatClient? AttackerClient { get; init; }

    /// <summary>
    /// Optional callback invoked after each probe completes.
    /// Use this for real-time progress display. 
    /// Alternative to IProgress for simpler API.
    /// </summary>
    public Action<ScanProgress>? OnProgress { get; init; }

    /// <summary>
    /// How often to report progress. 1 = every probe, 5 = every 5th probe.
    /// Values &lt;= 0 are treated as 1 (report on every probe) to avoid a divide-by-zero
    /// in the scan loop. Default: 1.
    /// </summary>
    public int ProgressReportInterval
    {
        get => _progressReportInterval;
        init => _progressReportInterval = value < 1 ? 1 : value;
    }
    private readonly int _progressReportInterval = 1;

    /// <summary>
    /// Creates options with default values.
    /// </summary>
    public static ScanOptions Default => new();

    /// <summary>
    /// Creates options for a quick scan.
    /// </summary>
    public static ScanOptions Quick => new()
    {
        Intensity = Intensity.Quick,
        TimeoutPerProbe = TimeSpan.FromSeconds(15)
    };

    /// <summary>
    /// Creates options for a comprehensive scan.
    /// </summary>
    public static ScanOptions Comprehensive => new()
    {
        Intensity = Intensity.Comprehensive,
        TimeoutPerProbe = TimeSpan.FromSeconds(60)
    };
}
