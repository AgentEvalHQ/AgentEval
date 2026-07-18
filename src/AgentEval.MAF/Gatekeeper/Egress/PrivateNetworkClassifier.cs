// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using System.Net.Sockets;

namespace AgentEval.MAF.Gatekeeper.Egress;

/// <summary>
/// #10 (Gatekeeper Hardening Phase 2) — classifies an <see cref="IPAddress"/> as private/loopback/link-local/
/// reserved (an SSRF-relevant "not really the public internet" address), the check <see cref="GatekeeperHttpMessageHandler"/>
/// runs against every DNS-resolved address before allowing a connection. Deliberately conservative (a
/// false-positive here just refuses a legitimate internal target unless the caller explicitly opts in via
/// <see cref="GatekeeperHttpEgressOptions.BlockPrivateNetworks"/> = <see langword="false"/>; a false-negative
/// would let an SSRF/DNS-rebind attack through, which is the worse failure mode — fail-closed).
/// </summary>
public static class PrivateNetworkClassifier
{
    /// <summary>
    /// True when <paramref name="address"/> is loopback, link-local, a private (RFC1918 / IPv6 unique-local)
    /// range, unspecified (<c>0.0.0.0</c> / <c>::</c>), or multicast — the ranges an outbound request from an
    /// agent's tool should not be allowed to reach unless the caller explicitly permits it.
    /// </summary>
    public static bool IsPrivateOrReserved(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
        {
            return true;
        }

        if (Equals(address, IPAddress.Any) || Equals(address, IPAddress.IPv6Any))
        {
            return true;   // unspecified — not a valid connection target, and never a legitimate DNS answer
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // IPv4-mapped IPv6 (::ffff:a.b.c.d) — classify by the embedded IPv4 address, not the wrapper.
            if (address.IsIPv4MappedToIPv6)
            {
                return IsPrivateOrReserved(address.MapToIPv4());
            }

            return IsUniqueLocalIPv6(address);
        }

        return IsPrivateOrReservedIPv4(address);
    }

    // RFC1918 (10/8, 172.16/12, 192.168/16) + link-local (169.254/16, covers the cloud-metadata well-known
    // 169.254.169.254) + CGNAT (100.64/10) + "this network" (0/8) + IETF protocol assignments / reserved
    // (192.0.0/24, 192.0.2/24 TEST-NET-1, 198.18/15 benchmarking, 198.51.100/24 TEST-NET-2, 203.0.113/24
    // TEST-NET-3, 240/4 reserved) + broadcast (255.255.255.255).
    private static bool IsPrivateOrReservedIPv4(IPAddress address)
    {
        var b = address.GetAddressBytes();
        if (b.Length != 4)
        {
            return true;   // not a plain IPv4 address we can classify — fail closed
        }

        return b[0] switch
        {
            0 => true,                                    // 0.0.0.0/8
            10 => true,                                    // 10.0.0.0/8
            100 => b[1] >= 64 && b[1] <= 127,               // 100.64.0.0/10 (CGNAT)
            127 => true,                                    // 127.0.0.0/8 (loopback range, beyond just 127.0.0.1)
            169 => b[1] == 254,                             // 169.254.0.0/16 (link-local; covers 169.254.169.254)
            172 => b[1] >= 16 && b[1] <= 31,                 // 172.16.0.0/12
            192 => (b[1] == 168) || (b[1] == 0 && b[2] == 0) || (b[1] == 0 && b[2] == 2),   // 192.168/16, 192.0.0/24, 192.0.2/24
            198 => (b[1] == 18 || b[1] == 19) || (b[1] == 51 && b[2] == 100),               // 198.18/15, 198.51.100/24
            203 => b[1] == 0 && b[2] == 113,                 // 203.0.113/24
            >= 224 and <= 239 => true,                       // 224.0.0.0/4 multicast (this method's own doc promises it)
            >= 240 => true,                                  // 240.0.0.0/4 reserved + 255.255.255.255 broadcast
            _ => false,
        };
    }

    // fc00::/7 (unique local) — the IPv6 analogue of RFC1918. (Link-local fe80::/10 and multicast are already
    // covered by IsIPv6LinkLocal/IsIPv6Multicast above.)
    private static bool IsUniqueLocalIPv6(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b.Length == 16 && (b[0] & 0xFE) == 0xFC;
    }
}
