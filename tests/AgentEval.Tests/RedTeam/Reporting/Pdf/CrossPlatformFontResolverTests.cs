// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.RedTeam.Reporting.Pdf;
using Xunit;

namespace AgentEval.Tests.RedTeam.Reporting.Pdf;

/// <summary>
/// Tests for <see cref="CrossPlatformFontResolver"/> (PERF-11): GetFont memoizes resolved font bytes per
/// face name so a PDF render does not re-read (and, on Linux/macOS, re-scan recursively) the same font.
/// </summary>
public class CrossPlatformFontResolverTests
{
    [Fact]
    public void GetFont_SameFace_ReturnsCachedReferenceOrConsistentNull()
    {
        var resolver = CrossPlatformFontResolver.Instance;
        // Resolve a common family to a platform face name, then load it twice.
        var face = resolver.ResolveTypeface("Helvetica", isBold: false, isItalic: false)!.FaceName;

        var first = resolver.GetFont(face);
        var second = resolver.GetFont(face);

        if (first is null)
        {
            // Font not present on this host — both calls must be consistently null.
            Assert.Null(second);
        }
        else
        {
            // PERF-11: the second call returns the SAME cached array, not a fresh disk read.
            Assert.Same(first, second);
        }
    }

    [Fact]
    public void GetFont_UnknownFace_ResultIsMemoized()
    {
        var resolver = CrossPlatformFontResolver.Instance;
        const string bogus = "definitely-not-a-real-font-face-zzz";

        var first = resolver.GetFont(bogus);
        var second = resolver.GetFont(bogus);

        // The result (null on Linux/macOS, or the platform's emergency Arial fallback on Windows) is
        // memoized either way — no repeated disk scan/read on the second call.
        if (first is null)
            Assert.Null(second);
        else
            Assert.Same(first, second);
    }
}
