// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365.Auth;
using AgentEval.Microsoft365.Diagnostics;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Auth;

/// <summary>
/// <see cref="WorkIqDeviceCodeTokenProvider"/>: the offline-testable surface (construction without network, the
/// deterministic cache file name, the AADSTS/consent classification and the username mask). The MSAL device-code
/// and silent paths need a real Entra app registration and are not exercised here.
/// </summary>
public sealed class WorkIqDeviceCodeTokenProviderTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string App = "22222222-2222-2222-2222-222222222222";
    private const string Scope = "api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask";

    [Fact]
    public void Constructor_MakesNoNetworkCall_AndNoTokenInfo()
    {
        using var prompt = new StringWriter();

        var provider = new WorkIqDeviceCodeTokenProvider(Tenant, App, Scope, prompt);

        Assert.Null(provider.LastTokenInfo);
        Assert.Equal(Tenant, provider.TenantId);
        Assert.Equal(App, provider.AppClientId);
        Assert.Equal(Scope, provider.Scope);
        Assert.Same(prompt, provider.DeviceCodePrompt);
        Assert.Same(WorkIqDiagnostics.None, provider.Diagnostics);
        Assert.Equal("", prompt.ToString());
    }

    [Fact]
    public void Constructor_DefaultPrompt_IsStandardError()
    {
        var provider = new WorkIqDeviceCodeTokenProvider(Tenant, App, Scope);

        Assert.Same(Console.Error, provider.DeviceCodePrompt);
    }

    [Theory]
    [InlineData("", App, Scope)]
    [InlineData(Tenant, " ", Scope)]
    [InlineData(Tenant, App, "")]
    public void Constructor_BlankArgument_Throws(string tenant, string app, string scope)
    {
        Assert.Throws<ArgumentException>(() => new WorkIqDeviceCodeTokenProvider(tenant, app, scope));
    }

    [Fact]
    public void ComputeCacheFileName_DeterministicPerTenantAppAndDistinctFromCopilotStudio()
    {
        var first = WorkIqDeviceCodeTokenProvider.ComputeCacheFileName(Tenant, App);
        var again = WorkIqDeviceCodeTokenProvider.ComputeCacheFileName(Tenant, App);
        var otherApp = WorkIqDeviceCodeTokenProvider.ComputeCacheFileName(Tenant, "33333333-3333-3333-3333-333333333333");
        var swapped = WorkIqDeviceCodeTokenProvider.ComputeCacheFileName(App, Tenant);

        Assert.Equal(first, again);
        Assert.NotEqual(first, otherApp);
        Assert.NotEqual(first, swapped);
        Assert.StartsWith("workiq_", first, StringComparison.Ordinal);
        Assert.EndsWith(".msalcache.bin", first, StringComparison.Ordinal);
        Assert.Equal("workiq_".Length + 64 + ".msalcache.bin".Length, first.Length);
        Assert.Equal(first, first.ToLowerInvariant());
        Assert.DoesNotContain(Tenant, first, StringComparison.Ordinal);
        Assert.Equal(-1, first.IndexOfAny(Path.GetInvalidFileNameChars()));
    }

    [Fact]
    public void CacheFilePath_UnderLocalAppDataAgentEvalMsalCache()
    {
        var provider = new WorkIqDeviceCodeTokenProvider(Tenant, App, Scope, TextWriter.Null);

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentEval",
            "msal_cache",
            WorkIqDeviceCodeTokenProvider.ComputeCacheFileName(Tenant, App));
        Assert.Equal(expected, provider.CacheFilePath);
        Assert.Equal(Path.GetDirectoryName(expected), WorkIqDeviceCodeTokenProvider.CacheDirectory);
    }

    [Theory]
    [InlineData("jdoe@contoso.com", "j***@contoso.com")]
    [InlineData("  jdoe@contoso.com ", "j***@contoso.com")]
    [InlineData("jdoe", "j***")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void MaskUsername_KeepsFirstCharacterAndDomainOnly(string? username, string? expected)
    {
        Assert.Equal(expected, WorkIqDeviceCodeTokenProvider.MaskUsername(username));
    }

    [Theory]
    [InlineData("AADSTS65001", 400, true)]
    [InlineData("AADSTS65004", 400, true)]
    [InlineData("AADSTS90094", 400, true)]
    [InlineData("AADSTS53003", 400, true)]
    [InlineData("AADSTS70011", 403, true)]
    [InlineData("AADSTS70011", 400, false)]
    [InlineData(null, 401, false)]
    public void IsPermissionRefusal_ConsentCodesOr403(string? code, int status, bool expected)
    {
        Assert.Equal(expected, WorkIqDeviceCodeTokenProvider.IsPermissionRefusal(code, status));
    }

    [Fact]
    public void TokenInfo_CarriesNoTokenField()
    {
        var info = new WorkIqTokenInfo(DateTimeOffset.UnixEpoch, "tenant", ["scope"], "j***@contoso.com", true);

        Assert.DoesNotContain(typeof(WorkIqTokenInfo).GetProperties(), p => p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase) && p.PropertyType == typeof(string));
        Assert.True(info.AcquiredSilently);
    }
}
