// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Output;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Ingestion;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.StoreV1;

public static partial class StoreV1Exporter
{
    internal static Task<IReadOnlyList<StoreV1HistoryRecord>?> ReadSubjectHistoryAsync(
        IOutputStoreReader store, SubjectIdentity subject, CancellationToken ct, AefReadBudget budget) =>
        ReadSubjectHistoryAsync(store, subject, ct, new AefWriteBudget(budget.Limit(64L << 20), ct, budget.Charge));

    internal static async Task<IReadOnlyList<StoreV1HistoryRecord>?> ReadSubjectHistoryAsync(
        IOutputStoreReader store, SubjectIdentity subject, CancellationToken ct, AefWriteBudget? budget)
    {
        if (!store.IsAvailable) return null;
        var records = new List<StoreV1HistoryRecord>();
        var accountant = budget is null ? null : new SemanticAccountant(budget);
        await foreach (var entry in store.GetHistoryAsync(subject, ct: ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (records.Count == StoreV1History.MaxRecords) throw InvalidSource("History exceeds the bounded record count.");
            if (!double.IsFinite(entry.Score)) throw InvalidSource("History score is not finite.");
            accountant?.Account(entry);
            records.Add(StoreV1History.Parse(JsonSerializer.SerializeToElement(entry, s_store), records.Count + 1));
        }
        return records;
    }

    // A subject index can contain orphan references. Its import container is not an invented execution.
    internal static AefConversion ExportSubjectHistory(SubjectIdentity subject, IReadOnlyList<StoreV1HistoryRecord> records,
        string outputDirectory, CancellationToken ct, AefReadBudget budget)
    {
        const string reason = "Original execution timing, completion and results are unknown: only a legacy subject history index was supplied.";
        var options = new StoreV1ExportOptions { WriteBudget = new(budget.Limit(64L << 20), ct, budget.Charge) };
        var header = new AefRunHeader
        {
            RunId = "import-" + Guid.NewGuid().ToString("N"), Producer = AefConverter.DefaultProducer,
            Subject = new()
            {
                Ref = AefConverter.TypedRef(subject.Kind == SubjectKind.Workflow ? "workflow" : "agent", subject.Name),
                Kind = subject.Kind == SubjectKind.Workflow ? AefSubjectKind.Workflow : AefSubjectKind.Agent,
            },
            StartedAt = DateTimeOffset.UnixEpoch, ContentCapture = AefContentCapture.On,
            Execution = new() { TargetMode = options.TargetMode },
            Imported = new()
            {
                From = From + " (version unknown; subject history index only)",
                Asserted = ["runId", "startedAt", "endedAt", "status", "abortReason", "execution.targetMode", "contentCapture"],
            },
            Ext = new JsonObject { [ExtName] = new JsonObject { ["historyScope"] = "subject-index" } },
        };
        ct.ThrowIfCancellationRequested();
        return Export(new ExportData(header, [], DefaultLane, DateTimeOffset.UnixEpoch, reason,
            [reason, "Imported: not sealed when it ran. Index references are recorded claims, not verified execution custody."])
            { HistoryRecords = records }, outputDirectory, options);
    }
}
