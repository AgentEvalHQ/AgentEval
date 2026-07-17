// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace AgentEval.MAF.CopilotStudio;

/// <summary>
/// Acquires Entra ID access tokens for the Copilot Studio Direct-to-Engine API using MSAL's public-client
/// device-code flow, with a persisted, OS-encrypted token cache so a re-run against the same tenant + app
/// registration doesn't re-prompt every time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why device-code, not a <c>TokenCredential</c> overload:</b> <c>Microsoft.Agents.CopilotStudio.Client</c>'s
/// <c>CopilotClient</c> has no <c>Azure.Core.TokenCredential</c> constructor overload — it is "bring an
/// already-acquired token": a <c>Func&lt;string, Task&lt;string&gt;&gt;</c> the client calls on every request and
/// uses as a Bearer header. <see cref="GetTokenAsync"/> is built to be passed directly as that callback.
/// </para>
/// <para><b>Flow, on every call to <see cref="GetTokenAsync"/>:</b></para>
/// <list type="number">
/// <item>Lazily build (once, memoized) an <see cref="IPublicClientApplication"/> for the configured tenant + app
/// client id, with its user token cache registered against a persisted, OS-encrypted store via
/// <c>Microsoft.Identity.Client.Extensions.Msal</c> — DPAPI on Windows, Keychain on macOS, libsecret on Linux
/// (falling back to an unencrypted file only where none of those are available, matching MSAL's own published
/// samples for this package).</item>
/// <item>If a cached account exists, try <c>AcquireTokenSilent</c>. MSAL itself handles silent refresh-token
/// renewal, so this succeeds on every run after the first, as long as the refresh token is still valid.</item>
/// <item>On <see cref="MsalUiRequiredException"/> (no cached account, or silent renewal failed), fall back to
/// <c>AcquireTokenWithDeviceCode</c> — prints the user code + verification URL and blocks until the user completes
/// sign-in in a browser (or the device code expires).</item>
/// </list>
/// <para>
/// <b>Cache key.</b> The persisted cache file name is a SHA-256 hash of <c>TenantId|AppClientId</c> (not the raw
/// ids — they aren't secret, but hashing sidesteps any filesystem-invalid-character concerns entirely) under
/// <c>%LOCALAPPDATA%/AgentEval/msal_cache/</c> (or the platform equivalent
/// <see cref="Environment.SpecialFolder.LocalApplicationData"/> resolves to). Scanning against a different
/// tenant/app pair gets its own cache file rather than colliding with — or being invalidated by — another.
/// </para>
/// <para>
/// <b>NOT independently live-verified.</b> The device-code prompt, the silent-refresh path, and the persisted-cache
/// round-trip all require a real Entra app registration plus a human completing the device-code sign-in in a
/// browser — none of which is available in this environment. What <b>is</b> verified: this compiles against the
/// real MSAL 4.84.2 + Microsoft.Identity.Client.Extensions.Msal 4.84.2 API surface, and constructs the
/// app/cache/scopes exactly as MSAL's own documentation and samples prescribe for a public-client desktop/CLI app.
/// <see cref="ComputeCacheFileName"/> (the one pure, offline-testable piece of this flow) has unit tests; the
/// device-code/silent-acquisition flow itself needs a real credentialed smoke test before this is trusted in
/// production — see the CHANGELOG entry for the full list of what that smoke test must cover.
/// </para>
/// </remarks>
public sealed class CopilotStudioTokenProvider
{
    private readonly string _tenantId;
    private readonly string _appClientId;
    private readonly string _scope;
    private readonly TextWriter _deviceCodePrompt;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private IPublicClientApplication? _app;

    /// <param name="tenantId">The Entra tenant id (from <see cref="CopilotStudioConfig.TenantId"/>).</param>
    /// <param name="appClientId">The Entra app-registration client id (from <see cref="CopilotStudioConfig.AppClientId"/>).</param>
    /// <param name="scope">The token audience scope — pass <c>CopilotClient.ScopeFromSettings(settings)</c>, never hardcode it.</param>
    /// <param name="deviceCodePrompt">Where the device-code sign-in instructions are written. Defaults to <see cref="Console.Error"/>.</param>
    public CopilotStudioTokenProvider(string tenantId, string appClientId, string scope, TextWriter? deviceCodePrompt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appClientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        _tenantId = tenantId;
        _appClientId = appClientId;
        _scope = scope;
        _deviceCodePrompt = deviceCodePrompt ?? Console.Error;
    }

    /// <summary>
    /// The <c>Func&lt;string, Task&lt;string&gt;&gt;</c> callback <c>CopilotClient</c>'s constructor expects.
    /// <paramref name="requestUri"/> (the URI the SDK is about to call) is accepted per that contract's shape but
    /// unused here — the scope, not the specific request URI, determines which token to hand back.
    /// </summary>
    public async Task<string> GetTokenAsync(string requestUri)
    {
        _ = requestUri;
        var app = await EnsureAppAsync().ConfigureAwait(false);
        var scopes = new[] { _scope };

        var accounts = await app.GetAccountsAsync().ConfigureAwait(false);
        var account = accounts.FirstOrDefault();
        if (account is not null)
        {
            try
            {
                var silent = await app.AcquireTokenSilent(scopes, account).ExecuteAsync().ConfigureAwait(false);
                return silent.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                // Cached token/refresh token unusable — fall through to an interactive device-code sign-in.
            }
        }

        var result = await app.AcquireTokenWithDeviceCode(scopes, ShowDeviceCodePrompt).ExecuteAsync().ConfigureAwait(false);
        return result.AccessToken;
    }

    private Task ShowDeviceCodePrompt(DeviceCodeResult deviceCode)
    {
        _deviceCodePrompt.WriteLine();
        _deviceCodePrompt.WriteLine("  Copilot Studio sign-in required:");
        _deviceCodePrompt.WriteLine($"    {deviceCode.Message}");
        _deviceCodePrompt.WriteLine();
        return Task.CompletedTask;
    }

    private async Task<IPublicClientApplication> EnsureAppAsync()
    {
        if (_app is not null)
        {
            return _app;
        }

        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_app is not null)
            {
                return _app;
            }

            var app = PublicClientApplicationBuilder
                .Create(_appClientId)
                .WithTenantId(_tenantId)
                .Build();

            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentEval", "msal_cache");
            var cacheFileName = ComputeCacheFileName(_tenantId, _appClientId);
            var storageProperties = new StorageCreationPropertiesBuilder(cacheFileName, cacheDir)
                .WithMacKeyChain("com.agenteval.cli.copilotstudio", "AgentEvalCopilotStudioMsalCache")
                .WithLinuxKeyring(
                    "com.agenteval.cli.copilotstudio.tokencache",
                    "default",
                    "AgentEval Copilot Studio MSAL token cache",
                    new KeyValuePair<string, string>("Product", "AgentEval"),
                    new KeyValuePair<string, string>("Component", "CopilotStudio"))
                .Build();

            var cacheHelper = await MsalCacheHelper.CreateAsync(storageProperties).ConfigureAwait(false);
            cacheHelper.RegisterCache(app.UserTokenCache);

            _app = app;
            return app;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Deterministic, filesystem-safe cache file name for a (tenantId, appClientId) pair — a SHA-256 hash so the
    /// name is stable across runs, unique per tenant+app, and never depends on either id containing only
    /// filesystem-legal characters.
    /// </summary>
    internal static string ComputeCacheFileName(string tenantId, string appClientId)
    {
        var bytes = Encoding.UTF8.GetBytes($"{tenantId}|{appClientId}");
        // Convert.ToHexStringLower is .NET 9+ only; this project also targets net8.0, so lower-case explicitly.
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return $"copilotstudio_{hash}.msalcache.bin";
    }
}
