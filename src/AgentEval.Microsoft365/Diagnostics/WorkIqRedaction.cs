// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace AgentEval.Microsoft365.Diagnostics;

/// <summary>
/// The one redaction policy of the provider's diagnostics: tokens, auth codes, secrets, cookies and the
/// <c>Authorization</c> header never reach a trace, an issue, a capture header dump, a report or an exception
/// message. Everything else passes through unchanged. The MSAL device-code message (where to sign in, which user code
/// to enter) is shown in full on the sign-in prompt only: the device-code token provider masks the user code itself
/// before it traces the message, so no trace or issue file carries it. The patterns are simple
/// and linear (no nested quantifiers) and run without a match timeout: a timeout that reads as "no match" would
/// be a load-dependent fail-open.
/// </summary>
public static partial class WorkIqRedaction
{
    /// <summary>What a redacted value is replaced with.</summary>
    public const string RedactedMarker = "[redacted]";

    /// <summary>What a JWT-looking string is replaced with.</summary>
    public const string JwtRedactedMarker = "[jwt redacted]";

    private static readonly string[] SensitiveHeaderNames = ["Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie"];

    /// <summary>
    /// Redacts <paramref name="text"/>: <c>Bearer &lt;anything&gt;</c> → <c>Bearer [redacted]</c>; JWT-looking strings
    /// (<c>eyJ….….…</c>, JWE five-segment forms included) → <c>[jwt redacted]</c>; <c>Cookie:</c> / <c>Set-Cookie:</c>
    /// header lines → the name with <c>[redacted]</c>; the query/form values of <c>code</c>, <c>device_code</c>,
    /// <c>client_secret</c>, <c>access_token</c>, <c>refresh_token</c> and <c>id_token</c> → <c>[redacted]</c>; and
    /// the JSON string values of <c>device_code</c>, <c>client_secret</c>, <c>access_token</c>, <c>refresh_token</c>,
    /// <c>id_token</c>, <c>Authorization</c>, <c>Proxy-Authorization</c>, <c>Cookie</c> and <c>Set-Cookie</c> →
    /// <c>[redacted]</c>. Null and empty pass through; so does a device-code sign-in message.
    /// </summary>
    /// <param name="text">The text to redact.</param>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = BearerPattern().Replace(text, "$1 " + RedactedMarker);
        result = JwtPattern().Replace(result, JwtRedactedMarker);
        result = CookieHeaderLinePattern().Replace(result, "$1: " + RedactedMarker);
        result = QueryOrFormValuePattern().Replace(result, "$1=" + RedactedMarker);
        result = JsonSecretPropertyPattern().Replace(result, "$1" + RedactedMarker + "$2");
        return result;
    }

    /// <summary>Whether a header's value is never retained (<c>Authorization</c>, <c>Proxy-Authorization</c>, <c>Cookie</c>, <c>Set-Cookie</c>; case-insensitive).</summary>
    /// <param name="name">The header name.</param>
    public static bool IsSensitiveHeader(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var sensitive in SensitiveHeaderNames)
        {
            if (string.Equals(sensitive, name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Copies headers into a case-insensitive dictionary (values joined with <c>", "</c>): sensitive headers become
    /// <see cref="RedactedMarker"/>, every other value goes through <see cref="Redact"/>. Accepts any
    /// <see cref="System.Net.Http.Headers.HttpHeaders"/> (request, response or content headers).
    /// </summary>
    /// <param name="headers">The headers.</param>
    public static IReadOnlyDictionary<string, string> RedactHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddRedacted(result, headers);
        return result;
    }

    /// <summary>Redacted copy of a request's headers and content headers together.</summary>
    /// <param name="request">The request.</param>
    public static IReadOnlyDictionary<string, string> RedactHeaders(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddRedacted(result, request.Headers);
        if (request.Content is not null)
        {
            AddRedacted(result, request.Content.Headers);
        }

        return result;
    }

    /// <summary>Redacted copy of a response's headers and content headers together.</summary>
    /// <param name="response">The response.</param>
    public static IReadOnlyDictionary<string, string> RedactHeaders(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddRedacted(result, response.Headers);
        AddRedacted(result, response.Content.Headers);
        return result;
    }

    private static void AddRedacted(Dictionary<string, string> target, IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
    {
        foreach (var (name, values) in headers)
        {
            var joined = string.Join(", ", values);
            target[name] = IsSensitiveHeader(name) ? RedactedMarker : Redact(joined);
        }
    }

    // "Bearer <anything>" (scheme case-insensitive, original casing kept): the whole credential after the scheme,
    // up to whitespace or a delimiter a credential never contains (so a JSON dump stays valid JSON).
    [GeneratedRegex(@"\b(Bearer)\s+[^\s""'<>,;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();

    // A JWS (3 segments; an empty signature for alg=none is allowed) or JWE (5 segments): base64url segments
    // starting with the base64url of '{"' — "eyJ".
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}(?:\.[A-Za-z0-9_-]*){1,3}", RegexOptions.CultureInvariant)]
    private static partial Regex JwtPattern();

    // "Cookie: …" / "Set-Cookie: …" header lines in a text dump: the whole value to the end of the line (or to a
    // quote, so a JSON dump stays valid JSON; the JSON form is handled below).
    [GeneratedRegex(@"\b(Set-Cookie|Cookie)\s*:\s*[^\r\n""]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CookieHeaderLinePattern();

    // Query-string / form values: code=…, device_code=…, client_secret=…, access_token=…, refresh_token=…,
    // id_token=…; the key must not be the tail of a longer identifier (error_code=42 is untouched), an empty
    // value stays empty (nothing unmeasured becomes a value), and an already redacted value is left alone.
    [GeneratedRegex(@"(?<![A-Za-z0-9_])(code|device_code|client_secret|access_token|refresh_token|id_token)=(?!\[redacted\])([^&\s""'#;,)\]]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QueryOrFormValuePattern();

    // JSON string properties of a token response ("access_token": "…" etc.) and of a header dump ("Authorization":
    // "…", "Cookie": "…"). A bare "code" is deliberately NOT in this list — a gateway error body's
    // "code": "TooManyRequests" is diagnostic information.
    [GeneratedRegex(@"(""(?:device_code|client_secret|access_token|refresh_token|id_token|Authorization|Proxy-Authorization|Cookie|Set-Cookie)""\s*:\s*"")(?:[^""\\]|\\.)+("")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JsonSecretPropertyPattern();
}
