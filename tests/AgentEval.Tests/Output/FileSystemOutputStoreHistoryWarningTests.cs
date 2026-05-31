// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Output;

/// <summary>
/// Regression coverage for GAP-05: GetHistoryAsync previously swallowed per-line deserialization
/// errors in a bare <c>catch { continue; }</c>, so a corrupt history.jsonl line was dropped
/// invisibly — a partially-corrupt audit history read as a shorter clean one. It now skips the bad
/// line AND surfaces the corruption on stderr (matching GetRecentRunsAsync).
/// Shares the ConsoleTests collection so Console.Error redirection doesn't race other tests.
/// </summary>
[Collection("ConsoleTests")]
public sealed class FileSystemOutputStoreHistoryWarningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-hist-warn-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task GetHistory_MalformedLine_SkippedButSurfacedOnStderr()
    {
        var store = new FileSystemOutputStore(_root);
        var subject = new SubjectIdentity(SubjectKind.Agent, "HistAgent");

        await store.AppendHistoryEntryAsync(subject, new HistoryEntry("run-1", DateTimeOffset.UnixEpoch, "PASS", 1.0, null));

        // Append a corrupt line directly to the history file.
        var historyFile = new FileSystemLayout(_root).HistoryFile(subject);
        await File.AppendAllTextAsync(historyFile, "{ this is not valid json\n");

        var originalErr = Console.Error;
        var captured = new StringWriter();
        var entries = new List<HistoryEntry>();
        try
        {
            Console.SetError(captured);
            await foreach (var e in store.GetHistoryAsync(subject))
                entries.Add(e);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        // The valid entry is still returned (one bad line doesn't fail the whole stream)...
        Assert.Single(entries);
        Assert.Equal("run-1", entries[0].RunId);
        // ...and the corruption is no longer invisible (GAP-05).
        Assert.Contains("history.jsonl", captured.ToString());
    }
}
