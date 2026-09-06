// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;

namespace AgentEval.Output;

/// <summary>
/// The digest of a stimulus — what an agent was ASKED — so two runs can be SHOWN to have been given
/// the same input rather than assumed to have been.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-031 S2, and it exists for S5.</b> <c>agenteval compare</c> must refuse to emit deltas
/// across runs that were not asked the same thing, and refuse loudly rather than warn. That refusal
/// needs a comparable fact about the input, and V1's finding is that it must be computable from two
/// run directories alone, with no manifest and no pack on disk. This is that fact.
/// </para>
/// <para>
/// ⚠ <b>Line endings are normalised before hashing, and that is not cosmetic.</b> The same prompt
/// checked out on Windows and on Linux differs by <c>\r</c> per line. A hash that moved with the
/// checkout would make every cross-platform comparison read RULES_CHANGED, every CI script would
/// grow an <c>--allow-incomparable</c> flag inside a month, and the refusal would be worthless —
/// which is exactly ADR-031 finding V2, one layer down. This repository has already been bitten by
/// invisible bytes in source once.
/// </para>
/// <para>
/// ⚠ <b>Nothing else is normalised.</b> Case, leading and trailing whitespace and Unicode
/// composition all survive, because two prompts differing in any of them are two prompts, and a
/// digest that erased the difference would let an incomparable pair report as comparable — the
/// flattering direction.
/// </para>
/// </remarks>
public static class StimulusHash
{
    /// <summary>The prefix every digest carries, so the algorithm is readable off the value.</summary>
    public const string Prefix = "sha256:";

    /// <summary>
    /// The digest of <paramref name="stimulus"/>, or <see langword="null"/> when there is nothing
    /// to hash.
    /// </summary>
    /// <remarks>
    /// Null for a null or empty stimulus, deliberately: an empty digest that looked like a value
    /// would let two producers that both recorded nothing compare as "the same stimulus". Absent
    /// means absent.
    /// </remarks>
    /// <param name="stimulus">What the agent was asked.</param>
    public static string? Of(string? stimulus)
    {
        if (string.IsNullOrEmpty(stimulus)) return null;

        string normalised = stimulus.Replace("\r\n", "\n", StringComparison.Ordinal)
                                    .Replace("\r", "\n", StringComparison.Ordinal);

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return Prefix + Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>
    /// Whether two recorded digests are the same stimulus.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>A null on either side is NOT COMPARABLE, and this returns false for it.</b> "Nobody
    /// computed a digest" and "the digests match" are different facts, and collapsing them would let
    /// a comparison across two runs that recorded nothing report as a comparison across two runs
    /// that were asked the same thing. A caller that needs to distinguish "differs" from "unknown"
    /// must test for null itself — this method deliberately cannot express the difference, so it
    /// refuses in the safe direction.
    /// </remarks>
    /// <param name="left">One recorded digest.</param>
    /// <param name="right">The other.</param>
    public static bool SameStimulus(string? left, string? right) =>
        left is { Length: > 0 } && right is { Length: > 0 } && string.Equals(left, right, StringComparison.Ordinal);
}
