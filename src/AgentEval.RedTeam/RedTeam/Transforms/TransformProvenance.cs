// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam.Transforms;

/// <summary>How a <see cref="TransformedAttack"/> applies its transformers to each seed probe.</summary>
public enum TransformMode
{
    /// <summary>Fan-out: apply each transformer to the seed independently → one sibling per transformer.</summary>
    Expand,

    /// <summary>Compose: feed each transformer the previous one's output → one composed sibling (e.g. base64-of-rot13).</summary>
    Chain,
}

/// <summary>
/// Single source of truth for the probe-Id suffix and <see cref="AttackProbe.Metadata"/> provenance conventions of
/// transformed probes, so they can never drift across transformers. Keys: <see cref="SeedIdKey"/> (the original probe
/// Id), <see cref="ChainKey"/> (apply order, e.g. <c>"base64&gt;rot13"</c>), <see cref="PayloadKey"/> (the text the
/// NEXT transform should encode — i.e. this transform's output, which is what makes chaining compose correctly).
/// </summary>
public static class TransformProvenance
{
    /// <summary>Metadata key: the text the next transform should encode (= the current transform's output).</summary>
    public const string PayloadKey = "transform.payload";

    /// <summary>Metadata key: the applied transform chain in apply order, left = first (e.g. <c>"base64&gt;rot13"</c>).</summary>
    public const string ChainKey = "transform.chain";

    /// <summary>Metadata key: the original (pre-transform) probe Id.</summary>
    public const string SeedIdKey = "transform.seedId";

    /// <summary>
    /// The text a transform should encode: an explicit <see cref="PayloadKey"/> metadata value (set by a prior
    /// transform in a chain) if present, otherwise the probe's <see cref="AttackProbe.Prompt"/>.
    /// </summary>
    public static string PayloadOf(AttackProbe probe) =>
        probe.Metadata is not null && probe.Metadata.TryGetValue(PayloadKey, out var v) && v is string s ? s : probe.Prompt;

    /// <summary>Appends a transform short-code to a probe Id (e.g. <c>"PI-001"</c> + <c>"base64"</c> → <c>"PI-001+base64"</c>).</summary>
    public static string SuffixId(string id, string transformName) => $"{id}+{transformName}";

    /// <summary>Shifts a difficulty by a delta, clamped to the valid <see cref="Difficulty"/> range.</summary>
    public static Difficulty Bump(Difficulty difficulty, DifficultyDelta delta) =>
        (Difficulty)Math.Clamp((int)difficulty + (int)delta, (int)Difficulty.Easy, (int)Difficulty.Hard);

    /// <summary>
    /// Clones the seed's metadata and stamps provenance for the just-applied transform: records the original
    /// <see cref="SeedIdKey"/> (once), appends to <see cref="ChainKey"/>, and sets <see cref="PayloadKey"/> to
    /// <paramref name="output"/> so the next transform in a chain encodes this transform's output.
    /// </summary>
    public static IReadOnlyDictionary<string, object> Stamp(AttackProbe seed, string transformName, string output)
    {
        var d = seed.Metadata is null
            ? new Dictionary<string, object>(StringComparer.Ordinal)
            : new Dictionary<string, object>(seed.Metadata, StringComparer.Ordinal);

        if (!d.ContainsKey(SeedIdKey))
            d[SeedIdKey] = StripSuffix(seed.Id);

        d[ChainKey] = d.TryGetValue(ChainKey, out var c) && c is string cs && cs.Length > 0
            ? $"{cs}>{transformName}"
            : transformName;

        d[PayloadKey] = output;
        return d;
    }

    private static string StripSuffix(string id)
    {
        var i = id.IndexOf('+', StringComparison.Ordinal);
        return i < 0 ? id : id[..i];
    }
}
