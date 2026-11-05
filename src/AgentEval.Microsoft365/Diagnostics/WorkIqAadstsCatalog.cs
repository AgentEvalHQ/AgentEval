// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace AgentEval.Microsoft365.Diagnostics;

/// <summary>One catalogued Entra sign-in error: the raw code, the plain-language cause and what fixes it.</summary>
/// <param name="Code">The raw code, for example <c>AADSTS65001</c>.</param>
/// <param name="Cause">What went wrong, in plain words.</param>
/// <param name="Fix">What the tester or an administrator must do.</param>
internal sealed record WorkIqAadstsEntry(string Code, string Cause, string Fix)
{
    /// <summary>The one-line form stamped into an issue's suggested cause: <c>CODE: cause → fix</c>.</summary>
    public string Describe() => $"{Code}: {Cause} → {Fix}";
}

/// <summary>
/// The Entra (<c>AADSTS…</c>) error codes that matter for a delegated device-code sign-in to Work IQ, each with a
/// plain-language cause and the fix, so the issue register tells an external tester what to do without reading
/// MSAL documentation. The raw code is always kept; a code outside the catalog gets a fallback that points at the
/// public reference, and an MSAL failure without any AADSTS code gets a cause from the MSAL error code. Owned by
/// lane B (M16).
/// </summary>
internal static partial class WorkIqAadstsCatalog
{
    /// <summary>The public reference for every AADSTS code.</summary>
    public const string ReferenceUrl = "https://learn.microsoft.com/entra/identity-platform/reference-error-codes";

    /// <summary>The MSAL.NET exception reference, for errors that carry no AADSTS code.</summary>
    public const string MsalReferenceUrl = "https://aka.ms/msal-net-exceptions";

    /// <summary>The only delegated scope Work IQ accepts.</summary>
    public const string WorkIqScope = "api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask";

    /// <summary>The prefix every catalogued code carries.</summary>
    public const string CodePrefix = "AADSTS";

    private static readonly IReadOnlyDictionary<string, WorkIqAadstsEntry> Catalog = Build();
    private static readonly IReadOnlyDictionary<string, string> MsalErrorCauses = BuildMsalErrorCauses();

    /// <summary>Every catalogued entry keyed by its full code (<c>AADSTS65001</c>), case-insensitive.</summary>
    public static IReadOnlyDictionary<string, WorkIqAadstsEntry> Entries => Catalog;

    /// <summary>The first <c>AADSTS&lt;digits&gt;</c> code in <paramref name="text"/>, upper-cased; null when there is none.</summary>
    /// <param name="text">An exception message or error body.</param>
    public static string? ExtractCode(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var match = CodePattern().Match(text);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    /// <summary>Normalises <c>65001</c>, <c>aadsts65001</c> or <c> AADSTS65001 </c> to <c>AADSTS65001</c>; null when the value is not a code.</summary>
    /// <param name="code">A code with or without its prefix.</param>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var trimmed = code.Trim();
        if (trimmed.StartsWith(CodePrefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[CodePrefix.Length..];
        }

        if (trimmed.Length == 0 || !trimmed.All(char.IsAsciiDigit))
        {
            return null;
        }

        return CodePrefix + trimmed;
    }

    /// <summary>Looks a code up (any accepted form).</summary>
    /// <param name="code">The code.</param>
    /// <param name="entry">The entry when catalogued.</param>
    public static bool TryGet(string? code, [NotNullWhen(true)] out WorkIqAadstsEntry? entry)
    {
        var normalized = Normalize(code);
        if (normalized is not null && Catalog.TryGetValue(normalized, out var found))
        {
            entry = found;
            return true;
        }

        entry = null;
        return false;
    }

    /// <summary>
    /// The plain-language cause and fix for <paramref name="code"/>: the catalog entry when known, otherwise the
    /// fallback that keeps the raw code and points at <see cref="ReferenceUrl"/>; a null or non-code value says so.
    /// </summary>
    /// <param name="code">The code (any accepted form), or null.</param>
    public static string Describe(string? code)
    {
        var normalized = Normalize(code);
        if (normalized is null)
        {
            return $"no AADSTS code in the error; the MSAL error code and message say what is known — see {ReferenceUrl}";
        }

        if (Catalog.TryGetValue(normalized, out var entry))
        {
            return entry.Describe();
        }

        return $"{normalized}: not in this catalog (the raw code is kept); look it up at {ReferenceUrl}";
    }

    /// <summary>
    /// The cause for an MSAL failure that carries no AADSTS code: a known MSAL error code (for example
    /// <c>code_expired</c>) gets its plain-language cause, an interaction-required failure the device-code hint,
    /// anything else a pointer at the MSAL reference with the raw error code kept.
    /// </summary>
    /// <param name="errorCode">The <c>MsalException.ErrorCode</c>.</param>
    /// <param name="interactionRequired">Whether the exception was an <c>MsalUiRequiredException</c>.</param>
    public static string DescribeMsalError(string? errorCode, bool interactionRequired)
    {
        var trimmed = errorCode?.Trim();
        if (!string.IsNullOrEmpty(trimmed) && MsalErrorCauses.TryGetValue(trimmed, out var cause))
        {
            return $"{trimmed}: {cause}";
        }

        if (interactionRequired)
        {
            return $"{(string.IsNullOrEmpty(trimmed) ? "interaction_required" : trimmed)}: silent sign-in needs interaction (no usable cached account, or the session expired or was revoked); run again and complete the device-code sign-in";
        }

        return string.IsNullOrEmpty(trimmed)
            ? $"MSAL failed without an error code; the message says what is known — see {MsalReferenceUrl}"
            : $"{trimmed}: an MSAL error without an AADSTS code; the message says what is known — see {MsalReferenceUrl}";
    }

    private static IReadOnlyDictionary<string, WorkIqAadstsEntry> Build()
    {
        var entries = new[]
        {
            new WorkIqAadstsEntry(
                "AADSTS65001",
                "the user or an administrator has not consented to the application: the WorkIQAgent.Ask delegated permission is not granted",
                "an administrator grants admin consent for the app registration's WorkIQAgent.Ask permission (Entra admin center → App registrations → API permissions → Grant admin consent), or the user consents when prompted if user consent is allowed"),
            new WorkIqAadstsEntry(
                "AADSTS700016",
                "the application was not found in the tenant: appClientId is wrong, or the app is registered in a different tenant than tenantId",
                "check appClientId and tenantId in the config against the Entra app registration; both must belong to the same tenant"),
            new WorkIqAadstsEntry(
                "AADSTS50020",
                "the signed-in account does not exist in the tenant named by tenantId (an account from another tenant, a personal account, or the wrong authority)",
                "sign in with a work account of that tenant, or correct tenantId; a guest account must be invited to the tenant first"),
            new WorkIqAadstsEntry(
                "AADSTS7000218",
                "public client flows are disabled on the app registration, so the device-code sign-in is refused (the service asks for a client secret or assertion)",
                "in the app registration enable 'Allow public client flows' (Authentication → Advanced settings), then run again"),
            new WorkIqAadstsEntry(
                "AADSTS50076",
                "multi-factor authentication is required by the tenant's policy and was not completed",
                "complete MFA in the browser during the device-code sign-in, then run again"),
            new WorkIqAadstsEntry(
                "AADSTS50079",
                "the user must enrol in multi-factor authentication before signing in",
                "enrol for MFA (the browser sign-in guides it), then run again"),
            new WorkIqAadstsEntry(
                "AADSTS50058",
                "a silent sign-in was attempted but the session needs interaction (no valid cached account, or the session expired)",
                "run again and complete the device-code sign-in; this is expected on the first run with a new token cache"),
            new WorkIqAadstsEntry(
                "AADSTS70011",
                "the requested scope is invalid, unknown or malformed",
                $"request exactly the Work IQ scope {WorkIqScope}"),
            new WorkIqAadstsEntry(
                "AADSTS65002",
                "the client id is a Microsoft first-party application that is not pre-authorised for the Work IQ API (consent between first-party apps is configured by Microsoft, not by the tenant)",
                "use your own app registration's client id as appClientId, never a Microsoft-owned client id"),
            new WorkIqAadstsEntry(
                "AADSTS50105",
                "the signed-in user is not assigned to the application (the enterprise application requires user assignment)",
                "an administrator assigns the user to the enterprise application (Entra → Enterprise applications → Users and groups), or turns off 'Assignment required'"),
            new WorkIqAadstsEntry(
                "AADSTS530003",
                "the device must be managed or compliant to reach the resource (a conditional access device policy)",
                "sign in from a managed, compliant device (Intune-enrolled or hybrid-joined), or ask the administrator for an exception for this app"),
            new WorkIqAadstsEntry(
                "AADSTS53003",
                "access was blocked by a conditional access policy",
                "an administrator reviews the conditional access policies for this user, app and location (the Entra sign-in log names the policy)"),
            new WorkIqAadstsEntry(
                "AADSTS900144",
                "a required parameter is missing from the sign-in request (typically the scope or the client id)",
                "check that the config carries tenantId, appClientId and the scope; if the config is complete this is a client bug — report the exact message"),
        };

        var catalog = new Dictionary<string, WorkIqAadstsEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            catalog.Add(entry.Code, entry);
        }

        return catalog;
    }

    private static IReadOnlyDictionary<string, string> BuildMsalErrorCauses() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["code_expired"] = "the device code expired before the sign-in was completed; run again and finish the browser sign-in within the time shown",
            ["authentication_canceled"] = "the sign-in was cancelled before it completed; run again",
            ["invalid_grant"] = "the cached sign-in is no longer valid (password changed, consent revoked, session revoked or expired); run again and complete the device-code sign-in",
            ["interaction_required"] = "silent sign-in needs interaction; run again and complete the device-code sign-in",
            ["user_null"] = "no account is signed in; run again and complete the device-code sign-in",
            ["no_tokens_found"] = "no token for this account is cached; run again and complete the device-code sign-in",
            ["unauthorized_client"] = "the app registration is not allowed to use this sign-in flow; enable 'Allow public client flows' on the app registration",
            ["invalid_client"] = "the client id is wrong or the app registration does not exist in this tenant; check appClientId and tenantId",
            ["invalid_scope"] = $"the scope is invalid; request exactly {WorkIqScope}",
            ["request_timeout"] = "Entra did not answer in time; retry, and check the network or proxy path to login.microsoftonline.com",
            ["service_not_available"] = "Entra is temporarily unavailable; retry later",
        };

    [GeneratedRegex(@"AADSTS\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
