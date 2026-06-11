// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam.Transforms;

/// <summary>How applying a transform shifts the difficulty of the probe it produces.</summary>
public enum DifficultyDelta
{
    /// <summary>The transform makes the probe easier than its input.</summary>
    Lower = -1,

    /// <summary>The transform leaves difficulty unchanged.</summary>
    Same = 0,

    /// <summary>The transform makes the probe harder than its input (typical for obfuscation/encoding).</summary>
    Raise = 1,
}

/// <summary>
/// Produces transformed sibling(s) of an <see cref="AttackProbe"/> — encodings, ciphers, obfuscations (Wave A is
/// deterministic only). Prior art: PyRIT prompt converters (MIT) and garak <c>probes.encoding</c> (Apache-2.0),
/// re-implemented natively.
/// </summary>
public interface IProbeTransformer
{
    /// <summary>
    /// Stable short code used in transformed-probe Id suffixes and provenance (e.g. <c>"base64"</c>, <c>"rot13"</c>).
    /// Lowercase, no spaces. MUST NOT contain <c>'+'</c> or <c>'&gt;'</c>: those are the reserved delimiters for the
    /// probe-Id suffix (<c>id+name</c>) and the <see cref="TransformProvenance.ChainKey"/> apply-order string
    /// (<c>name&gt;name</c>) respectively, so a Name containing either would make the chain provenance ambiguous to
    /// parse back.
    /// </summary>
    string Name { get; }

    /// <summary>How this transform shifts difficulty relative to its input.</summary>
    DifficultyDelta DifficultyImpact { get; }

    /// <summary>
    /// Returns zero or more transformed siblings of <paramref name="probe"/>. MUST be deterministic — the same input
    /// always yields byte-identical output(s), with no RNG / wall-clock / culture-sensitive formatting — so baselines
    /// stay stable. Most transformers return exactly one sibling; a transformer MAY return none when it does not apply.
    /// </summary>
    IEnumerable<AttackProbe> Transform(AttackProbe probe);
}
