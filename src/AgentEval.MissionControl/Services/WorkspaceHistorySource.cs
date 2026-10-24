// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentEval.Output;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Adapters.StoreV1;
using Path = System.IO.Path;

namespace AgentEval.MissionControl.Services;

internal sealed record WorkspaceHistoryRow(string RunId, DateTimeOffset? Timestamp, string? Verdict, double? Score,
    string? Notes, string SourceId, string ContainerRunId, string RecordKind, int? SourceOrdinal,
    string TimestampBasis, string HistoryAvailability, int? RecordedEntryCount, AefRunReadInfo Evidence,
    JsonElement? RecordedEntry, JsonElement? Summary, JsonElement? Metrics);

// Selects history sources only. Store-v1 parsing/conversion and native summary projection stay in their existing owners.
internal sealed class WorkspaceHistorySource(IOutputStoreReader store, IWorkspaceRunSource source)
{
    internal async Task<IReadOnlyList<WorkspaceHistoryRow>?> ReadAsync(string kind, string name, CancellationToken ct)
    {
        var catalog = await source.ReadAsync(ct);
        var runs = catalog.Where(r => WorkspaceRunProjector.Subject(r.Evidence) is var subject
            && subject.RawKind == kind && subject.Name == name).ToArray();
        var stored = runs.Where(r => !r.ConvertedOnRead).ToArray();
        var budget = new AefReadBudget(256L << 20);
        foreach (var run in catalog) budget.Charge(run.Evidence.Bytes);
        var result = new List<WorkspaceHistoryRow>();
        foreach (var run in stored.OrderBy(r => r.SourceId, StringComparer.Ordinal))
        {
            var history = StoreV1History.ReadCaptured(run.Evidence, ct);
            if (history is { Captured: true, Count: > 0 })
            {
                foreach (var record in history.Records)
                {
                    if (!Matches(run, record.Entry.RunId)) throw Bad("A run's captured index entry names an unrelated execution.");
                    Add(Recorded(run, record));
                }
            }
            else Add(Summary(run, history switch
            {
                { Captured: false } => "not_captured", { Captured: true, Count: 0 } => "recorded_empty", _ => "not_recorded"
            }, history?.Count));
        }

        IReadOnlyList<StoreV1HistoryRecord>? index = null;
        if (kind is "agent" or "workflow")
        {
            var subject = new SubjectIdentity(kind == "workflow" ? SubjectKind.Workflow : SubjectKind.Agent, name);
            index = store.WorkspaceRoot is { } root
                ? StoreV1History.ReadFile(new FileSystemLayout(root).HistoryFile(subject), ct, budget)
                : await StoreV1Exporter.ReadSubjectHistoryAsync(store, subject, ct, budget);
        }
        var remaining = (index ?? []).Where(record =>
        {
            var linked = stored.Count(r => Matches(r, record.Entry.RunId));
            if (linked > 1) throw Bad("Multiple stored sources claim a legacy history reference.");
            return linked == 0; // Never recover a linked snapshot's uncaptured content from a legacy sibling.
        }).ToArray();
        if (remaining.Length > 0)
        {
            var temporary = Directory.CreateTempSubdirectory("ae-workspace-history-");
            try
            {
                var converted = StoreV1Exporter.ExportSubjectHistory(
                    new(kind == "workflow" ? SubjectKind.Workflow : SubjectKind.Agent, name), remaining,
                    Path.Combine(temporary.FullName, "run"), ct, budget);
                var snapshot = new AefRunEvidenceReader().Read(converted.Directory, 64L << 20, ct);
                var history = StoreV1History.ReadCaptured(snapshot, ct) ?? throw Bad("The converted index is unavailable.");
                var identity = "legacy-history-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.Info.SubjectRef))).ToLowerInvariant();
                var container = new AefWorkspaceRun(identity, snapshot, []);
                foreach (var record in history.Records) Add(Recorded(container, record));
            }
            finally { temporary.Delete(true); }
        }
        foreach (var run in runs.Where(r => r.ConvertedOnRead).OrderBy(r => r.SourceId, StringComparer.Ordinal))
            if (index is null || !index.Any(record => Matches(run, record.Entry.RunId)))
                Add(Summary(run, "not_recorded", null));
        return result.Count == 0 && runs.Length == 0 && index is null && !store.IsAvailable ? null : result;

        void Add(WorkspaceHistoryRow row)
        {
            ct.ThrowIfCancellationRequested();
            if (result.Count == StoreV1History.MaxRecords) throw Bad("The history exceeds the bounded output record count.");
            result.Add(row);
        }
    }

    private static bool Matches(AefWorkspaceRun run, string id) => run.Evidence.Info.RunId == id || run.RunAliases.Contains(id, StringComparer.Ordinal);
    private static WorkspaceHistoryRow Recorded(AefWorkspaceRun run, StoreV1HistoryRecord record) =>
        new(record.Entry.RunId, record.Entry.Timestamp, record.Entry.Verdict, record.Entry.Score, record.Entry.Notes,
            run.SourceId, run.Evidence.Info.RunId, "legacy_index", record.SourceOrdinal, "recorded-index", "captured", null,
            run.Evidence.Info, record.Recorded, null, null);
    private static WorkspaceHistoryRow Summary(AefWorkspaceRun run, string availability, int? count)
    {
        var pointer = WorkspaceRunProjector.Pointer(run);
        return new(pointer.RunId, pointer.Timestamp, pointer.Verdict, pointer.Score, null, run.SourceId, run.Evidence.Info.RunId,
            "execution_summary", null, "execution-start", availability, count, run.Evidence.Info, null, run.Evidence.Summary, run.Evidence.Metrics);
    }
    private static InvalidDataException Bad(string actual) => new("Expected: bounded source-qualified history. Actual: " + actual +
        " Suggestions: select or repair the original source. Because: missing or ambiguous history must not be silently reconstructed.");
}
