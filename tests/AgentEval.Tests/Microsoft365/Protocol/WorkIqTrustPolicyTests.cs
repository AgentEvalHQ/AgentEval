// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using A2A;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Protocol;
using AgentEval.Tests.Microsoft365.TestHelpers;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Protocol;

/// <summary><see cref="WorkIqTrustPolicy"/>: where a token may go and which card interface is selected.</summary>
public sealed class WorkIqTrustPolicyTests
{
    private static readonly Uri CardUri = new("https://workiq.svc.cloud.microsoft/a2a/.well-known/agent-card.json");

    private static WorkIqTrustPolicy Policy(params string[] hosts) => new(hosts.Length == 0 ? [Microsoft365Config.DefaultTrustedHost] : hosts);

    private static AgentCard Card(string fixture) =>
        JsonSerializer.Deserialize<AgentCard>(FixtureLoader.Text(fixture), A2AJsonUtilities.DefaultOptions)!;

    [Fact]
    public void Constructor_NoHosts_Throws()
    {
        Assert.Throws<ArgumentException>(() => new WorkIqTrustPolicy(Array.Empty<string>()));
        Assert.Throws<ArgumentNullException>(() => new WorkIqTrustPolicy(null!));
    }

    [Theory]
    [InlineData("https://workiq.svc.cloud.microsoft/a2a/", true)]
    [InlineData("https://WORKIQ.svc.cloud.microsoft/a2a/", true)]
    [InlineData("http://workiq.svc.cloud.microsoft/a2a/", false)]
    [InlineData("https://example.invalid/a2a/", false)]
    [InlineData("https://workiq.svc.cloud.microsoft.example.invalid/a2a/", false)]
    public void IsTrusted_HttpsAndListedHostOnly(string url, bool expected)
    {
        Assert.Equal(expected, Policy().IsTrusted(new Uri(url)));
    }

    [Fact]
    public void IsTrustedHost_TrimsAndIgnoresCase()
    {
        var policy = Policy(" Workiq.svc.cloud.microsoft ");
        Assert.True(policy.IsTrustedHost("workiq.svc.cloud.microsoft"));
        Assert.False(policy.IsTrustedHost(""));
        Assert.False(policy.IsTrustedHost(null));
    }

    [Fact]
    public void SelectInterface_DefaultCard_PicksJsonRpc10NotHttpJson()
    {
        var selected = Policy().SelectInterface(Card("cards/default-card-two-interfaces.json"), CardUri);

        Assert.Equal("JSONRPC", selected.ProtocolBinding);
        Assert.Equal("1.0", selected.ProtocolVersion);
        Assert.Equal("https://workiq.svc.cloud.microsoft/a2a/", selected.Url);
    }

    [Fact]
    public void SelectInterface_HttpJsonFirst_StillPicksJsonRpc()
    {
        var card = Card("cards/default-card-two-interfaces.json");
        card.SupportedInterfaces.Reverse();

        var selected = Policy().SelectInterface(card, CardUri);

        Assert.Equal("JSONRPC", selected.ProtocolBinding);
    }

    [Fact]
    public void SelectInterface_UntrustedHost_RefusesWithUntrustedHost()
    {
        var ex = Assert.Throws<WorkIqProtocolException>(() => Policy().SelectInterface(Card("cards/card-untrusted-host.json"), CardUri));

        Assert.Equal(WorkIqFailureCategory.UntrustedHost, ex.Failure.Category);
        Assert.Equal(OutcomeCertainty.Known, ex.Failure.Certainty);
        Assert.Equal(WorkIqOperation.Discover, ex.Failure.Operation);
        Assert.Contains("example.invalid", ex.Failure.SafeMessage, StringComparison.Ordinal);
        Assert.Contains("no token was sent", ex.Failure.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectInterface_V03Only_IsVersionMismatchNotDowngrade()
    {
        var ex = Assert.Throws<WorkIqProtocolException>(() => Policy().SelectInterface(Card("cards/card-v03-only.json"), CardUri));

        Assert.Equal(WorkIqFailureCategory.ProtocolVersionMismatch, ex.Failure.Category);
        Assert.Contains("JSONRPC/0.3@workiq.svc.cloud.microsoft", ex.Failure.SafeMessage, StringComparison.Ordinal);
        Assert.Contains("never downgrades", ex.Failure.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectInterface_OnlyHttpJson_IsVersionMismatch()
    {
        var card = Card("cards/default-card-two-interfaces.json");
        card.SupportedInterfaces.RemoveAll(i => i.ProtocolBinding == "JSONRPC");

        var ex = Assert.Throws<WorkIqProtocolException>(() => Policy().SelectInterface(card, CardUri));

        Assert.Equal(WorkIqFailureCategory.ProtocolVersionMismatch, ex.Failure.Category);
    }

    [Fact]
    public void SelectInterface_TrustedAfterUntrusted_PicksTheTrustedOne()
    {
        var card = Card("cards/card-untrusted-host.json");
        card.SupportedInterfaces.Add(new AgentInterface { Url = "https://workiq.svc.cloud.microsoft/a2a/", ProtocolBinding = "JSONRPC", ProtocolVersion = "1.0" });

        var selected = Policy().SelectInterface(card, CardUri);

        Assert.Equal("https://workiq.svc.cloud.microsoft/a2a/", selected.Url);
    }

    [Fact]
    public void SelectInterface_AgentSpecificCard_KeepsOpaqueIdAndTenant()
    {
        var selected = Policy().SelectInterface(Card("cards/agent-specific-card.json"), CardUri);

        Assert.Equal("https://workiq.svc.cloud.microsoft/a2a/U_abc123.def456/", selected.Url);
        Assert.Equal("rt-8d3f2a1c", selected.Tenant);
    }

    [Theory]
    [InlineData("1.0", true)]
    [InlineData(" 1.0 ", true)]
    [InlineData("1.0.1", true)]
    [InlineData("1.01", false)]
    [InlineData("1.1", false)]
    [InlineData("0.3", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsRequiredVersion_OnlyOneDotZero(string? version, bool expected)
    {
        Assert.Equal(expected, WorkIqTrustPolicy.IsRequiredVersion(version));
    }

    [Theory]
    [InlineData("JSONRPC", true)]
    [InlineData("jsonrpc", true)]
    [InlineData("HTTP+JSON", false)]
    [InlineData("GRPC", false)]
    [InlineData(null, false)]
    public void IsRequiredBinding_JsonRpcOnly(string? binding, bool expected)
    {
        Assert.Equal(expected, WorkIqTrustPolicy.IsRequiredBinding(binding));
        Assert.Equal(ProtocolBindingNames.JsonRpc, WorkIqTrustPolicy.RequiredBinding);
    }
}
