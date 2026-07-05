// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using AgentEval.Assertions;

namespace AgentEval.Tests.Assertions;

/// <summary>Glass Box Phase 3 (P3.3-pre) — the ReDoS-safe, fail-closed forbidden-content scan.</summary>
public class ForbiddenPatternScannerTests
{
    private static readonly Regex Secret = new("(?i)secret", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    [Fact]
    public void Match_ReturnsTrue()
        => Assert.True(ForbiddenPatternScanner.ScanForForbiddenPattern("my SECRET key", Secret));

    [Fact]
    public void NoMatch_ReturnsFalse()
        => Assert.False(ForbiddenPatternScanner.ScanForForbiddenPattern("nothing here", Secret));

    [Fact]
    public void NullText_ReturnsFalse()
        => Assert.False(ForbiddenPatternScanner.ScanForForbiddenPattern(null, Secret));

    [Fact]
    public void MatchEmptyPattern_OnNullOrEmptyText_ReturnsFalse()
    {
        // A permissive regex that matches the empty string must NOT report a hit on absent/empty text
        // (nothing to scan == clean).
        var matchAll = new Regex(".*", RegexOptions.None, TimeSpan.FromSeconds(1));
        Assert.False(ForbiddenPatternScanner.ScanForForbiddenPattern(null, matchAll));
        Assert.False(ForbiddenPatternScanner.ScanForForbiddenPattern("", matchAll));
    }

    [Fact]
    public void UnboundedRegex_Throws()
    {
        // A pattern with no MatchTimeout can't be ReDoS-safe — the helper must reject it.
        var noTimeout = new Regex("secret"); // Regex.InfiniteMatchTimeout
        Assert.Throws<ArgumentException>(() => ForbiddenPatternScanner.ScanForForbiddenPattern("x", noTimeout));
    }

    [Fact]
    public void Timeout_FailsClosed_ReturnsTrue()
    {
        // A catastrophic-backtracking pattern with a tiny timeout must fail closed (treated as a hit).
        var evil = new Regex("(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(1));
        var attack = new string('a', 5000) + "!";

        Assert.True(ForbiddenPatternScanner.ScanForForbiddenPattern(attack, evil));
    }
}
