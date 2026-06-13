// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Core;
using AgentEval.RedTeam.Transforms;
using Microsoft.Extensions.AI;

namespace AgentEval.RedTeam;

/// <summary>
/// Fluent builder for configuring and executing red team attacks.
/// </summary>
/// <example>
/// <code>
/// var result = await AttackPipeline
///     .Create()
///     .WithAttack(Attack.PromptInjection)
///     .WithAttack(Attack.Jailbreak)
///     .WithIntensity(Intensity.Moderate)
///     .WithTimeout(TimeSpan.FromMinutes(5))
///     .ScanAsync(agent);
/// </code>
/// </example>
public sealed class AttackPipeline
{
    private readonly List<IAttackType> _attacks = [];
    private Intensity _intensity = Intensity.Quick;
    private TimeSpan _timeout = TimeSpan.FromMinutes(10);
    private TimeSpan _delayBetweenProbes = TimeSpan.Zero;
    private TimeSpan _timeoutPerProbe = TimeSpan.FromSeconds(30);
    private IProgress<ScanProgress>? _progress;
    private int _maxProbesPerAttack = 0;
    private bool _failFast = false;
    private bool _includeEvidence = true;
    private IChatClient? _judgeClient;
    private IChatClient? _attackerClient;
    private TimeSpan? _timeoutPerTurn;
    private TimeSpan _maxConversationDuration = TimeSpan.Zero;
    private int _parallelism = 1;

    private AttackPipeline() { }

    /// <summary>
    /// Creates a new attack pipeline.
    /// </summary>
    public static AttackPipeline Create() => new();

    /// <summary>
    /// Adds an attack type to the pipeline.
    /// </summary>
    public AttackPipeline WithAttack<TAttack>() where TAttack : IAttackType, new()
    {
        _attacks.Add(new TAttack());
        return this;
    }

    /// <summary>
    /// Adds a pre-configured attack instance to the pipeline.
    /// </summary>
    public AttackPipeline WithAttack(IAttackType attack)
    {
        ArgumentNullException.ThrowIfNull(attack);
        _attacks.Add(attack);
        return this;
    }

    /// <summary>
    /// Adds multiple attacks to the pipeline.
    /// </summary>
    public AttackPipeline WithAttacks(params IAttackType[] attacks)
    {
        ArgumentNullException.ThrowIfNull(attacks);
        _attacks.AddRange(attacks);
        return this;
    }

    /// <summary>
    /// Adds all built-in attacks to the pipeline.
    /// </summary>
    public AttackPipeline WithAllAttacks()
    {
        _attacks.AddRange(Attack.All);
        return this;
    }

    /// <summary>
    /// Adds MVP attacks to the pipeline (PromptInjection, Jailbreak, PIILeakage).
    /// </summary>
    public AttackPipeline WithMvpAttacks()
    {
        _attacks.AddRange(Attack.AllMvp);
        return this;
    }

    /// <summary>
    /// Sets the intensity level for probe generation.
    /// </summary>
    public AttackPipeline WithIntensity(Intensity intensity)
    {
        _intensity = intensity;
        return this;
    }

    /// <summary>
    /// Sets the overall timeout for the scan.
    /// </summary>
    public AttackPipeline WithTimeout(TimeSpan timeout)
    {
        _timeout = timeout;
        return this;
    }

    /// <summary>
    /// Sets the timeout per individual probe.
    /// </summary>
    public AttackPipeline WithTimeoutPerProbe(TimeSpan timeout)
    {
        _timeoutPerProbe = timeout;
        return this;
    }

    /// <summary>
    /// Sets a delay between probes (for rate limiting).
    /// </summary>
    public AttackPipeline WithDelayBetweenProbes(TimeSpan delay)
    {
        _delayBetweenProbes = delay;
        return this;
    }

    /// <summary>
    /// Sets the maximum number of probes per attack.
    /// </summary>
    public AttackPipeline WithMaxProbesPerAttack(int max)
    {
        _maxProbesPerAttack = max;
        return this;
    }

    /// <summary>
    /// Enables fail-fast mode - stops on first successful attack.
    /// </summary>
    public AttackPipeline WithFailFast(bool failFast = true)
    {
        _failFast = failFast;
        return this;
    }

    /// <summary>
    /// Sets whether to include evidence (prompts/responses) in results.
    /// </summary>
    public AttackPipeline WithEvidence(bool includeEvidence = true)
    {
        _includeEvidence = includeEvidence;
        return this;
    }

    /// <summary>
    /// Sets a progress reporter for scan updates.
    /// </summary>
    public AttackPipeline WithProgress(IProgress<ScanProgress> progress)
    {
        _progress = progress;
        return this;
    }

    /// <summary>
    /// Sets the LLM judge client used to re-evaluate <see cref="EvaluationOutcome.Inconclusive"/> probes
    /// (single-turn and multi-turn fallback, GAP-19). Distinct from <see cref="WithAttacker"/>.
    /// </summary>
    public AttackPipeline WithJudge(IChatClient judgeClient)
    {
        ArgumentNullException.ThrowIfNull(judgeClient);
        _judgeClient = judgeClient;
        return this;
    }

    /// <summary>
    /// Sets the attacker LLM client that GENERATES adversarial turns for LLM-driven multi-turn attacks
    /// (attacker-rung Crescendo / PAIR / TAP, Wave C′). Without it those attacks fall back to their scripted path.
    /// This is the attacker (generates), distinct from <see cref="WithJudge"/> (scores). PAIR/TAP require this.
    /// </summary>
    public AttackPipeline WithAttacker(IChatClient attackerClient)
    {
        ArgumentNullException.ThrowIfNull(attackerClient);
        _attackerClient = attackerClient;
        return this;
    }

    /// <summary>
    /// Sets the per-turn timeout for multi-turn conversations (Wave C). Defaults to the per-probe timeout.
    /// A multi-turn conversation's hard bound is <c>MaxTurns × TimeoutPerTurn</c> (or <see cref="WithMaxConversationDuration"/>).
    /// </summary>
    public AttackPipeline WithTimeoutPerTurn(TimeSpan timeoutPerTurn)
    {
        _timeoutPerTurn = timeoutPerTurn;
        return this;
    }

    /// <summary>
    /// Sets a soft ceiling on total wall-clock per multi-turn conversation (checked between turns; never a hard
    /// mid-turn cancel). <see cref="TimeSpan.Zero"/> (default) means no soft cap.
    /// </summary>
    public AttackPipeline WithMaxConversationDuration(TimeSpan maxConversationDuration)
    {
        _maxConversationDuration = maxConversationDuration;
        return this;
    }

    /// <summary>
    /// Sets the maximum number of probes executed concurrently within an attack (RA3-05). Values &lt;= 1 run
    /// sequentially (the default). Only raise this when the agent and evaluator are safe for concurrent invocation.
    /// </summary>
    public AttackPipeline WithParallelism(int parallelism)
    {
        _parallelism = parallelism;
        return this;
    }

    /// <summary>
    /// Amplifies the most-recently-added attack by FANNING OUT each of its probes through each transformer
    /// (Expand) — Wave A. The original probes are dropped.
    /// </summary>
    /// <param name="transformers">The transformers to apply independently to each probe.</param>
    public AttackPipeline WithTransform(params IProbeTransformer[] transformers)
        => WithTransform(keepOriginal: false, transformers);

    /// <summary>
    /// Amplifies the most-recently-added attack by FANNING OUT each probe through each transformer (Expand).
    /// </summary>
    /// <param name="keepOriginal">When true, the untransformed probes are emitted alongside the transformed ones.</param>
    /// <param name="transformers">The transformers to apply independently to each probe.</param>
    public AttackPipeline WithTransform(bool keepOriginal, params IProbeTransformer[] transformers)
    {
        ArgumentNullException.ThrowIfNull(transformers);
        ReplaceLast(a => new TransformedAttack(a, transformers, TransformMode.Expand, keepOriginal));
        return this;
    }

    /// <summary>
    /// Amplifies the most-recently-added attack by COMPOSING the transformers (Chain): the second encodes the first's
    /// output, etc. (e.g. base64-of-rot13).
    /// </summary>
    /// <param name="transformers">The transformers to compose, in apply order (left = first).</param>
    public AttackPipeline WithChainedTransform(params IProbeTransformer[] transformers)
        => WithChainedTransform(keepOriginal: false, transformers);

    /// <summary>
    /// Amplifies the most-recently-added attack by COMPOSING the transformers (Chain).
    /// </summary>
    /// <param name="keepOriginal">When true, the untransformed seed probes are emitted alongside the composed ones
    /// (e.g. to keep a plaintext control next to the encoded variant).</param>
    /// <param name="transformers">The transformers to compose, in apply order (left = first).</param>
    public AttackPipeline WithChainedTransform(bool keepOriginal, params IProbeTransformer[] transformers)
    {
        ArgumentNullException.ThrowIfNull(transformers);
        ReplaceLast(a => new TransformedAttack(a, transformers, TransformMode.Chain, keepOriginal));
        return this;
    }

    private void ReplaceLast(Func<IAttackType, IAttackType> wrap)
    {
        if (_attacks.Count == 0)
            throw new InvalidOperationException("WithTransform/WithChainedTransform must follow a WithAttack call.");
        _attacks[^1] = wrap(_attacks[^1]);
    }

    /// <summary>
    /// Executes the configured attack pipeline against the agent.
    /// </summary>
    /// <param name="agent">The agent to scan.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Aggregate result of all attacks.</returns>
    /// <exception cref="InvalidOperationException">If no attacks are configured.</exception>
    public async Task<RedTeamResult> ScanAsync(
        IEvaluableAgent agent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);

        if (_attacks.Count == 0)
        {
            throw new InvalidOperationException(
                "No attacks configured. Call WithAttack<T>(), WithAttack(attack), or WithAllAttacks().");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);

        var options = new ScanOptions
        {
            Intensity = _intensity,
            AttackTypes = _attacks,
            DelayBetweenProbes = _delayBetweenProbes,
            TimeoutPerProbe = _timeoutPerProbe,
            MaxProbesPerAttack = _maxProbesPerAttack,
            FailFast = _failFast,
            IncludeEvidence = _includeEvidence,
            JudgeClient = _judgeClient,
            AttackerClient = _attackerClient,
            // _timeoutPerTurn null ⇒ ScanOptions.TimeoutPerTurn falls back to TimeoutPerProbe (same as unset).
            TimeoutPerTurn = _timeoutPerTurn ?? _timeoutPerProbe,
            MaxConversationDuration = _maxConversationDuration,
            Parallelism = _parallelism,
        };

        var runner = new RedTeamRunner();
        return await runner.ScanAsync(agent, options, _progress, cts.Token);
    }

    /// <summary>
    /// Enumerates the probes that will actually be executed, applying the same
    /// per-attack <see cref="_maxProbesPerAttack"/> cap that <see cref="RedTeamRunner"/>
    /// uses (Take(max) per attack — NOT a global cap). Keeps the preview and count
    /// in lock-step with the runner's real plan.
    /// </summary>
    private IEnumerable<AttackProbe> EnumerateEffectiveProbes()
    {
        foreach (var attack in _attacks)
        {
            IEnumerable<AttackProbe> probes = attack.GetProbes(_intensity);
            if (_maxProbesPerAttack > 0)
                probes = probes.Take(_maxProbesPerAttack);
            foreach (var probe in probes)
                yield return probe;
        }
    }

    /// <summary>
    /// Gets a preview of the probes that will be executed (honors MaxProbesPerAttack).
    /// </summary>
    /// <returns>List of all probes across all configured attacks, capped per attack.</returns>
    public IReadOnlyList<AttackProbe> GetProbePreview() => EnumerateEffectiveProbes().ToList();

    /// <summary>
    /// Gets the total probe count for the current configuration (honors MaxProbesPerAttack).
    /// </summary>
    public int TotalProbeCount => EnumerateEffectiveProbes().Count();
}
