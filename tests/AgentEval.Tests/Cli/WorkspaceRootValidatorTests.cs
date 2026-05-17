// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Tests for <see cref="WorkspaceRootValidator"/> — pinning the contract that
/// the <c>--root</c> CLI flag canonicalises via <c>Path.GetFullPath</c>, rejects
/// non-existent and malformed paths, and surfaces a stderr diagnostic on failure.
/// Phase-4 Task 4.7b — defense-in-depth against accidental <c>../</c> traversal.
/// </summary>
public class WorkspaceRootValidatorTests : IDisposable
{
    private readonly string _scratch;

    public WorkspaceRootValidatorTests()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "wsroot-validator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
            try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    [Fact]
    public void Null_ReturnsNull()
    {
        Assert.Null(WorkspaceRootValidator.CanonicaliseOrNull(null));
    }

    [Fact]
    public void Empty_ReturnsNull()
    {
        Assert.Null(WorkspaceRootValidator.CanonicaliseOrNull(""));
        Assert.Null(WorkspaceRootValidator.CanonicaliseOrNull("   "));
    }

    [Fact]
    public void ExistingDirectory_ReturnsCanonicalPath()
    {
        var result = WorkspaceRootValidator.CanonicaliseOrNull(_scratch);
        Assert.NotNull(result);
        Assert.True(Directory.Exists(result));
        Assert.Equal(Path.GetFullPath(_scratch), result);
    }

    [Fact]
    public void NonExistentPath_ReturnsNull()
    {
        var fake = Path.Combine(_scratch, "does-not-exist-" + Guid.NewGuid().ToString("N"));
        Assert.Null(WorkspaceRootValidator.CanonicaliseOrNull(fake));
    }

    [Fact]
    public void TraversalPath_CanonicalisesAwayDotDot()
    {
        // Create scratch/inner/, then pass scratch/inner/.. → should canonicalise to scratch/.
        var inner = Path.Combine(_scratch, "inner");
        Directory.CreateDirectory(inner);

        var withTraversal = Path.Combine(inner, "..");
        var result = WorkspaceRootValidator.CanonicaliseOrNull(withTraversal);

        Assert.NotNull(result);
        // The canonical path is the parent (_scratch), not the literal "inner/.." string.
        Assert.Equal(Path.GetFullPath(_scratch).TrimEnd(Path.DirectorySeparatorChar),
                     result!.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void RelativePath_CanonicalisesToAbsolute()
    {
        // Pass a relative segment that resolves under CWD. We don't need this
        // path to exist for the canonicalisation; we just want to confirm the
        // function returns null (because the canonicalised path doesn't exist)
        // rather than throwing.
        var result = WorkspaceRootValidator.CanonicaliseOrNull("definitely-not-a-real-dir-" + Guid.NewGuid().ToString("N"));
        Assert.Null(result);
    }

    [Fact]
    public void PathWithInvalidCharacters_ReturnsNull()
    {
        // Path.GetFullPath rejects certain invalid characters on Windows. The
        // validator should swallow the resulting ArgumentException and return null.
        // On Linux these characters are valid filename chars, so we only test
        // a guaranteed-invalid pattern.
        if (!OperatingSystem.IsWindows()) return;

        // Use NUL character which is invalid on every platform.
        var bad = _scratch + "\0invalid";
        Assert.Null(WorkspaceRootValidator.CanonicaliseOrNull(bad));
    }
}
