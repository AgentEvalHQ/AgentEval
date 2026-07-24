// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Ready-made <c>isIdentifier</c> predicates for <see cref="ReferentialIntegrityGate"/> (Fable 5 §7). The gate's
/// default only flags tokens that contain a <b>digit</b>, so an <b>all-letter</b> id — a username, a slug, an
/// <c>admin_backup</c> your backend accepts — is not validated by default, and since an injection chooses the id
/// shape that is a deterministic gap. These give a caller whose ids can be alpha-only a first-class way to close
/// it <b>without</b> hand-writing a predicate and without changing the (safe, low-false-alarm) default.
/// <para>Prefer <see cref="Matching"/> with your id's real shape; reach for <see cref="AlphanumericMinLength"/>
/// only when you cannot express the shape and accept its higher false-alarm rate. Run the gate
/// <see cref="ToolGatePolicy.WarnOnly"/> first to measure alarms before enforcing, per the gate's guidance.
/// All returned predicates are pure and safe to share across gate instances.</para>
/// </summary>
public static class IdentifierRecognizers
{
    /// <summary>The gate's DEFAULT, exposed for reuse/composition: a token of length ≥ <paramref name="minLength"/>
    /// that contains at least one digit (fires on <c>A-1042</c> / <c>FAKE-9931</c>, not on plain words). The
    /// permissive baseline — it does not validate alpha-only ids.</summary>
    public static Func<string, bool> ContainsDigit(int minLength = 4)
    {
        if (minLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minLength), "must be at least 1.");
        }

        return token => token.Length >= minLength && token.Any(char.IsDigit);
    }

    /// <summary>Precise (recommended for alpha-only ids): a token is an id iff it matches <paramref name="pattern"/>
    /// — use your id's real shape, e.g. <c>new Regex("^(usr|ord)_[a-z0-9]{6,}$", RegexOptions.None,
    /// TimeSpan.FromMilliseconds(100))</c>. Supply a bounded, anchored pattern (the gate applies it per token and
    /// does not impose its own timeout on a caller-supplied regex).</summary>
    public static Func<string, bool> Matching(Regex pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return token => pattern.IsMatch(token);
    }

    /// <summary>Stricter length-only: ANY token of length ≥ <paramref name="minLength"/> is treated as an id,
    /// letters-only included (so <c>admin_backup</c> / a long slug is validated). Highest recall, highest
    /// false-alarm rate — flags ordinary long words too, so measure under <c>WarnOnly</c> before enforcing. Use
    /// only when the id shape can't be expressed as a <see cref="Matching"/> pattern.</summary>
    public static Func<string, bool> AlphanumericMinLength(int minLength = 8)
    {
        if (minLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minLength), "must be at least 1.");
        }

        return token => token.Length >= minLength;
    }
}
