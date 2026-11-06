// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
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
/// MSAL produces (where to sign in and which code to enter) is the one auth text that is shown, in full on the prompt
/// writer; the trace keeps the page and the expiry with the user code masked. Tokens never reach a trace. The wait for
/// the sign-in is bounded by the device code's own expiry (MSAL ends it with <c>code_expired</c>), not by an HTTP
/// timeout: the A2A client acquires the token before each call, outside the call's timeout. An MSAL service failure is wrapped in
/// <see cref="WorkIqProtocolException"/> (<see cref="WorkIqFailureCategory.Forbidden"/> for consent/permission
/// AADSTS codes or HTTP 403, else <see cref="WorkIqFailureCategory.Unauthorized"/>) carrying the AADSTS code and
/// MSAL error code only, with the MSAL exception as its inner exception. The provider registers no issue itself: the
/// auth handler that asked for the token registers the one issue through <see cref="WorkIqIssueClassifier"/>, which
/// reads the AADSTS code in the safe message and takes its cause from <see cref="WorkIqAadstsCatalog"/>.
/// </remarks>
public sealed class WorkIqDeviceCodeTokenProvider : IWorkIqTokenProvider, IWorkIqSignInWaitSource
{
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private IPublicClientApplication? _app;
    private WorkIqTokenInfo? _lastTokenInfo;
    private WorkIqDiagnostics _diagnostics = WorkIqDiagnostics.None;
    private bool _diagnosticsFromHost;

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

    /// <summary>
    /// Where the device-code sign-in instructions are written (in full: the person must read the page and the code). A
    /// host that keeps a log routes it through a writer that shows the code on screen and masks it in the log.
    /// </summary>
    public TextWriter DeviceCodePrompt { get; }

    /// <summary>
    /// The diagnostics the auth events (<c>auth.*</c>) are traced through; never null. A host that owns a
    /// <see cref="WorkIqDiagnostics"/> sets it here. When the host sets none, the connection that uses this provider
    /// attaches its own — <see cref="Microsoft365AgentFactory.BuildLive"/> the live agent's, the readiness check its run's —
    /// so one provider shared by the readiness check and the live agent traces each sign-in into the run that started it.
    /// </summary>
    public WorkIqDiagnostics Diagnostics
    {
        get => Volatile.Read(ref _diagnostics);
        init
        {
            _diagnostics = value ?? throw new ArgumentNullException(nameof(value));
            _diagnosticsFromHost = true;
        }
    }

    /// <summary>
    /// Attaches the diagnostics of the connection now using this provider, unless the host set <see cref="Diagnostics"/>
    /// itself (the host's choice wins).
    /// </summary>
    /// <param name="diagnostics">The connection's diagnostics.</param>
    internal void AttachDiagnostics(WorkIqDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!_diagnosticsFromHost)
        {
            Volatile.Write(ref _diagnostics, diagnostics);
        }
    }

    /// <summary>What the last successful acquisition established (no token); null before the first success.</summary>
    internal WorkIqTokenInfo? LastTokenInfo => Volatile.Read(ref _lastTokenInfo);

    /// <inheritdoc/>
    Action<DateTimeOffset>? IWorkIqSignInWaitSource.SignInPrompted
    {
        get => SignInPrompted;
        set => SignInPrompted = value;
    }

    /// <summary>Invoked with the device code's expiry when the device-code sign-in starts (the readiness check bounds its wait by it).</summary>
    internal Action<DateTimeOffset>? SignInPrompted { get; set; }

    /// <summary>The directory of the persisted MSAL cache (<c>%LOCALAPPDATA%/AgentEval/msal_cache</c> or the platform equivalent).</summary>
    internal static string CacheDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentEval", "msal_cache");

    /// <summary>The full path of this provider's cache file (for the doctor report; nothing is created by reading it).</summary>
    internal string CacheFilePath => Path.Combine(CacheDirectory, ComputeCacheFileName(TenantId, AppClientId));

    /// <inheritdoc/>
    public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        // The turn waiting on this token, if any: captured here, before MSAL runs the prompt callback on its own flow.
        var signInWait = WorkIqSignInWait.Current;
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
            var result = await app.AcquireTokenWithDeviceCode(scopes, deviceCode => ShowDeviceCodePrompt(deviceCode, signInWait)).ExecuteAsync(cancellationToken).ConfigureAwait(false);
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
        finally
        {
            // The sign-in attempt is over (a token, a failure or a stop): a waiting turn's deadline bounds the network again.
            NotifySignInEnded(signInWait);
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

    /// <summary>Whether an AADSTS code or HTTP status describes a consent/permission refusal (→ Forbidden) rather than an authentication failure (→ Unauthorized).</summary>
    /// <param name="aadstsCode">The AADSTS code, when any.</param>
    /// <param name="httpStatus">The HTTP status MSAL saw.</param>
    internal static bool IsPermissionRefusal(string? aadstsCode, int httpStatus) =>
        httpStatus == 403 || WorkIqAadstsCatalog.IsPermissionRefusal(aadstsCode);

    private Task ShowDeviceCodePrompt(DeviceCodeResult deviceCode, IWorkIqSignInWaitListener? signInWait)
    {
        // The device-code message tells the user where to sign in and which code to type: it must be shown, in full, on
        // the prompt writer. The trace keeps where and until when, with the user code masked: the trace is a file that
        // is sent back, and the code is a credential for the next minutes.
        Diagnostics.Trace(
            WorkIqTraceStage.Auth,
            "auth.device_code_prompted",
            $"device-code sign-in required (code valid until {deviceCode.ExpiresOn:O}): {MaskUserCode(deviceCode.Message, deviceCode.UserCode)}",
            WorkIqTraceLevel.Warning);
        DeviceCodePrompt.WriteLine();
        DeviceCodePrompt.WriteLine("  Microsoft 365 (Work IQ) sign-in required:");
        DeviceCodePrompt.WriteLine($"    {deviceCode.Message}");
        DeviceCodePrompt.WriteLine();
        DeviceCodePrompt.Flush();
        try
        {
            SignInPrompted?.Invoke(deviceCode.ExpiresOn);
        }
        catch (Exception ex)
        {
            Diagnostics.Trace(WorkIqTraceStage.Auth, "auth.sign_in_observer_failed", $"the sign-in observer threw {ex.GetType().Name}; the sign-in continues", WorkIqTraceLevel.Warning);
        }

        try
        {
            // A turn waiting on this token now waits for the code's expiry, not for its network deadline.
            signInWait?.SignInStarted(deviceCode.ExpiresOn);
        }
        catch (Exception ex)
        {
            Diagnostics.Trace(WorkIqTraceStage.Auth, "auth.sign_in_observer_failed", $"the turn's sign-in listener threw {ex.GetType().Name}; the sign-in continues", WorkIqTraceLevel.Warning);
        }

        return Task.CompletedTask;
    }

    private void NotifySignInEnded(IWorkIqSignInWaitListener? signInWait)
    {
        try
        {
            signInWait?.SignInEnded();
        }
        catch (Exception ex)
        {
            Diagnostics.Trace(WorkIqTraceStage.Auth, "auth.sign_in_observer_failed", $"the turn's sign-in listener threw {ex.GetType().Name} at the end of the sign-in", WorkIqTraceLevel.Warning);
        }
    }

    /// <summary>
    /// The device-code message with the user code replaced by <see cref="WorkIqRedaction.RedactedMarker"/> (the page
    /// address and the rest of the sentence kept); the message unchanged when no code is known or it does not appear.
    /// </summary>
    /// <param name="message">MSAL's device-code message.</param>
    /// <param name="userCode">MSAL's user code.</param>
    internal static string MaskUserCode(string? message, string? userCode)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message ?? "";
        }

        return string.IsNullOrWhiteSpace(userCode)
            ? message
            : message.Replace(userCode.Trim(), WorkIqRedaction.RedactedMarker, StringComparison.Ordinal);
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

    // The provider traces a failure and wraps it; the auth handler that asked for the token registers its one issue,
    // classified from the failure, whose safe message carries the AADSTS code the catalog describes.
    private WorkIqProtocolException Wrap(MsalServiceException ex, string phase, long started)
    {
        var aadsts = WorkIqAadstsCatalog.ExtractCode(ex.Message);
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
}
