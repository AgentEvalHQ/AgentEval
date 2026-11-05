// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentEval.Microsoft365.Diagnostics;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace AgentEval.Microsoft365.Auth;

/// <summary>
/// What the last successful token acquisition established, for the <c>m365 doctor</c> command: expiry, the
/// token's tenant, the granted scopes and a masked account hint. It never carries the token itself.
/// </summary>
/// <param name="ExpiresOn">When the access token expires.</param>
/// <param name="TenantId">The tenant id the token was issued for, when MSAL reported one.</param>
/// <param name="Scopes">The scopes granted.</param>
/// <param name="AccountHint">The signed-in account, masked (<c>j***@contoso.com</c>); null when unknown.</param>
/// <param name="AcquiredSilently">Whether the token came from the cache (silent) rather than a device-code sign-in.</param>
internal sealed record WorkIqTokenInfo(
    DateTimeOffset ExpiresOn,
    string? TenantId,
    IReadOnlyList<string> Scopes,
    string? AccountHint,
    bool AcquiredSilently);

/// <summary>
/// The default <see cref="IWorkIqTokenProvider"/>: an MSAL public client for the configured tenant + app
/// registration, silent acquisition first, device-code sign-in (instructions written to
/// <see cref="DeviceCodePrompt"/>) when no usable cached account exists, and a persisted, OS-encrypted token cache
/// (DPAPI / Keychain / libsecret; never an unprotected file) keyed by <see cref="ComputeCacheFileName"/> under
/// <c>%LOCALAPPDATA%/AgentEval/msal_cache/</c>. Mirrors the Copilot Studio provider so the two can later share
/// one helper. Constructing it makes no network call; the first <see cref="GetAccessTokenAsync"/> may prompt.
/// Not independently live-verified: the device-code and silent-refresh paths need a real Entra app registration.
/// </summary>
/// <remarks>
/// Every step is traced through <see cref="Diagnostics"/> (<c>auth.silent_hit</c>, <c>auth.silent_miss</c>,
/// <c>auth.device_code_prompted</c>, <c>auth.token_acquired</c>, <c>auth.failed</c>); the device-code message
/// MSAL produces (where to sign in and which code to enter) is the one auth text that is shown, on the prompt
/// writer and in the trace. Tokens never reach a trace. An MSAL service failure is wrapped in
/// <see cref="WorkIqProtocolException"/> (<see cref="WorkIqFailureCategory.Forbidden"/> for consent/permission
/// AADSTS codes or HTTP 403, else <see cref="WorkIqFailureCategory.Unauthorized"/>) carrying the AADSTS code and
/// MSAL error code only, so the transport classifier can map it.
/// </remarks>
public sealed partial class WorkIqDeviceCodeTokenProvider : IWorkIqTokenProvider
{
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private IPublicClientApplication? _app;
    private WorkIqTokenInfo? _lastTokenInfo;

    /// <summary>Creates the provider.</summary>
    /// <param name="tenantId">The Entra tenant id (from <see cref="Microsoft365Config.TenantId"/>).</param>
    /// <param name="appClientId">The public-client app registration id (from <see cref="Microsoft365Config.AppClientId"/>).</param>
    /// <param name="scope">The token scope (from <see cref="Microsoft365Config.Scope"/>).</param>
    /// <param name="deviceCodePrompt">Where the device-code sign-in instructions are written; defaults to <see cref="Console.Error"/>.</param>
    public WorkIqDeviceCodeTokenProvider(string tenantId, string appClientId, string scope, TextWriter? deviceCodePrompt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appClientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        TenantId = tenantId;
        AppClientId = appClientId;
        Scope = scope;
        DeviceCodePrompt = deviceCodePrompt ?? Console.Error;
    }

    /// <summary>The Entra tenant id.</summary>
    internal string TenantId { get; }

    /// <summary>The public-client app registration id.</summary>
    internal string AppClientId { get; }

    /// <summary>The token scope.</summary>
    internal string Scope { get; }

    /// <summary>Where the device-code sign-in instructions are written.</summary>
    internal TextWriter DeviceCodePrompt { get; }

    /// <summary>The diagnostics auth events are traced through; never null (defaults to <see cref="WorkIqDiagnostics.None"/>).</summary>
    internal WorkIqDiagnostics Diagnostics { get; init; } = WorkIqDiagnostics.None;

    /// <summary>What the last successful acquisition established (no token); null before the first success.</summary>
    internal WorkIqTokenInfo? LastTokenInfo => Volatile.Read(ref _lastTokenInfo);

    /// <summary>The directory of the persisted MSAL cache (<c>%LOCALAPPDATA%/AgentEval/msal_cache</c> or the platform equivalent).</summary>
    internal static string CacheDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentEval", "msal_cache");

    /// <summary>The full path of this provider's cache file (for the doctor report; nothing is created by reading it).</summary>
    internal string CacheFilePath => Path.Combine(CacheDirectory, ComputeCacheFileName(TenantId, AppClientId));

    /// <inheritdoc/>
    public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var app = await EnsureAppAsync(cancellationToken).ConfigureAwait(false);
        var scopes = new[] { Scope };
        var started = Diagnostics.Clock.GetTimestamp();

        var accounts = await app.GetAccountsAsync().ConfigureAwait(false);
        var account = accounts.FirstOrDefault();
        if (account is not null)
        {
            try
            {
                var silent = await app.AcquireTokenSilent(scopes, account).ExecuteAsync(cancellationToken).ConfigureAwait(false);
                Record(silent, acquiredSilently: true);
                Diagnostics.Trace(
                    WorkIqTraceStage.Auth,
                    "auth.silent_hit",
                    $"token acquired silently from the cache for {MaskUsername(account.Username) ?? "a cached account"}; expires {silent.ExpiresOn:O}; tenant {silent.TenantId ?? "(unknown)"}",
                    elapsedMs: Diagnostics.Clock.GetElapsedTime(started).TotalMilliseconds);
                return silent.AccessToken;
            }
            catch (MsalUiRequiredException ex)
            {
                Diagnostics.Trace(
                    WorkIqTraceStage.Auth,
                    "auth.silent_miss",
                    $"the cached account cannot be used silently ({ex.ErrorCode}, {ex.Classification}); falling back to the device-code sign-in",
                    WorkIqTraceLevel.Warning,
                    elapsedMs: Diagnostics.Clock.GetElapsedTime(started).TotalMilliseconds);
            }
            catch (MsalServiceException ex)
            {
                throw Wrap(ex, "silent acquisition", started);
            }
        }
        else
        {
            Diagnostics.Trace(WorkIqTraceStage.Auth, "auth.no_cached_account", $"no cached account in {CacheFilePath}; starting the device-code sign-in");
        }

        try
        {
            var result = await app.AcquireTokenWithDeviceCode(scopes, ShowDeviceCodePrompt).ExecuteAsync(cancellationToken).ConfigureAwait(false);
            Record(result, acquiredSilently: false);
            Diagnostics.Trace(
                WorkIqTraceStage.Auth,
                "auth.token_acquired",
                $"device-code sign-in completed for {MaskUsername(result.Account?.Username) ?? "the signed-in account"}; expires {result.ExpiresOn:O}; tenant {result.TenantId ?? "(unknown)"}",
                elapsedMs: Diagnostics.Clock.GetElapsedTime(started).TotalMilliseconds);
            return result.AccessToken;
        }
        catch (MsalServiceException ex)
        {
            throw Wrap(ex, "device-code sign-in", started);
        }
        catch (MsalClientException ex)
        {
            throw WrapClient(ex, "device-code sign-in", started);
        }
    }

    /// <summary>
    /// Deterministic, filesystem-safe cache file name for a (tenantId, appClientId) pair — a SHA-256 hash so the
    /// name is stable across runs, unique per tenant+app, independent of either id's characters, and distinct from
    /// the Copilot Studio provider's cache (prefix <c>workiq_</c>).
    /// </summary>
    internal static string ComputeCacheFileName(string tenantId, string appClientId)
    {
        var bytes = Encoding.UTF8.GetBytes($"{tenantId}|{appClientId}");
        // Convert.ToHexStringLower is .NET 9+ only; this project also targets net8.0, so lower-case explicitly.
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return $"workiq_{hash}.msalcache.bin";
    }

    /// <summary>Masks a user principal name for diagnostics (<c>j***@contoso.com</c>); null for null or blank.</summary>
    /// <param name="username">The account's username.</param>
    internal static string? MaskUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        var trimmed = username.Trim();
        var at = trimmed.IndexOf('@');
        return at > 0 ? trimmed[0] + "***" + trimmed[at..] : trimmed[0] + "***";
    }

    /// <summary>Extracts the <c>AADSTS</c> code from an MSAL message, or null.</summary>
    /// <param name="message">The exception message.</param>
    internal static string? ExtractAadstsCode(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        var match = AadstsPattern().Match(message);
        return match.Success ? match.Value : null;
    }

    /// <summary>Whether an AADSTS code or HTTP status describes a consent/permission refusal (→ Forbidden) rather than an authentication failure (→ Unauthorized).</summary>
    /// <param name="aadstsCode">The AADSTS code, when any.</param>
    /// <param name="httpStatus">The HTTP status MSAL saw.</param>
    internal static bool IsPermissionRefusal(string? aadstsCode, int httpStatus) =>
        httpStatus == 403
        || aadstsCode is "AADSTS65001" or "AADSTS65004" or "AADSTS650052" or "AADSTS90094" or "AADSTS53003" or "AADSTS7000215";

    private Task ShowDeviceCodePrompt(DeviceCodeResult deviceCode)
    {
        // The device-code message tells the user where to sign in and which code to type: it must be shown.
        Diagnostics.Trace(
            WorkIqTraceStage.Auth,
            "auth.device_code_prompted",
            $"device-code sign-in required (code valid until {deviceCode.ExpiresOn:O}): {deviceCode.Message}",
            WorkIqTraceLevel.Warning);
        DeviceCodePrompt.WriteLine();
        DeviceCodePrompt.WriteLine("  Microsoft 365 (Work IQ) sign-in required:");
        DeviceCodePrompt.WriteLine($"    {deviceCode.Message}");
        DeviceCodePrompt.WriteLine();
        DeviceCodePrompt.Flush();
        return Task.CompletedTask;
    }

    private void Record(AuthenticationResult result, bool acquiredSilently)
    {
        var tenant = string.IsNullOrWhiteSpace(result.TenantId) ? result.Account?.HomeAccountId?.TenantId : result.TenantId;
        var info = new WorkIqTokenInfo(
            result.ExpiresOn,
            string.IsNullOrWhiteSpace(tenant) ? null : tenant,
            result.Scopes?.ToArray() ?? [],
            MaskUsername(result.Account?.Username),
            acquiredSilently);
        Volatile.Write(ref _lastTokenInfo, info);
    }

    private WorkIqProtocolException Wrap(MsalServiceException ex, string phase, long started)
    {
        var aadsts = ExtractAadstsCode(ex.Message);
        var category = IsPermissionRefusal(aadsts, ex.StatusCode) ? WorkIqFailureCategory.Forbidden : WorkIqFailureCategory.Unauthorized;
        var failure = new WorkIqFailure(
            category,
            $"MSAL {ex.GetType().Name} during {phase}: error code {ex.ErrorCode}{(aadsts is null ? "" : ", " + aadsts)}, HTTP {ex.StatusCode}; " +
            (category == WorkIqFailureCategory.Forbidden
                ? "the app registration or user lacks consent/permission for the scope"
                : "re-run the device-code sign-in") +
            $"; scope must be {Scope}",
            OutcomeCertainty.Known,
            WorkIqOperation.Unknown,
            HttpStatus: ex.StatusCode == 0 ? null : ex.StatusCode);
        Diagnostics.Trace(
            WorkIqTraceStage.Auth,
            "auth.failed",
            failure.SafeMessage,
            WorkIqTraceLevel.Error,
            httpStatus: failure.HttpStatus,
            elapsedMs: Diagnostics.Clock.GetElapsedTime(started).TotalMilliseconds);
        Diagnostics.Issue(new WorkIqIssue(
            default,
            WorkIqTraceStage.Auth,
            category == WorkIqFailureCategory.Forbidden ? WorkIqIssueCategory.Permission : WorkIqIssueCategory.Authentication,
            "auth.msal_service_error",
            failure.SafeMessage,
            category == WorkIqFailureCategory.Forbidden
                ? "Admin consent for the delegated WorkIQAgent.Ask permission is missing, the user declined consent, or a conditional-access policy blocked the sign-in."
                : "The sign-in failed or the device code expired/was declined; re-run the device-code sign-in in the configured tenant.")
        {
            HttpStatus = failure.HttpStatus,
            AadstsCode = aadsts,
        });
        return new WorkIqProtocolException(failure, ex);
    }

    private WorkIqProtocolException WrapClient(MsalClientException ex, string phase, long started)
    {
        var failure = new WorkIqFailure(
            WorkIqFailureCategory.Unauthorized,
            $"MSAL {ex.GetType().Name} during {phase}: error code {ex.ErrorCode}; re-run the device-code sign-in; scope must be {Scope}",
            OutcomeCertainty.Known,
            WorkIqOperation.Unknown);
        Diagnostics.Trace(WorkIqTraceStage.Auth, "auth.failed", failure.SafeMessage, WorkIqTraceLevel.Error, elapsedMs: Diagnostics.Clock.GetElapsedTime(started).TotalMilliseconds);
        Diagnostics.Issue(WorkIqIssue.Create(
            WorkIqTraceStage.Auth,
            WorkIqIssueCategory.Authentication,
            "auth.msal_client_error",
            failure.SafeMessage,
            "The device code expired before the sign-in completed, the sign-in was canceled, or the local MSAL cache could not be used; re-run the device-code sign-in."));
        return new WorkIqProtocolException(failure, ex);
    }

    private async Task<IPublicClientApplication> EnsureAppAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
        {
            return _app;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_app is not null)
            {
                return _app;
            }

            var app = PublicClientApplicationBuilder
                .Create(AppClientId)
                .WithTenantId(TenantId)
                .Build();

            var cacheFileName = ComputeCacheFileName(TenantId, AppClientId);
            var storageProperties = new StorageCreationPropertiesBuilder(cacheFileName, CacheDirectory)
                .WithMacKeyChain("com.agenteval.cli.workiq", "AgentEvalWorkIqMsalCache")
                .WithLinuxKeyring(
                    "com.agenteval.cli.workiq.tokencache",
                    "default",
                    "AgentEval Work IQ MSAL token cache",
                    new KeyValuePair<string, string>("Product", "AgentEval"),
                    new KeyValuePair<string, string>("Component", "Microsoft365"))
                .Build();

            var cacheHelper = await MsalCacheHelper.CreateAsync(storageProperties).ConfigureAwait(false);
            cacheHelper.RegisterCache(app.UserTokenCache);
            Diagnostics.Trace(WorkIqTraceStage.Auth, "auth.cache_registered", $"MSAL public client ready for tenant {TenantId}, app {AppClientId}; OS-encrypted cache file {Path.Combine(CacheDirectory, cacheFileName)}");

            _app = app;
            return app;
        }
        finally
        {
            _initLock.Release();
        }
    }

    [GeneratedRegex(@"AADSTS\d{4,}", RegexOptions.CultureInvariant)]
    private static partial Regex AadstsPattern();
}
