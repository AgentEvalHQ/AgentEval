// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Output;

/// <summary>
/// Phase-0 security task 0.9 (2026-05-13): constructing
/// <see cref="FileSystemOutputStore"/> must NOT delete files. The stale-
/// sentinel sweep (<c>*.invalid.json</c> / <c>*.lock</c> / <c>*.tmp</c>
/// older than 24h) was previously invoked from the constructor, which
/// violated Mission Control's "read-only viewer" contract — MC constructs
/// the store at startup and must never mutate the workspace.
/// </summary>
/// <remarks>
/// <para>
/// These tests lock down the read-only contract by:
/// </para>
/// <list type="number">
///   <item>creating a workspace with a 25-hour-old <c>.tmp</c> + <c>.lock</c> + <c>.invalid.json</c>;</item>
///   <item>constructing the store (mimicking MC's startup);</item>
///   <item>asserting all three sentinel files still exist on disk;</item>
///   <item>then explicitly invoking <c>SweepStaleSentinelsAsync</c> (mimicking a CLI writer path) and asserting the files are now gone.</item>
/// </list>
/// <para>
/// If the ctor sweep ever returns, the first assertion fails — the read-only
/// promise is broken at startup.
/// </para>
/// </remarks>
public class FileSystemOutputStoreReadOnlyTests
{
    [Fact]
    public async Task Constructor_DoesNotDelete_StaleSentinels()
    {
        var workspace = Path.Combine(Path.GetTempPath(),
            "agenteval-readonly-ctor-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workspace);

        try
        {
            // Seed three stale sentinel files (28 hours old — beyond the 24h cutoff).
            var stalePast = DateTimeOffset.UtcNow.AddHours(-28).UtcDateTime;
            var tmpFile = Path.Combine(workspace, "abandoned-write.tmp");
            var lockFile = Path.Combine(workspace, "ensure-subject.lock");
            var invalidFile = Path.Combine(workspace, "schema-failure.invalid.json");

            await File.WriteAllTextAsync(tmpFile, "stale tmp content");
            await File.WriteAllTextAsync(lockFile, "stale lock content");
            await File.WriteAllTextAsync(invalidFile, "stale invalid content");
            File.SetLastWriteTimeUtc(tmpFile, stalePast);
            File.SetLastWriteTimeUtc(lockFile, stalePast);
            File.SetLastWriteTimeUtc(invalidFile, stalePast);

            // Constructing the store MUST NOT touch the workspace contents.
            // Mission Control instantiates FileSystemOutputStore at startup
            // and is documented as read-only; if this ctor sweeps, MC violates
            // its own contract.
            _ = new FileSystemOutputStore(workspace);

            Assert.True(File.Exists(tmpFile),
                "Constructor must not delete stale .tmp sentinels — violates read-only contract.");
            Assert.True(File.Exists(lockFile),
                "Constructor must not delete stale .lock sentinels — violates read-only contract.");
            Assert.True(File.Exists(invalidFile),
                "Constructor must not delete stale .invalid.json sentinels — violates read-only contract.");
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task SweepStaleSentinelsAsync_Explicit_DeletesStaleFiles()
    {
        // Companion test: confirm the sweep DOES work when invoked explicitly
        // (the CLI bench writer entry points call it after constructing the
        // store). If this regresses, workspace hygiene degrades across long-
        // running benchmark cycles even though the ctor stays read-only.
        var workspace = Path.Combine(Path.GetTempPath(),
            "agenteval-readonly-sweep-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workspace);

        try
        {
            var stalePast = DateTimeOffset.UtcNow.AddHours(-28).UtcDateTime;
            var tmpFile = Path.Combine(workspace, "stale.tmp");
            await File.WriteAllTextAsync(tmpFile, "stale");
            File.SetLastWriteTimeUtc(tmpFile, stalePast);

            var store = new FileSystemOutputStore(workspace);
            Assert.True(File.Exists(tmpFile), "Sanity: ctor leaves file in place.");

            await store.SweepStaleSentinelsAsync(TimeSpan.FromHours(24));

            Assert.False(File.Exists(tmpFile),
                "Explicit sweep must delete stale .tmp sentinels older than the threshold.");
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task SweepStaleSentinelsAsync_FreshFiles_LeftAlone()
    {
        // The sweep's age filter must protect freshly-written sentinels —
        // an in-flight benchmark can legitimately have a `.lock` file
        // 30 seconds old. Only the 24h+ stale ones get culled.
        var workspace = Path.Combine(Path.GetTempPath(),
            "agenteval-readonly-fresh-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workspace);

        try
        {
            var freshFile = Path.Combine(workspace, "active.lock");
            await File.WriteAllTextAsync(freshFile, "fresh");
            // Don't backdate — file is "just now".

            var store = new FileSystemOutputStore(workspace);
            await store.SweepStaleSentinelsAsync(TimeSpan.FromHours(24));

            Assert.True(File.Exists(freshFile),
                "Fresh sentinels (< threshold) must survive an explicit sweep — otherwise we'd kill in-flight writers.");
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { /* best-effort */ }
        }
    }
}
