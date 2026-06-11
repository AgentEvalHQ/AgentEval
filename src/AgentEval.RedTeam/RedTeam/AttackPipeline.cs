// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Core;
using AgentEval.RedTeam.Transforms;

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
            IncludeEvidence = _includeEvidence
        };

        var runner = new RedTeamRunner();
        return await runner.ScanAsync(agent, options, _progress, cts.Token);
    }

    /// <summary>
    /// Gets a preview of the probes that will be executed.
    /// </summary>
    /// <returns>List of all probes across all configured attacks.</returns>
    public IReadOnlyList<AttackProbe> GetProbePreview()
    {
        return _attacks
            .SelectMany(a => a.GetProbes(_intensity))
            .ToList();
    }

    /// <summary>
    /// Gets the total probe count for the current configuration.
    /// </summary>
    public int TotalProbeCount
    {
        get
        {
            var total = _attacks.Sum(a => a.GetProbes(_intensity).Count());
            if (_maxProbesPerAttack > 0)
            {
                return Math.Min(total, _attacks.Count * _maxProbesPerAttack);
            }
            return total;
        }
    }
}
