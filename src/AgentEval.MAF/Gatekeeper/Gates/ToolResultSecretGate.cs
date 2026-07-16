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
/// same discipline as <c>RegexPiiGate</c>/<c>ArgumentPatternGate</c>); a timeout on one pattern is treated as
/// "no match" for that pattern only — the others still run.</para>
/// <para><b>Always <see cref="ToolResultAction.Redact"/>s, never <see cref="ToolResultAction.Block"/>s</b> — a
/// secret shape is maskable in place (unlike an injection marker, whose danger is the surrounding instruction
/// text), so the rest of the result remains useful to the model with just the credential blanked out.</para>
/// </summary>
public sealed class ToolResultSecretGate : IToolResultGate
{
    private static readonly TimeSpan SecretTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly (string Name, Regex Pattern)[] Patterns =
    {
        ("AWS_AccessKeyId", new Regex(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.Compiled, SecretTimeout)),
        ("GitHub_Token", new Regex(@"\bgh[pousr]_[A-Za-z0-9]{36,}\b", RegexOptions.Compiled, SecretTimeout)),
        ("Slack_Token", new Regex(@"\bxox[baprs]-[0-9A-Za-z-]{10,48}\b", RegexOptions.Compiled, SecretTimeout)),
        ("Google_ApiKey", new Regex(@"\bAIza[0-9A-Za-z\-_]{35}\b", RegexOptions.Compiled, SecretTimeout)),
        ("Stripe_SecretKey", new Regex(@"\bsk_live_[0-9a-zA-Z]{16,}\b", RegexOptions.Compiled, SecretTimeout)),
        // Matches the WHOLE PEM block (BEGIN..END, non-greedy over the body) so the actual key material gets
        // masked too — matching only the header line would mask a label while leaving the key bytes exposed.
        ("PrivateKey_Block", new Regex(@"-----BEGIN\s?((RSA|EC|OPENSSH|DSA|PGP)\s)?PRIVATE KEY-----[\s\S]*?-----END\s?((RSA|EC|OPENSSH|DSA|PGP)\s)?PRIVATE KEY-----", RegexOptions.Compiled, SecretTimeout)),
        ("JWT", new Regex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b", RegexOptions.Compiled, SecretTimeout)),
        ("Bearer_Token", new Regex(@"\bBearer\s+[A-Za-z0-9\-_.=]{20,}", RegexOptions.Compiled | RegexOptions.IgnoreCase, SecretTimeout)),
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
        var redacted = text;
        foreach (var (name, pattern) in Patterns)
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
                // Treat a timeout as no-match for this pattern (ReDoS guard); other patterns still run.
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
}
