// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper Hardening Phase 2, P0-3 — a built-in <see cref="IToolResultGate"/> that detects and masks
/// common credential/secret SHAPES (cloud provider keys, private-key blocks, bearer tokens, JWTs) in a tool's
/// result before it reaches the model. Mirrors the chat-side <c>RegexPiiGate</c>'s bounded-regex, mask-with-█
/// approach, applied to the "a tool fetched a file/response that happens to contain a live credential" surface
/// instead of a chat message — e.g. a log file, a config dump, or an error message a tool call returns
/// verbatim can carry a secret the caller never intended to hand to the model (or, downstream, to whatever
/// consumes the model's output).
/// <para>Each pattern is compiled with a bounded <see cref="Regex.MatchTimeout"/> (the repo's ReDoS guard —
/// same discipline as <c>RegexPiiGate</c>/<c>ArgumentPatternGate</c>). A timeout is handled <b>fail-CLOSED</b>
/// (a pattern that could not finish has not proven the result secret-free): the <c>PrivateKey_Block</c> pattern
/// — the only one that empirically times out — falls back to a ReDoS-immune linear scan that completely and
/// precisely masks any PEM key block, and a timeout on any other pattern withholds the whole result. Every
/// timeout is recorded in the verdict reason, never swallowed silently. 300ms (not the 100ms sibling gates use) —
/// empirically, 100ms occasionally lost the race under CI-runner contention on the multi-line
/// <c>PrivateKey_Block</c> pattern (a real, non-deterministic timeout on a call that normally completes in
/// microseconds — pure scheduling jitter under a loaded parallel test run, not a change in the ReDoS threat
/// model <see cref="Regex.MatchTimeout"/> is bounded against). 300ms is still two orders of magnitude below
/// the "no network/LLM cost inline" ceiling <see cref="GateCost.PureCode"/> exists to enforce.</para>
/// <para><b>Always <see cref="ToolResultAction.Redact"/>s, never <see cref="ToolResultAction.Block"/>s</b> — a
/// secret shape is maskable in place (unlike an injection marker, whose danger is the surrounding instruction
/// text), so the rest of the result remains useful to the model with just the credential blanked out.</para>
/// </summary>
public sealed class ToolResultSecretGate : IToolResultGate
{
    private static readonly TimeSpan DefaultSecretTimeout = TimeSpan.FromMilliseconds(300);

    private static readonly (string Name, Regex Pattern)[] DefaultPatterns = BuildPatterns(DefaultSecretTimeout);

    private readonly (string Name, Regex Pattern)[] _patterns;

    /// <summary>Creates the gate. <paramref name="matchTimeout"/> overrides the per-pattern ReDoS
    /// <see cref="Regex.MatchTimeout"/> (default 300ms); a timeout is handled fail-closed (see the class remarks),
    /// so tuning it down on constrained hardware is safe. Must be positive.</summary>
    public ToolResultSecretGate(TimeSpan? matchTimeout = null)
    {
        if (matchTimeout is { } t)
        {
            if (t <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(matchTimeout), t, "matchTimeout must be positive.");
            }

            _patterns = t == DefaultSecretTimeout ? DefaultPatterns : BuildPatterns(t);
        }
        else
        {
            _patterns = DefaultPatterns;
        }
    }

    private static (string Name, Regex Pattern)[] BuildPatterns(TimeSpan timeout) => new (string, Regex)[]
    {
        ("AWS_AccessKeyId", new Regex(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.Compiled, timeout)),
        ("GitHub_Token", new Regex(@"\bgh[pousr]_[A-Za-z0-9]{36,}\b", RegexOptions.Compiled, timeout)),
        ("Slack_Token", new Regex(@"\bxox[baprs]-[0-9A-Za-z-]{10,48}\b", RegexOptions.Compiled, timeout)),
        ("Google_ApiKey", new Regex(@"\bAIza[0-9A-Za-z\-_]{35}\b", RegexOptions.Compiled, timeout)),
        ("Stripe_SecretKey", new Regex(@"\bsk_live_[0-9a-zA-Z]{16,}\b", RegexOptions.Compiled, timeout)),
        // Matches the WHOLE PEM block (BEGIN..END, non-greedy over the body) so the actual key material gets
        // masked too — matching only the header line would mask a label while leaving the key bytes exposed.
        ("PrivateKey_Block", new Regex(@"-----BEGIN\s?((RSA|EC|OPENSSH|DSA|PGP)\s)?PRIVATE KEY-----[\s\S]*?-----END\s?((RSA|EC|OPENSSH|DSA|PGP)\s)?PRIVATE KEY-----", RegexOptions.Compiled, timeout)),
        ("JWT", new Regex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b", RegexOptions.Compiled, timeout)),
        ("Bearer_Token", new Regex(@"\bBearer\s+[A-Za-z0-9\-_.=]{20,}", RegexOptions.Compiled | RegexOptions.IgnoreCase, timeout)),
    };

    /// <inheritdoc/>
    public string PolicyName => "tool-result-secret-detection";

    /// <inheritdoc/>
    public GateCost Cost => GateCost.PureCode;

    /// <inheritdoc/>
    public ValueTask<ToolResultVerdict> InspectAsync(GatedToolResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var text = GateText.Stringify(result.Result);
        if (string.IsNullOrEmpty(text))
        {
            return new ValueTask<ToolResultVerdict>(ToolResultVerdict.Allow(PolicyName));
        }

        var matchedNames = new List<string>();
        var timedOutNames = new List<string>();
        var redacted = text;
        foreach (var (name, pattern) in _patterns)
        {
            try
            {
                if (!pattern.IsMatch(text))
                {
                    continue;
                }

                matchedNames.Add(name);
                redacted = pattern.Replace(redacted, m => new string('█', m.Length));
            }
            catch (RegexMatchTimeoutException)
            {
                // SEC (Fable 5 review — fail-open fix): a pattern that could not finish scanning has NOT proven
                // the result secret-free. The old behavior (treat a timeout as no-match) fails OPEN and leaks
                // exactly the secret the pattern guards — and the multi-line PrivateKey_Block pattern is the one
                // that empirically times out under load. Record the timeout and fail CLOSED below, never silently.
                timedOutNames.Add(name);
            }
        }

        if (timedOutNames.Count > 0)
        {
            // Fail CLOSED for a timed-out scan. For the realistic case (the PEM block pattern) a ReDoS-immune
            // linear scan is a COMPLETE detector for BEGIN…END…PRIVATE KEY----- spans — so it masks any real key
            // precisely and, crucially, leaves a genuinely clean result (a spurious jitter timeout on
            // secret-free text) untouched rather than destroying it.
            if (timedOutNames.Contains("PrivateKey_Block"))
            {
                redacted = MaskPemPrivateKeyBlocksLinear(redacted, out var maskedPem);
                if (maskedPem)
                {
                    matchedNames.Add("PrivateKey_Block");
                }
            }

            // A NON-block pattern timing out is not expected (the others are simple single-line shapes). If one
            // does, we cannot localize the potential secret, so withhold the whole result — the safe direction
            // over utility, deliberately.
            if (timedOutNames.Any(n => !string.Equals(n, "PrivateKey_Block", StringComparison.Ordinal)))
            {
                var withheld = $"█[redacted: secret scan timed out on {string.Join("/", timedOutNames)} — result withheld]";
                return new ValueTask<ToolResultVerdict>(ToolResultVerdict.Redact(
                    PolicyName, withheld,
                    $"tool '{result.FunctionName}' result withheld: secret scan timed out and could not be verified safe"));
            }
        }

        if (matchedNames.Count == 0)
        {
            return new ValueTask<ToolResultVerdict>(ToolResultVerdict.Allow(PolicyName));
        }

        var verdict = ToolResultVerdict.Redact(
            PolicyName, redacted, $"tool '{result.FunctionName}' result contained secret shape(s): {string.Join(", ", matchedNames)}");
        return new ValueTask<ToolResultVerdict>(verdict);
    }

    // ReDoS-immune (pure IndexOf, no backtracking) complete detector/masker for PEM PRIVATE KEY blocks — the
    // fail-closed fallback when the regex-based PrivateKey_Block scan times out. Masks the full span from
    // "-----BEGIN" through the closing "…PRIVATE KEY-----" so the key body is masked, not just the header label.
    private static string MaskPemPrivateKeyBlocksLinear(string text, out bool masked)
    {
        masked = false;
        const string Begin = "-----BEGIN";
        const string End = "-----END";
        const string KeyTail = "PRIVATE KEY-----";

        var search = 0;
        while (true)
        {
            var begin = text.IndexOf(Begin, search, StringComparison.Ordinal);
            if (begin < 0)
            {
                break;
            }

            // Anchor the close on "-----END" first so the OPENING header line's own "PRIVATE KEY-----" is never
            // mistaken for the block end (which would mask only the label and leave the key body exposed).
            var end = text.IndexOf(End, begin + Begin.Length, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            var keyTail = text.IndexOf(KeyTail, end, StringComparison.Ordinal);
            if (keyTail < 0)
            {
                break;
            }

            var blockEnd = keyTail + KeyTail.Length;
            text = text.Substring(0, begin) + new string('█', blockEnd - begin) + text.Substring(blockEnd);
            masked = true;
            search = blockEnd;
        }

        return text;
    }
}
