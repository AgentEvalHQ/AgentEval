// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using AgentEval.MAF.Gatekeeper;
using AgentEval.MAF.Gatekeeper.Memory;
using Microsoft.Agents.AI;

namespace AgentEval.RedTeam.Gatekeeper.MemorySecurity;

/// <summary>One durable record in the harness memory store.</summary>
internal sealed class HarnessMemoryRecord
{
    public required string Id { get; init; }
    public required string Key { get; init; }
    public required string Content { get; set; }
    /// <summary>SHA-256 of the content when it was written; out-of-band tampering changes the content, not this.</summary>
    public required string Digest { get; init; }
    public required MemorySecurityScope Owner { get; init; }
    public required MemoryCategory Category { get; init; }
    public required MemoryProvenance Provenance { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string Session { get; init; }
    public MemoryRecordState State { get; set; } = MemoryRecordState.Active;

    public bool IntegrityVerified => string.Equals(Digest, HarnessMemoryStore.Sha256(Content), StringComparison.Ordinal);
}

/// <summary>
/// The memory store behind the harness tools: a naive store on purpose. Recall is keyword match, newest first, top
/// one; it does not rank by trust or check integrity, so containment is the gates' job, not the store's.
/// </summary>
internal sealed class HarnessMemoryStore
{
    private readonly List<HarnessMemoryRecord> _records = [];
    private readonly Dictionary<string, int> _writesPerSource = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _candidatesPerSource = new(StringComparer.Ordinal);
    private long _clock;
    private int _ids;

    /// <summary>When true, recall ignores the owner partition: the deliberately broken shared partition of MS-SCOPE-001.</summary>
    public bool SharedPartitionBug { get; set; }

    public IReadOnlyList<HarnessMemoryRecord> Records => _records;

    public int WritesInRun { get; set; }
    public int WritesInSession { get; set; }

    public HarnessMemoryRecord Write(
        MemorySecurityScope owner, string key, string content, MemoryCategory category, MemoryProvenance provenance, string session)
    {
        var record = new HarnessMemoryRecord
        {
            Id = $"mem-{++_ids}",
            Key = key,
            Content = content,
            Digest = Sha256(content),
            Owner = owner,
            Category = category,
            Provenance = provenance,
            CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(++_clock),
            Session = session,
        };
        _records.Add(record);
        return record;
    }

    /// <summary>Counts a write the gates are about to see, for the resource-budget snapshot.</summary>
    public void CountWrite(MemoryProvenance provenance, string content)
    {
        WritesInRun++;
        WritesInSession++;
        _writesPerSource[provenance.SourceId] = WritesForSource(provenance.SourceId) + 1;
        if (!_candidatesPerSource.TryGetValue(provenance.SourceId, out var candidates))
        {
            _candidatesPerSource[provenance.SourceId] = candidates = new HashSet<string>(StringComparer.Ordinal);
        }

        candidates.Add(Sha256(content));
    }

    public int WritesForSource(string sourceId) => _writesPerSource.GetValueOrDefault(sourceId);

    public int UniqueCandidatesForSource(string sourceId) => _candidatesPerSource.TryGetValue(sourceId, out var set) ? set.Count : 0;

    public int WritesForUser(MemorySecurityScope owner) => _records.Count(r => SameScope(r.Owner, owner));

    public int QuarantinedForScope { get; set; }

    /// <summary>The newest active record in <paramref name="scope"/> (or any scope, with the shared-partition bug) matching the query.</summary>
    public HarnessMemoryRecord? Recall(MemorySecurityScope scope, string query)
    {
        var terms = Terms(query);
        return _records
            .Where(r => r.State is MemoryRecordState.Active)
            .Where(r => SharedPartitionBug || SameScope(r.Owner, scope))
            .Where(r => terms.Count == 0 || terms.Any(t =>
                r.Key.Contains(t, StringComparison.OrdinalIgnoreCase) || r.Content.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefault();
    }

    public IReadOnlyList<HarnessMemoryRecord> ActiveWithKey(MemorySecurityScope scope, string key) =>
        _records.Where(r => r.State is MemoryRecordState.Active && SameScope(r.Owner, scope)
                            && string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Revokes every record of <paramref name="rootLineageId"/>: the rollback an operator runs after attribution.</summary>
    public int RevokeLineage(string rootLineageId)
    {
        var revoked = 0;
        foreach (var record in _records.Where(r => r.State is MemoryRecordState.Active && r.Provenance.RootLineageId == rootLineageId))
        {
            record.State = MemoryRecordState.Revoked;
            revoked++;
        }

        return revoked;
    }

    public void NewSession() => WritesInSession = 0;

    public static bool SameScope(MemorySecurityScope a, MemorySecurityScope b) =>
        a.TenantId == b.TenantId && a.UserId == b.UserId && a.AgentId == b.AgentId && a.ApplicationId == b.ApplicationId;

    public static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static List<string> Terms(string query) =>
        query.Split([' ', ',', '.', '?', '!', ':', ';', '\'', '"', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 4)
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>A quarantined candidate: kept out of recall, with the lineage it came from.</summary>
internal sealed record HarnessQuarantinedCandidate(string OperationId, string Content, string RootLineageId, string Session);

/// <summary>The quarantine boundary the enforcing policy needs.</summary>
internal sealed class HarnessQuarantineStore(HarnessMemoryStore store) : IMemoryQuarantineStore
{
    private int _ids;

    public List<HarnessQuarantinedCandidate> Candidates { get; } = [];

    public string Session { get; set; } = "none";

    public ValueTask<MemoryQuarantineReceipt> StoreAsync(MemoryQuarantineRequest request, CancellationToken cancellationToken = default)
    {
        Candidates.Add(new HarnessQuarantinedCandidate(
            request.Context.OperationId, request.Context.Content ?? "", request.Context.Provenance.RootLineageId, Session));
        store.QuarantinedForScope++;
        return ValueTask.FromResult(new MemoryQuarantineReceipt($"q-{++_ids}", request.Context.OperationId, DateTimeOffset.UtcNow));
    }
}

/// <summary>One memory-gate decision, content-free, with the lineage of what it decided on.</summary>
internal sealed record HarnessDecision(
    string OperationId, MemoryGateStage Stage, MemoryOperationKind Kind, MemoryGateAction Action, string ReasonCode,
    string RootLineageId, string Session);

/// <summary>The audit log the pipeline writes to: what attribution and rollback read.</summary>
internal sealed class HarnessDecisionLog : IMemoryGateDecisionSink
{
    public List<HarnessDecision> Decisions { get; } = [];

    public string Session { get; set; } = "none";

    public ValueTask RecordAsync(MemoryGateContext context, MemoryGateDecision decision, CancellationToken cancellationToken = default)
    {
        Decisions.Add(new HarnessDecision(
            context.OperationId, context.Stage, context.Kind, decision.Action, decision.ReasonCode,
            context.Provenance.RootLineageId, Session));
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// What a real application supplies to the memory gates, done honestly: scope from the harness phase, never from model
/// arguments; provenance from what the model saw in this session (the lowest trust wins); record metadata, conflicts and
/// budget counters from the store.
/// </summary>
internal sealed class HarnessMemoryHost : IMemoryToolContextAdapter, IMemoryScopeResolver
{
    public const string WriteTool = "memory_write";
    public const string RecallTool = "memory_recall";
    public const string ProcedureTool = "memory_save_procedure";

    private readonly HarnessMemoryStore _store;
    private int _operations;

    public HarnessMemoryHost(HarnessMemoryStore store) => _store = store;

    public static MemoryToolOperationRegistry Registry { get; } = new(
    [
        new MemoryOperationContract(WriteTool, MemoryOperationKind.Write, MemorySurface.Tool, ["content"], [], MemoryCategory.Fact,
            isSideEffecting: true, mayReturnSensitiveContent: false),
        new MemoryOperationContract(RecallTool, MemoryOperationKind.Recall, MemorySurface.Tool, ["query"], [], MemoryCategory.Unknown,
            isSideEffecting: false, mayReturnSensitiveContent: true),
        new MemoryOperationContract(ProcedureTool, MemoryOperationKind.Promote, MemorySurface.Tool, ["steps"], [], MemoryCategory.Procedure,
            isSideEffecting: true, mayReturnSensitiveContent: false),
    ]);

    /// <summary>The user the current session runs for.</summary>
    public MemorySecurityScope Scope { get; set; } = new(tenantId: "t1", userId: "user-a");

    public string Session { get; set; } = "none";

    /// <summary>The lowest-trust content the model has seen in this session: what a write in it is attributed to.</summary>
    public MemoryProvenance SessionProvenance { get; private set; } = UserTurn("none");

    /// <summary>The write the call gate just admitted, consumed by the tool body so the store keeps the same provenance.</summary>
    public (string Key, MemoryProvenance Provenance)? PendingWrite { get; set; }

    /// <summary>The record the recall tool returned last (null: none matched), read by the result gate's context.</summary>
    public HarnessMemoryRecord? LastRecalled { get; set; }

    public bool RecallRan { get; set; }

    public void BeginSession(string session, MemorySecurityScope scope, MemoryProvenance userTurn)
    {
        Session = session;
        Scope = scope;
        SessionProvenance = userTurn;
        PendingWrite = null;
        LastRecalled = null;
        RecallRan = false;
        _store.NewSession();
    }

    /// <summary>A harness plant on the attacker's own channel: the write comes from <paramref name="source"/>, whatever else this session saw.</summary>
    public void ForceProvenance(MemoryProvenance source) => SessionProvenance = source;

    /// <summary>The model was shown content from <paramref name="source"/>; a later write in this session inherits it if lower.</summary>
    public void Expose(MemoryProvenance source)
    {
        if (source.Trust < SessionProvenance.Trust)
        {
            SessionProvenance = source;
        }
    }

    public static MemoryProvenance UserTurn(string session) =>
        new(MemorySourceKind.User, $"user-turn-{session}", MemoryTrustLevel.Medium);

    public MemorySecurityScope Resolve(AgentSession session, string? agentName) => Scope;

    public MemoryGateContext CreateCallContext(GatedToolCall call, MemoryOperationContract operation, MemoryGateStage stage)
    {
        var content = Argument(call, operation.ContentArguments.FirstOrDefault());
        var operationId = $"op-{++_operations}";
        if (operation.Kind is MemoryOperationKind.Recall or MemoryOperationKind.Search)
        {
            return new MemoryGateContext(
                operationId, stage, operation, "harness-store", Scope, UserTurn(Session), content,
                logicalSessionId: Session, budget: Budget(SessionProvenance));
        }

        var key = Argument(call, "key") ?? Argument(call, "name") ?? "note";
        var provenance = SessionProvenance;
        if (stage is MemoryGateStage.BeforeWrite)
        {
            _store.CountWrite(provenance, content ?? "");
        }

        PendingWrite = (key, provenance);
        var conflicts = _store.ActiveWithKey(Scope, key)
            .Take(64)
            .Select(r => new MemoryConflictCandidate(r.Id, r.Digest, r.Provenance.Trust, r.Provenance.RootLineageId, r.Category));
        return new MemoryGateContext(
            operationId, stage, operation, "harness-store", Scope, provenance, content,
            conflicts: conflicts, logicalSessionId: Session, budget: Budget(provenance));
    }

    public MemoryGateContext CreateResultContext(GatedToolResult result, MemoryOperationContract operation)
    {
        var operationId = $"op-{++_operations}";
        if (LastRecalled is not { } record)
        {
            // The host's own "nothing found" message: application text, not memory content.
            return new MemoryGateContext(
                operationId, MemoryGateStage.AfterRead, operation, "harness-store", Scope,
                new MemoryProvenance(MemorySourceKind.Application, "harness-store", MemoryTrustLevel.ApplicationTrusted),
                result.ResultText,
                logicalSessionId: Session,
                recordMetadata: new MemoryRecordMetadata("none", Scope, integrityVerified: true));
        }

        return new MemoryGateContext(
            operationId, MemoryGateStage.AfterRead, operation, "harness-store", Scope, record.Provenance, record.Content,
            logicalSessionId: Session,
            recordMetadata: new MemoryRecordMetadata(
                record.Id, record.Owner, record.State, createdAtUtc: record.CreatedAt, integrityVerified: record.IntegrityVerified),
            budget: new MemoryBudgetSnapshot(recalledItemCount: 1, recalledContentCharacters: record.Content.Length));
    }

    public IReadOnlyDictionary<string, object?> ApplySanitizedArguments(
        GatedToolCall call, MemoryOperationContract operation, string sanitizedContent)
    {
        var copy = call.Arguments is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(call.Arguments, StringComparer.Ordinal);
        copy[operation.ContentArguments.FirstOrDefault() ?? "content"] = sanitizedContent;
        return copy;
    }

    public object ApplySanitizedResult(GatedToolResult result, MemoryOperationContract operation, string sanitizedContent) =>
        sanitizedContent;

    private MemoryBudgetSnapshot Budget(MemoryProvenance provenance) => new(
        writesInRun: _store.WritesInRun,
        writesInSession: _store.WritesInSession,
        writesForUser: _store.WritesForUser(Scope),
        writesForSource: _store.WritesForSource(provenance.SourceId),
        uniqueCandidatesForSource: _store.UniqueCandidatesForSource(provenance.SourceId),
        quarantinedItemsForScope: _store.QuarantinedForScope);

    private static string? Argument(GatedToolCall call, string? name) =>
        name is not null && call.Arguments is not null && call.Arguments.TryGetValue(name, out var value) && value is not null
            ? value.ToString()
            : null;
}
