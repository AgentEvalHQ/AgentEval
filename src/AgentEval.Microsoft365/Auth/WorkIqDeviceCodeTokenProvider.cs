// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;

namespace AgentEval.Microsoft365.Auth;

/// <summary>
/// The default <see cref="IWorkIqTokenProvider"/>: an MSAL public client for the configured tenant + app
/// registration, silent acquisition first, device-code sign-in (instructions written to
/// <see cref="DeviceCodePrompt"/>) when no usable cached account exists, and a persisted, OS-encrypted token cache
/// (DPAPI / Keychain / libsecret; never an unprotected file) keyed by <see cref="ComputeCacheFileName"/> under
/// <c>%LOCALAPPDATA%/AgentEval/msal_cache/</c>. Mirrors the Copilot Studio provider so the two can later share
/// one helper. Constructing it makes no network call; the first <see cref="GetAccessTokenAsync"/> may prompt.
/// Not independently live-verified: the device-code and silent-refresh paths need a real Entra app registration.
/// </summary>
public sealed class WorkIqDeviceCodeTokenProvider : IWorkIqTokenProvider
{
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

    /// <inheritdoc/>
    public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
        throw new NotImplementedException("TODO(M11)");

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
}
