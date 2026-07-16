// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.CopilotStudio;
using Xunit;

namespace AgentEval.Tests.Cli.CopilotStudio;

/// <summary>
/// Tests for the offline-testable slice of <c>CopilotStudioTokenProvider</c>: construction/argument validation and
/// <see cref="CopilotStudioTokenProvider.ComputeCacheFileName"/>, the pure function behind the persisted token
/// cache's file name. The MSAL device-code/silent-acquisition flow itself (<c>GetTokenAsync</c>) is NOT exercised
/// here — it requires a real Entra app registration and a human completing an interactive device-code sign-in, so
/// it cannot run in CI. See the class's own XML doc remarks and the CHANGELOG entry for what a pre-production
/// credentialed smoke test must still confirm.
/// </summary>
public class CopilotStudioTokenProviderTests
{
    [Fact]
    public void ComputeCacheFileName_IsDeterministic()
    {
        var first = CopilotStudioTokenProvider.ComputeCacheFileName("tenant-a", "app-1");
        var second = CopilotStudioTokenProvider.ComputeCacheFileName("tenant-a", "app-1");
        Assert.Equal(first, second);
    }

    [Fact]
    public void ComputeCacheFileName_DiffersByTenant()
    {
        var a = CopilotStudioTokenProvider.ComputeCacheFileName("tenant-a", "app-1");
        var b = CopilotStudioTokenProvider.ComputeCacheFileName("tenant-b", "app-1");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeCacheFileName_DiffersByAppClientId()
    {
        var a = CopilotStudioTokenProvider.ComputeCacheFileName("tenant-a", "app-1");
        var b = CopilotStudioTokenProvider.ComputeCacheFileName("tenant-a", "app-2");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeCacheFileName_DoesNotCollideAcrossTheSeparator()
    {
        // "tenant-a|pp-1" vs "tenant-ap|p-1" would collide under naive string concatenation without a separator
        // that can't appear in either input in a confusable way — the '|' delimiter plus SHA-256 over the whole
        // string is enough here since we only need "different inputs get different file names", not a security
        // boundary. This asserts the obvious near-miss pair doesn't collide in practice.
        var a = CopilotStudioTokenProvider.ComputeCacheFileName("tenant-a", "pp-1");
        var b = CopilotStudioTokenProvider.ComputeCacheFileName("tenant-ap", "p-1");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeCacheFileName_IsFilesystemSafe()
    {
        var name = CopilotStudioTokenProvider.ComputeCacheFileName("weird/tenant\\id:with*chars?", "app<>|\"");
        Assert.All(name, c => Assert.DoesNotContain(c, System.IO.Path.GetInvalidFileNameChars()));
        Assert.EndsWith(".msalcache.bin", name);
        Assert.StartsWith("copilotstudio_", name);
    }

    [Theory]
    [InlineData(null, "app", "scope")]
    [InlineData("", "app", "scope")]
    [InlineData("tenant", null, "scope")]
    [InlineData("tenant", "", "scope")]
    [InlineData("tenant", "app", null)]
    [InlineData("tenant", "app", "")]
    public void Constructor_RejectsMissingRequiredArgs(string? tenantId, string? appClientId, string? scope)
    {
        Assert.ThrowsAny<ArgumentException>(() => new CopilotStudioTokenProvider(tenantId!, appClientId!, scope!));
    }

    [Fact]
    public void Constructor_ValidArgs_DoesNotThrow_AndMakesNoNetworkCall()
    {
        // Constructing the provider must not itself build the MSAL IPublicClientApplication (that's deferred to
        // the first GetTokenAsync call) — otherwise even BuildLive's credential-free construction path would
        // become network-dependent. This just proves construction succeeds; EnsureAppAsync/GetTokenAsync are
        // exercised only by a live, credentialed, manual test (see CopilotStudioLiveConnectorManualTests).
        var provider = new CopilotStudioTokenProvider("tenant-abc", "app-xyz", "https://example.powerplatform.com/.default");
        Assert.NotNull(provider);
    }
}
