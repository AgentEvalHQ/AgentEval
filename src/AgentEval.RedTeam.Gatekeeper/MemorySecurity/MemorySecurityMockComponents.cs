// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AgentEval.RedTeam.MemorySecurity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Gatekeeper.MemorySecurity;

/// <summary>Scope key used by the hermetic SQL-style memory store.</summary>
public sealed record MockMemoryScope
{
    public MockMemoryScope(string tenantId, string userId)
    {
        TenantId = Validate(tenantId, nameof(tenantId));
        UserId = Validate(userId, nameof(userId));
    }

    public string TenantId { get; }
    public string UserId { get; }

    private static string Validate(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 128)
            throw new ArgumentException("Scope identifiers cannot exceed 128 characters.", parameterName);
        return value;
    }
}

/// <summary>Bounded record persisted by the mock store.</summary>
public sealed record MockMemoryRecord(
    string RecordId,
    MockMemoryScope Scope,
    string Content,
    string ContentDigest,
    string SourceId,
    bool Quarantined);

/// <summary>
/// Thread-safe SQL-style store with tenant/user partitions. The optional shared-partition defect
/// exists only to prove that the attack corpus detects cross-user contamination.
/// </summary>
public sealed class MockMemorySqlStore
{
    public const int MaximumRecords = 10_000;
    public const int MaximumContentCharacters = 8_192;
    private readonly object _sync = new();
    private readonly Dictionary<string, MockMemoryRecord> _records = new(StringComparer.Ordinal);
    private int _nextId;

    public MockMemorySqlStore(bool deliberateSharedPartitionBug = false)
        => DeliberateSharedPartitionBug = deliberateSharedPartitionBug;

    public bool DeliberateSharedPartitionBug { get; }
    public int WriteCount { get; private set; }
    public int ReadCount { get; private set; }

    public MockMemoryRecord Write(
        MockMemoryScope scope,
        string content,
        string sourceId,
        bool quarantined = false)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        if (content.Length > MaximumContentCharacters)
            throw new ArgumentException($"Content cannot exceed {MaximumContentCharacters} characters.", nameof(content));
        if (sourceId.Length > 128)
            throw new ArgumentException("Source identifiers cannot exceed 128 characters.", nameof(sourceId));

        lock (_sync)
        {
            if (_records.Count >= MaximumRecords)
                throw new InvalidOperationException("Mock memory store capacity exceeded.");
            var id = $"mock-memory-{++_nextId:D6}";
            var record = new MockMemoryRecord(id, scope, content, Digest(content), sourceId, quarantined);
            _records[StorageKey(scope, id)] = record;
            WriteCount++;
            return record;
        }
    }

    public IReadOnlyList<MockMemoryRecord> Recall(MockMemoryScope scope, string query, int maximumResults = 16)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (maximumResults is <= 0 or > 256)
            throw new ArgumentOutOfRangeException(nameof(maximumResults));

        lock (_sync)
        {
            ReadCount++;
            var prefix = StoragePrefix(scope);
            return _records
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(pair => pair.Value)
                .Where(record => !record.Quarantined && record.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(record => record.RecordId, StringComparer.Ordinal)
                .Take(maximumResults)
                .ToArray();
        }
    }

    public bool Tamper(MockMemoryScope scope, string recordId, string replacement)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacement);
        if (replacement.Length > MaximumContentCharacters)
            throw new ArgumentException($"Content cannot exceed {MaximumContentCharacters} characters.", nameof(replacement));

        lock (_sync)
        {
            var key = StorageKey(scope, recordId);
            if (!_records.TryGetValue(key, out var record))
                return false;
            // Deliberately retains the original digest to simulate out-of-band tampering.
            _records[key] = record with { Content = replacement };
            return true;
        }
    }

    public bool HasValidIntegrity(MockMemoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(record.ContentDigest),
            Convert.FromHexString(Digest(record.Content)));
    }

    public MockMemorySqlStore Restart()
    {
        var restarted = new MockMemorySqlStore(DeliberateSharedPartitionBug);
        lock (_sync)
        {
            foreach (var pair in _records)
                restarted._records[pair.Key] = pair.Value;
            restarted._nextId = _nextId;
        }
        return restarted;
    }

    private string StorageKey(MockMemoryScope scope, string recordId)
        => $"{StoragePrefix(scope)}{recordId}";

    private string StoragePrefix(MockMemoryScope scope)
        => DeliberateSharedPartitionBug
            ? $"{scope.TenantId}\u001fshared\u001f"
            : $"{scope.TenantId}\u001f{scope.UserId}\u001f";

    private static string Digest(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>Hermetic external source for browser, email, or cloud-delivered memory candidates.</summary>
public sealed class MockMemoryInjectionSource
{
    private readonly string _content;

    public MockMemoryInjectionSource(MemoryAttackDeliverySurface surface, string sourceId, string content)
    {
        if (surface is not (
                MemoryAttackDeliverySurface.BrowserDocument or
                MemoryAttackDeliverySurface.Email or
                MemoryAttackDeliverySurface.CloudTool))
        {
            throw new ArgumentOutOfRangeException(nameof(surface), surface, "Only browser, email, and cloud surfaces are supported.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        if (sourceId.Length > 128 || content.Length > MemorySecurityScenarioTextLimit)
            throw new ArgumentException("Mock source values exceed their bounded limits.");

        Surface = surface;
        SourceId = sourceId;
        _content = content;
    }

    private const int MemorySecurityScenarioTextLimit = 4_096;
    public MemoryAttackDeliverySurface Surface { get; }
    public string SourceId { get; }
    public int FetchCount { get; private set; }

    public ValueTask<string> FetchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FetchCount++;
        return ValueTask.FromResult(_content);
    }
}

/// <summary>Local or hosted MCP memory simulator with explicit identity and server-side enforcement hook.</summary>
public sealed class MockMemoryMcpEndpoint
{
    private readonly MockMemorySqlStore _store;
    private readonly Func<MemorySecurityScenario, bool> _serverAdmission;
    private readonly Func<string, bool> _serverRecallAdmission;

    public MockMemoryMcpEndpoint(
        string serverName,
        string serverVersion,
        bool hosted,
        MockMemorySqlStore store,
        Func<MemorySecurityScenario, bool>? serverAdmission = null,
        Func<string, bool>? serverRecallAdmission = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverVersion);
        if (serverName.Length > 128 || serverVersion.Length > 64)
            throw new ArgumentException("MCP identity values exceed their bounded limits.");
        ServerName = serverName;
        ServerVersion = serverVersion;
        Hosted = hosted;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _serverAdmission = serverAdmission ?? (_ => true);
        _serverRecallAdmission = serverRecallAdmission ?? (_ => true);
    }

    public string ServerName { get; }
    public string ServerVersion { get; }
    public bool Hosted { get; }
    public int ServerAdmissionCount { get; private set; }
    public int ServerRecallAdmissionCount { get; private set; }

    public MockMemoryRecord? Plant(MockMemoryScope scope, MemorySecurityScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(scenario);
        ServerAdmissionCount++;
        return _serverAdmission(scenario)
            ? _store.Write(scope, scenario.PlantInput, scenario.Id)
            : null;
    }

    public IReadOnlyList<MockMemoryRecord> Recall(MockMemoryScope scope, string query)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ServerRecallAdmissionCount++;
        return _serverRecallAdmission(query)
            ? _store.Recall(scope, query)
            : [];
    }
}

/// <summary>Content-free immutable audit event.</summary>
public sealed record MockMemoryAuditEvent
{
    public MockMemoryAuditEvent(
        string scenarioId,
        string sourceId,
        string decision,
        string contentDigest)
    {
        ScenarioId = ValidateIdentifier(scenarioId, nameof(scenarioId), 128);
        SourceId = ValidateIdentifier(sourceId, nameof(sourceId), 128);
        Decision = ValidateIdentifier(decision, nameof(decision), 64);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentDigest);
        if (contentDigest.Length != 64 || contentDigest.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Content digest must be a 64-character hexadecimal SHA-256 digest.", nameof(contentDigest));
        ContentDigest = contentDigest.ToLowerInvariant();
    }

    public string ScenarioId { get; }
    public string SourceId { get; }
    public string Decision { get; }
    public string ContentDigest { get; }

    private static string ValidateIdentifier(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new ArgumentException("Audit identifiers must be bounded ASCII identifiers.", parameterName);
        }
        return value;
    }
}

/// <summary>Bounded mock audit sink used by calibration and incident-attribution tests.</summary>
public sealed class MockMemoryAuditStore
{
    private readonly ConcurrentQueue<MockMemoryAuditEvent> _events = new();
    private int _count;

    public IReadOnlyCollection<MockMemoryAuditEvent> Events => _events.ToArray();

    public void Record(MockMemoryAuditEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        if (Interlocked.Increment(ref _count) > 10_000)
        {
            Interlocked.Decrement(ref _count);
            throw new InvalidOperationException("Mock audit capacity exceeded.");
        }
        _events.Enqueue(@event);
    }
}

/// <summary>Bounded quarantine used by offline attack simulations.</summary>
public sealed class MockMemoryQuarantineStore
{
    private readonly ConcurrentDictionary<string, MockMemoryRecord> _records = new(StringComparer.Ordinal);

    public IReadOnlyCollection<MockMemoryRecord> Records => _records.Values.ToArray();

    public void Quarantine(MockMemoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_records.Count >= 10_000)
            throw new InvalidOperationException("Mock quarantine capacity exceeded.");
        _records.TryAdd(record.RecordId, record with { Quarantined = true });
    }

    public bool Rollback(string recordId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        return _records.TryRemove(recordId, out _);
    }
}

/// <summary>
/// Generic MAF context provider backed by the SQL-style mock. It exercises the real
/// <see cref="AIContextProvider"/> lifecycle without network or provider dependencies.
/// </summary>
public sealed class MockMemoryAIContextProvider : AIContextProvider
{
    private readonly MockMemorySqlStore _store;
    private readonly MockMemoryScope _scope;
    private readonly string _query;
    private readonly bool _providerNativeCandidateHook;

    public MockMemoryAIContextProvider(
        MockMemorySqlStore store,
        MockMemoryScope scope,
        string query,
        bool providerNativeCandidateHook = false)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (query.Length > 512)
            throw new ArgumentException("Recall query cannot exceed 512 characters.", nameof(query));
        _query = query;
        _providerNativeCandidateHook = providerNativeCandidateHook;
    }

    public override IReadOnlyList<string> StateKeys => ["mock-memory-context-v1"];
    public bool ProviderNativeCandidateHook => _providerNativeCandidateHook;
    public int CandidateWriteCount { get; private set; }

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var messages = _store.Recall(_scope, _query)
            .Select(record => new ChatMessage(ChatRole.User, record.Content))
            .ToArray();
        return ValueTask.FromResult(new AIContext { Messages = messages });
    }

    protected override ValueTask StoreAIContextAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var message in context.RequestMessages.Concat(context.ResponseMessages ?? []))
        {
            var content = message.Text;
            if (string.IsNullOrWhiteSpace(content))
                continue;
            CandidateWriteCount++;
            _store.Write(_scope, content, _providerNativeCandidateHook ? "provider-native" : "generic-context");
        }
        return default;
    }
}
