// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;

namespace AgentEval.Tests.Output;

public class WorkspaceRootDiscoveryTests
{
    [Fact]
    public void Find_FromNestedDir_ReturnsSlnDir()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            // .sln at level 2; call from level 5
            var slnDir = Path.Combine(root, "level1", "level2");
            Directory.CreateDirectory(slnDir);
            File.WriteAllText(Path.Combine(slnDir, "MySolution.sln"), "");

            var deepDir = Path.Combine(slnDir, "level3", "level4", "level5");
            Directory.CreateDirectory(deepDir);

            var result = WorkspaceRootDiscovery.Find(deepDir);

            Assert.Equal(slnDir, result);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Find_WithGitOnly_ReturnsGitParent()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, ".git"));

            var result = WorkspaceRootDiscovery.Find(root);

            Assert.Equal(root, result);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // MNT-03: the shared canonicalise+exist helper used by both the CLI --root validator and the MC
    // --workspace startup path.
    [Fact]
    public void CanonicaliseExistingDirectory_Blank_IsOkWithNullCanonical()
    {
        var (canonical, status, _) = WorkspaceRootDiscovery.CanonicaliseExistingDirectory("   ");
        Assert.Equal(WorkspaceRootDiscovery.PathStatus.Ok, status);
        Assert.Null(canonical);
    }

    [Fact]
    public void CanonicaliseExistingDirectory_ExistingDir_IsOkCanonical()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var (canonical, status, _) = WorkspaceRootDiscovery.CanonicaliseExistingDirectory(dir);
            Assert.Equal(WorkspaceRootDiscovery.PathStatus.Ok, status);
            Assert.Equal(Path.GetFullPath(dir), canonical);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void CanonicaliseExistingDirectory_NonExistent_IsNotFound()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "nope");
        var (canonical, status, detail) = WorkspaceRootDiscovery.CanonicaliseExistingDirectory(missing);
        Assert.Equal(WorkspaceRootDiscovery.PathStatus.NotFound, status);
        Assert.Null(canonical);
        Assert.Equal(Path.GetFullPath(missing), detail); // detail carries the canonical path
    }

    [Fact]
    public void CanonicaliseExistingDirectory_RelativeWithBasePath_ResolvesAgainstBase()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var child = Path.Combine(baseDir, "ws");
        Directory.CreateDirectory(child);
        try
        {
            var (canonical, status, _) = WorkspaceRootDiscovery.CanonicaliseExistingDirectory("ws", basePath: baseDir);
            Assert.Equal(WorkspaceRootDiscovery.PathStatus.Ok, status);
            Assert.Equal(Path.GetFullPath(child), canonical);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void Find_NoMarkersInSubtree_DoesNotReturnSubtreeDir()
    {
        // This test verifies that a fresh directory with no .sln, .slnx, or .git
        // is NOT returned as the workspace root. A parent may still be found
        // (e.g. if the machine's temp folder is inside a repo), but the isolated
        // subtree dir itself is not the result.
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var subDir = Path.Combine(root, "sub");
        try
        {
            Directory.CreateDirectory(subDir);

            var result = WorkspaceRootDiscovery.Find(subDir);

            // The fresh subDir has no .sln or .git, so it must not be returned.
            Assert.NotEqual(subDir, result);
            // Also, root itself has no markers so it must not be returned either.
            Assert.NotEqual(root, result);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
