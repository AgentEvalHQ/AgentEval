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
    /// <summary>The gate operation that admitted the write (null for seeded state): what attribution follows.</summary>
    public string? OperationId { get; init; }
    public MemoryRecordState State { get; set; } = MemoryRecordState.Active;

    public bool IntegrityVerified => string.Equals(Digest, HarnessMemoryStore.Sha256(Content), StringComparison.Ordinal);
}

/// <summary>
/// The memory store behind the harness tools: a naive store on purpose. A recall returns the newest active records that
/// share a word with the query, up to <see cref="RecallWindow"/> (the bench's recall budget cap, the most the gates let
/// through); it does not rank by trust or check integrity, so containment is the gates' job, not the store's.
/// </summary>
internal sealed class HarnessMemoryStore
{
    /// <summary>The resource budget the bench's gate enforces: the library defaults.</summary>
    public static readonly MemoryResourceBudgetOptions BudgetOptions = new();

    /// <summary>The most records one recall returns: the budget's recalled-item cap.</summary>
    public static readonly int RecallWindow = BudgetOptions.MaximumRecalledItems;

    private readonly List<HarnessMemoryRecord> _records = [];
    private readonly Dictionary<string, int> _writesPerSource = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _writesPerUser = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _candidatesPerSource = new(StringComparer.Ordinal);
    private long _clock;
    private int _ids;

    /// <summary>When true, recall ignores the owner partition: the deliberately broken shared partition of MS-SCOPE-001.</summary>
    public bool SharedPartitionBug { get; set; }

    public IReadOnlyList<HarnessMemoryRecord> Records => _records;

    /// <summary>Writes stored so far in this case (the budget snapshot counts prior writes: the gate blocks at the cap).</summary>
    public int WritesInRun { get; private set; }

    public int WritesInSession { get; private set; }

    public int QuarantinedForScope { get; private set; }

    /// <summary>State a real deployment already holds before the case: not a write in the run, so not counted.</summary>
    public HarnessMemoryRecord Seed(MemorySecurityScope owner, string key, string content, MemoryCategory category, MemoryProvenance provenance) =>
        Add(owner, key, content, category, provenance, "seed", operationId: null);

    /// <summary>A write the gates admitted (operation <paramref name="operationId"/>), counted for the next budget snapshot.</summary>
    public HarnessMemoryRecord Write(
        MemorySecurityScope owner, string key, string content, MemoryCategory category, MemoryProvenance provenance, string session,
        string? operationId)
    {
        var record = Add(owner, key, content, category, provenance, session, operationId);
        WritesInRun++;
        WritesInSession++;
        _writesPerSource[provenance.SourceId] = WritesForSource(provenance.SourceId) + 1;
        _writesPerUser[Correlation(owner)] = WritesForUser(owner) + 1;
        Candidate(provenance, content);
        return record;
    }

    /// <summary>A candidate the gates sent to quarantine: it counts toward its source's unique candidates and the scope's quarantine.</summary>
    public void Quarantined(MemoryProvenance provenance, string content)
    {
        QuarantinedForScope++;
        Candidate(provenance, content);
    }

    public int WritesForSource(string sourceId) => _writesPerSource.GetValueOrDefault(sourceId);

    public int UniqueCandidatesForSource(string sourceId) => _candidatesPerSource.TryGetValue(sourceId, out var set) ? set.Count : 0;

    public int WritesForUser(MemorySecurityScope owner) => _writesPerUser.GetValueOrDefault(Correlation(owner));

    /// <summary>
    /// The newest active records in <paramref name="scope"/> (or any scope, with the shared-partition bug) that share a
    /// word of four letters or more with the query, at most <see cref="RecallWindow"/>.
    /// </summary>
    public IReadOnlyList<HarnessMemoryRecord> Recall(MemorySecurityScope scope, string query, Func<HarnessMemoryRecord, bool>? include = null)
    {
        var terms = Terms(query);
        return _records
            .Where(r => r.State is MemoryRecordState.Active)
            .Where(r => include is null || include(r))
            .Where(r => SharedPartitionBug || SameScope(r.Owner, scope))
            .Where(r => terms.Count == 0 || terms.Any(t =>
                r.Key.Contains(t, StringComparison.OrdinalIgnoreCase) || r.Content.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.CreatedAt)
            .Take(RecallWindow)
            .ToList();
    }

    /// <summary>What a recall tool returns for <paramref name="records"/>: one line per record, newest first.</summary>
    public static string Render(IReadOnlyList<HarnessMemoryRecord> records) =>
        records.Count == 0 ? "No matching note found." : string.Join("\n", records.Select(r => $"- {r.Content}"));

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

    private HarnessMemoryRecord Add(
        MemorySecurityScope owner, string key, string content, MemoryCategory category, MemoryProvenance provenance, string session,
        string? operationId)
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
            OperationId = operationId,
        };
        _records.Add(record);
        return record;
    }

    private void Candidate(MemoryProvenance provenance, string content)
    {
        if (!_candidatesPerSource.TryGetValue(provenance.SourceId, out var candidates))
        {
            _candidatesPerSource[provenance.SourceId] = candidates = new HashSet<string>(StringComparer.Ordinal);
        }

        candidates.Add(Sha256(content));
    }

    private static string Correlation(MemorySecurityScope scope) => $"{scope.TenantId}|{scope.UserId}|{scope.AgentId}|{scope.ApplicationId}";

    private static List<string> Terms(string query) =>
        query.Split([' ', ',', '.', '?', '!', ':', ';', '\'', '"', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 4)
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>A quarantined candidate: kept out of recall, with the gate operation and lineage it came with.</summary>
internal sealed record HarnessQuarantinedCandidate(string OperationId, string RootLineageId, string Session);

/// <summary>The quarantine boundary the enforcing policy needs.</summary>
internal sealed class HarnessQuarantineStore(HarnessMemoryStore store) : IMemoryQuarantineStore
{
    private int _ids;

    public List<HarnessQuarantinedCandidate> Candidates { get; } = [];

    public string Session { get; set; } = "none";

    public ValueTask<MemoryQuarantineReceipt> StoreAsync(MemoryQuarantineRequest request, CancellationToken cancellationToken = default)
    {
        var content = request.Context.Content ?? "";
        Candidates.Add(new HarnessQuarantinedCandidate(request.Context.OperationId, request.Context.Provenance.RootLineageId, Session));
        store.Quarantined(request.Context.Provenance, content);
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
/// budget counters from the store. A recall result holds several records, and the gates see one context per result:
/// it carries the lowest trust among them, and an owner and an integrity check that hold only when every record agrees.
/// </summary>
internal sealed class HarnessMemoryHost : IMemoryToolContextAdapter, IMemoryScopeResolver
{
    public const string WriteTool = "memory_write";
    public const string RecallTool = "memory_recall";
    public const string ProcedureTool = "memory_save_procedure";

    /// <summary>The owner a recall result reports when its records belong to different owners: it matches no caller.</summary>
    private static readonly MemorySecurityScope MixedOwners = new(tenantId: "mixed-owners", userId: "mixed-owners");

    private readonly HarnessMemoryStore _store;
    private readonly Dictionary<string, (MemoryProvenance Provenance, string OperationId)> _admitted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<HarnessMemoryRecord>> _recalled = new(StringComparer.Ordinal);
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

    public void BeginSession(string session, MemorySecurityScope scope, MemoryProvenance userTurn)
    {
        Session = session;
        Scope = scope;
        SessionProvenance = userTurn;
        _admitted.Clear();
        _recalled.Clear();
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

    /// <summary>
    /// The provenance and gate operation the call gate attributed to a write of <paramref name="content"/> (as admitted,
    /// or as the gate rewrote it), consumed by the tool body so the store keeps both; the session's provenance and no
    /// operation when the gate saw no such write.
    /// </summary>
    public (MemoryProvenance Provenance, string? OperationId) TakeAdmitted(string content) =>
        _admitted.Remove(HarnessMemoryStore.Sha256(content), out var admitted) ? admitted : (SessionProvenance, null);

    /// <summary>Records what a recall for <paramref name="query"/> returned, for the result gate's context.</summary>
    public void Recalled(string query, IReadOnlyList<HarnessMemoryRecord> records) => _recalled[query] = records;

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
        _admitted[HarnessMemoryStore.Sha256(content ?? "")] = (provenance, operationId);
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
        var query = Argument(result.Arguments, "query") ?? "";
        var records = _recalled.GetValueOrDefault(query) ?? [];
        if (records.Count == 0)
        {
            // The host's own "nothing found" message: application text, not memory content.
            return new MemoryGateContext(
                operationId, MemoryGateStage.AfterRead, operation, "harness-store", Scope,
                new MemoryProvenance(MemorySourceKind.Application, "harness-store", MemoryTrustLevel.ApplicationTrusted),
                result.ResultText,
                logicalSessionId: Session,
                recordMetadata: new MemoryRecordMetadata("none", Scope, integrityVerified: true),
                budget: new MemoryBudgetSnapshot(recalledItemCount: 0, recalledContentCharacters: result.ResultText?.Length ?? 0));
        }

        var lowest = records.MinBy(r => r.Provenance.Trust)!;
        var owner = records.All(r => HarnessMemoryStore.SameScope(r.Owner, records[0].Owner)) ? records[0].Owner : MixedOwners;
        return new MemoryGateContext(
            operationId, MemoryGateStage.AfterRead, operation, "harness-store", Scope, lowest.Provenance,
            HarnessMemoryStore.Render(records),
            logicalSessionId: Session,
            recordMetadata: new MemoryRecordMetadata(
                records.Count == 1 ? records[0].Id : $"recall-{HarnessMemoryStore.Sha256(string.Join(",", records.Select(r => r.Id)))[..16]}",
                owner, MemoryRecordState.Active,
                createdAtUtc: records.Min(r => r.CreatedAt), integrityVerified: records.All(r => r.IntegrityVerified)),
            budget: new MemoryBudgetSnapshot(
                recalledItemCount: records.Count, recalledContentCharacters: records.Sum(r => r.Content.Length)));
    }

    public IReadOnlyDictionary<string, object?> ApplySanitizedArguments(
        GatedToolCall call, MemoryOperationContract operation, string sanitizedContent)
    {
        // The write the gate rewrote is stored as rewritten: it keeps the provenance and operation of the original, so
        // attribution follows the decision, not the bytes.
        var original = HarnessMemoryStore.Sha256(Argument(call, operation.ContentArguments.FirstOrDefault()) ?? "");
        if (_admitted.TryGetValue(original, out var admitted))
        {
            _admitted[HarnessMemoryStore.Sha256(sanitizedContent)] = admitted;
        }

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

    private static string? Argument(GatedToolCall call, string? name) => Argument(call.Arguments, name);

    private static string? Argument(IReadOnlyDictionary<string, object?>? arguments, string? name) =>
        name is not null && arguments is not null && arguments.TryGetValue(name, out var value) && value is not null
            ? value.ToString()
            : null;
}
