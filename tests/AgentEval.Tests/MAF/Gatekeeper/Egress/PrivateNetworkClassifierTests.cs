// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using AgentEval.MAF.Gatekeeper.Egress;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper.Egress;

/// <summary>Gatekeeper Hardening Phase 2, #10 — the SSRF/DNS-rebind address classifier.</summary>
public class PrivateNetworkClassifierTests
{
    [Theory]
    [InlineData("127.0.0.1")]        // loopback
    [InlineData("127.255.255.254")]  // loopback range, not just .1
    [InlineData("10.0.0.1")]         // RFC1918
    [InlineData("172.16.0.1")]       // RFC1918
    [InlineData("172.31.255.255")]   // RFC1918 upper bound
    [InlineData("192.168.1.1")]      // RFC1918
    [InlineData("169.254.169.254")]  // link-local — the cloud-metadata well-known address
    [InlineData("169.254.1.1")]      // link-local generally
    [InlineData("100.64.0.1")]       // CGNAT
    [InlineData("0.0.0.0")]          // unspecified / "this network"
    [InlineData("255.255.255.255")]  // broadcast
    [InlineData("192.0.2.1")]        // TEST-NET-1
    [InlineData("198.51.100.1")]     // TEST-NET-2
    [InlineData("203.0.113.1")]      // TEST-NET-3
    [InlineData("224.0.0.251")]      // multicast (mDNS) — regression test: this method's own doc already
    [InlineData("239.255.255.250")]  // multicast (SSDP) — promised multicast is covered, IPv4 silently wasn't
    public void IPv4_PrivateOrReserved_ReturnsTrue(string ip)
        => Assert.True(PrivateNetworkClassifier.IsPrivateOrReserved(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("8.8.8.8")]          // public (Google DNS)
    [InlineData("1.1.1.1")]          // public (Cloudflare DNS)
    [InlineData("172.15.255.255")]   // just below the RFC1918 172.16/12 range
    [InlineData("172.32.0.0")]       // just above the RFC1918 172.16/12 range
    [InlineData("11.0.0.1")]         // just above 10/8
    [InlineData("223.255.255.255")]  // just below the multicast 224.0.0.0/4 range
    public void IPv4_Public_ReturnsFalse(string ip)
        => Assert.False(PrivateNetworkClassifier.IsPrivateOrReserved(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("::1")]              // loopback
    [InlineData("fe80::1")]          // link-local
    [InlineData("fc00::1")]          // unique-local (RFC4193)
    [InlineData("fd12:3456:789a::1")] // unique-local
    [InlineData("ff02::1")]          // multicast
    [InlineData("::")]               // unspecified
    public void IPv6_PrivateOrReserved_ReturnsTrue(string ip)
        => Assert.True(PrivateNetworkClassifier.IsPrivateOrReserved(IPAddress.Parse(ip)));

    [Fact]
    public void IPv6_Public_ReturnsFalse()
        => Assert.False(PrivateNetworkClassifier.IsPrivateOrReserved(IPAddress.Parse("2001:4860:4860::8888")));   // Google public DNS

    [Fact]
    public void IPv4MappedIPv6_ClassifiesByEmbeddedIPv4()
    {
        // ::ffff:127.0.0.1 — a loopback address wrapped in IPv4-mapped IPv6 notation. Must classify by the
        // EMBEDDED address, not treat the wrapper as some ordinary public-looking IPv6 address.
        var mapped = IPAddress.Parse("::ffff:127.0.0.1");
        Assert.True(PrivateNetworkClassifier.IsPrivateOrReserved(mapped));
    }

    [Fact]
    public void NullAddress_Throws()
        => Assert.Throws<ArgumentNullException>(() => PrivateNetworkClassifier.IsPrivateOrReserved(null!));
}
