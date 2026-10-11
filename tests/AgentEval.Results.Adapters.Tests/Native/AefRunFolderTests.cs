// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Results.Adapters.Native;

namespace AgentEval.Results.Adapters.Tests.Native;

public sealed class AefRunFolderTests : IDisposable
{
    private readonly string _root = AefTestRuns.TempPath("aef-folder");

    public void Dispose() => AefTestRuns.Delete(_root);

    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TwoWritersInTheSameSecond_HoldDifferentFolders_EachLockedUntilReleased()
    {
        using var first = AefRunFolder.Reserve(_root, Start, "eval");
        using var second = AefRunFolder.Reserve(_root, Start, "eval");

        Assert.NotEqual(first.Directory, second.Directory);
        Assert.Matches("^20261001T090000Z-eval-[0-9a-f]{16}$", first.RunId);
        Assert.True(File.Exists(first.Directory + ".lock"));   // beside the folder, never inside the sealed run
        Assert.False(Directory.Exists(first.Directory));        // AefRunWriter.Create makes it
    }

    [Fact]
    public void ReleasingAFolder_RemovesItsLock()
    {
        var folder = AefRunFolder.Reserve(_root, Start, "bench");
        var lockFile = folder.Directory + ".lock";

        folder.Dispose();

        Assert.False(File.Exists(lockFile));
    }

    [Fact]
    public void AHeldLock_CannotBeTakenTwice()
    {
        using var held = AefRunFolder.Reserve(_root, Start, "redteam");

        // What a second writer that drew the same id meets: CreateNew fails, so it draws again instead of sharing.
        Assert.ThrowsAny<IOException>(() => new FileStream(held.Directory + ".lock", FileMode.CreateNew));
    }
}
