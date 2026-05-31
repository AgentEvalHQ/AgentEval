// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Output;

/// <summary>
/// Regression coverage for BUG-31: the cross-process JSONL append acquired its Global named mutex
/// via a parameterless <c>WaitOne()</c> — no timeout, no cancellation. A hung-but-alive holder (no
/// AbandonedMutexException) blocked the append forever, and ct was passed only to Task.Run
/// (cancelling scheduling, not the in-flight wait). The wait is now bounded by an overall deadline
/// and polled in short slices so ct is observed.
/// </summary>
public sealed class FileSystemOutputStoreAppendLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-append-lock-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private static string MutexNameFor(string path)
    {
        var canonical = Path.GetFullPath(path);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "Global\\AgentEval-jsonl-" + Convert.ToHexString(hash, 0, 8);
    }

    /// <summary>
    /// Acquires the named mutex on a dedicated thread (mutex thread-affinity) and holds it until
    /// the returned disposable is disposed. Mirrors a live, hung holder in another process.
    /// </summary>
    private static IDisposable HoldMutex(string mutexName)
    {
        var acquired = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            using var m = new Mutex(initiallyOwned: false, name: mutexName);
            m.WaitOne();
            try { acquired.Set(); release.Wait(); }
            finally { m.ReleaseMutex(); }
        }) { IsBackground = true, Name = "mutex-holder" };
        thread.Start();
        // Generous ceiling: the holder is a dedicated thread acquiring an uncontended mutex, so this
        // only guards against the thread never starting. A tight bound can flake when the OS scheduler
        // is saturated by the rest of the parallel suite on a constrained CI runner.
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(30)), "holder thread failed to acquire the mutex");

        return new Releaser(() =>
        {
            release.Set();
            thread.Join(TimeSpan.FromSeconds(5));
            acquired.Dispose();
            release.Dispose();
        });
    }

    private sealed class Releaser(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    [Fact]
    public async Task AppendHistoryEntry_WhenLockHeld_HonoursCancellation()
    {
        var store = new FileSystemOutputStore(_root);
        var subject = new SubjectIdentity(SubjectKind.Agent, "LockSubject");
        var historyPath = new FileSystemLayout(_root).HistoryFile(subject);

        using var _ = HoldMutex(MutexNameFor(historyPath));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var entry = new HistoryEntry("run-1", DateTimeOffset.UnixEpoch, "PASS", 1.0, null);

        var sw = Stopwatch.StartNew();
        // Before the fix this blocked forever (ct never reached the in-flight WaitOne).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.AppendHistoryEntryAsync(subject, entry, cts.Token));
        sw.Stop();

        // The pre-fix bug blocked FOREVER, so this bound only has to prove the wait returned at all
        // rather than that it returned within a few ms. It must stay under the 30s default lock
        // timeout (so a lock-timeout can't masquerade as success); 25s gives headroom over the
        // thread-pool/timer starvation seen on the loaded CI runner without reaching that ceiling.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(25),
            $"cancellation should interrupt the wait, not block indefinitely; took {sw.Elapsed}");
    }

    [Fact]
    public async Task AppendHistoryEntry_WhenLockHeldPastDeadline_ThrowsTimeout()
    {
        var original = FileSystemOutputStore.JsonlAppendLockTimeout;
        FileSystemOutputStore.JsonlAppendLockTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            var store = new FileSystemOutputStore(_root);
            var subject = new SubjectIdentity(SubjectKind.Agent, "TimeoutSubject");
            var historyPath = new FileSystemLayout(_root).HistoryFile(subject);

            using var _ = HoldMutex(MutexNameFor(historyPath));

            var entry = new HistoryEntry("run-2", DateTimeOffset.UnixEpoch, "PASS", 1.0, null);

            var sw = Stopwatch.StartNew();
            await Assert.ThrowsAsync<TimeoutException>(
                () => store.AppendHistoryEntryAsync(subject, entry, CancellationToken.None));
            sw.Stop();

            // The 300ms lock timeout fires as soon as the polling loop runs past its deadline; under
            // CI starvation that next iteration can be delayed several seconds. The bound only proves
            // the timeout fired rather than blocking indefinitely, so 25s is deliberately generous.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(25),
                $"timeout should fire without blocking indefinitely, took {sw.Elapsed}");
        }
        finally
        {
            FileSystemOutputStore.JsonlAppendLockTimeout = original;
        }
    }

    [Fact]
    public async Task AppendHistoryEntry_Uncontended_AppendsLine()
    {
        var store = new FileSystemOutputStore(_root);
        var subject = new SubjectIdentity(SubjectKind.Agent, "HappySubject");
        var historyPath = new FileSystemLayout(_root).HistoryFile(subject);
        var entry = new HistoryEntry("run-3", DateTimeOffset.UnixEpoch, "PASS", 1.0, null);

        await store.AppendHistoryEntryAsync(subject, entry, CancellationToken.None);

        Assert.True(File.Exists(historyPath));
        Assert.Single(await File.ReadAllLinesAsync(historyPath));
    }
}
