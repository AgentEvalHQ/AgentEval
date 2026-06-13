// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam.Transforms;

/// <summary>
/// Decorator that wraps an <see cref="IAttackType"/> and rewrites its probes through one or more
/// <see cref="IProbeTransformer"/>s — <see cref="TransformMode.Expand"/> (fan-out: one sibling per transformer) or
/// <see cref="TransformMode.Chain"/> (compose: each transformer encodes the previous one's output). The inner attack's
/// identity (<see cref="Name"/>, OWASP/MITRE ids, severity) and <see cref="GetEvaluator"/> are preserved, so a
/// transformed attack flows through the unchanged runner, reporters, and baseline — its probes simply land in the same
/// <c>AttackResult</c> bucket as the inner attack. Deterministic: same seed probes ⇒ byte-identical transformed probes.
/// </summary>
public sealed class TransformedAttack : IAttackType
{
    private readonly IAttackType _inner;
    private readonly IReadOnlyList<IProbeTransformer> _transformers;
    private readonly TransformMode _mode;
    private readonly bool _keepOriginal;

    /// <summary>Creates a transformed attack.</summary>
    /// <param name="inner">The attack whose probes are transformed.</param>
    /// <param name="transformers">The transformers to apply (Expand) or compose (Chain). Must be non-empty.</param>
    /// <param name="mode">Expand (fan-out) or Chain (compose).</param>
    /// <param name="keepOriginal">When true, the untransformed seed probes are also emitted alongside the transformed ones.</param>
    public TransformedAttack(
        IAttackType inner,
        IReadOnlyList<IProbeTransformer> transformers,
        TransformMode mode,
        bool keepOriginal = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _transformers = transformers ?? throw new ArgumentNullException(nameof(transformers));
        if (_transformers.Count == 0)
            throw new ArgumentException("At least one transformer is required.", nameof(transformers));
        // Enforce the IProbeTransformer.Name contract at composition time: '+' and '>' are reserved delimiters for
        // probe-Id suffixes (id+name) and chain provenance (name>name), so a Name containing either would make the
        // chain string ambiguous to parse back. Built-in transformers are safe; this guards third-party ones.
        foreach (var t in _transformers)
        {
            if (t.Name.IndexOfAny(['+', '>']) >= 0)
                throw new ArgumentException(
                    $"Transformer Name '{t.Name}' contains a reserved delimiter ('+' or '>'); see IProbeTransformer.Name.",
                    nameof(transformers));
        }
        _mode = mode;
        _keepOriginal = keepOriginal;
    }

    /// <inheritdoc />
    public string Name => _inner.Name;

    /// <inheritdoc />
    public string DisplayName => _inner.DisplayName;

    /// <inheritdoc />
    public string Description => _inner.Description;

    /// <inheritdoc />
    public string OwaspLlmId => _inner.OwaspLlmId;

    /// <inheritdoc />
    public string[] MitreAtlasIds => _inner.MitreAtlasIds;

    /// <inheritdoc />
    public Severity DefaultSeverity => _inner.DefaultSeverity;

    /// <inheritdoc />
    public IProbeEvaluator GetEvaluator() => _inner.GetEvaluator();

    /// <inheritdoc />
    public IReadOnlyList<AttackProbe> GetProbes(Intensity intensity)
    {
        var seeds = _inner.GetProbes(intensity);
        var output = new List<AttackProbe>(seeds.Count * Math.Max(1, _transformers.Count));

        foreach (var seed in seeds)
        {
            if (_keepOriginal)
                output.Add(seed);

            if (_mode == TransformMode.Expand)
            {
                foreach (var t in _transformers)
                    output.AddRange(t.Transform(seed));
            }
            else
            {
                output.AddRange(ApplyChain(seed));
            }
        }

        return output;
    }

    // Chain feeds each transformer the previous step's output probe, so difficulty Bumps once PER step and
    // saturates at Hard — INTENDED: every additional decode layer makes the composed probe strictly harder than a
    // single encoding (a base64-of-rot13 needs two decode hops). Expand, by contrast, bumps each sibling once off the
    // seed. ChainDifficultyAccumulatesPerStep guards this semantic.
    private IEnumerable<AttackProbe> ApplyChain(AttackProbe seed)
    {
        IEnumerable<AttackProbe> current = [seed];
        foreach (var t in _transformers)
            current = current.SelectMany(t.Transform).ToList();
        return current;
    }
}
