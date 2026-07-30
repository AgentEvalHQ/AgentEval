// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Durable, tenant-bound, deliberately single-process security-graph store. The raw session identifier and
/// HMAC key are never serialized. Distributed deployments must provide another <see cref="ISecurityGraphStore"/>
/// implementation.
/// </summary>
public sealed class JsonFileSecurityGraphStore : ISecurityGraphStore
{
    private const int StoreVersion = 1;
    private const int MinimumKeyBytes = 32;
    private const int MaximumKeyBytes = 4096;
    private const int MinimumMaxObservations = 1;
    private const int MaximumMaxObservations = 100_000;
    private const int MinimumMaxCoverageGaps = 1;
    private const int MaximumMaxCoverageGaps = 4096;
    private const long MinimumMaxFileBytes = 1024;
    private const long MaximumMaxFileBytes = 64L * 1024 * 1024;
    private const int MinimumJsonDepth = 4;
    private const int MaximumJsonDepth = 32;
    private const string CapacityGapReason = "capacity_exceeded";
    private static readonly TimeSpan MinimumRetention = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaximumRetention = TimeSpan.FromDays(365);
    private static readonly byte[] SessionDigestDomain =
        "AgentEval.SecurityGraph.Session.v1"u8.ToArray();
    private static readonly byte[] KeyVerifierDomain =
        "AgentEval.SecurityGraph.KeyVerifier.v1"u8.ToArray();

    private readonly string _path;
    private readonly string _ownershipPath;
    private readonly string _tenant;
    private readonly string _sessionKeyId;
    private readonly byte[] _sessionKey;
    private readonly TimeProvider _timeProvider;
    private readonly StoreBounds _bounds;
    private readonly SemaphoreSlim _mutationLock = new(1, 1);
    private FileStream? _ownershipHandle;
    private StoreState _state = StoreState.Unhealthy("store_initializing");
    private int _disposed;

    /// <summary>
    /// Opens, exclusively owns, and fully validates a version-1 store. Missing storage is created only when
    /// <see cref="JsonFileSecurityGraphStoreOptions.BootstrapIfMissing"/> is explicitly enabled.
    /// </summary>
    public JsonFileSecurityGraphStore(
        string path,
        string tenant,
        string sessionKeyId,
        ReadOnlySpan<byte> sessionHmacKey,
        JsonFileSecurityGraphStoreOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _tenant = SecurityGraphValidation.Identity(
            tenant,
            nameof(tenant),
            ContainmentValidation.MaxTenantLength);
        _sessionKeyId = ContainmentValidation.Token(
            sessionKeyId,
            nameof(sessionKeyId),
            ContainmentValidation.MaxKeyIdLength);
        if (sessionHmacKey.Length is < MinimumKeyBytes or > MaximumKeyBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sessionHmacKey),
                $"Session HMAC key material must contain {MinimumKeyBytes}..{MaximumKeyBytes} bytes.");
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        _bounds = StoreBounds.Resolve(options ?? new JsonFileSecurityGraphStoreOptions());
        _sessionKey = sessionHmacKey.ToArray();

        try
        {
            _path = Path.GetFullPath(path);
            _ownershipPath = _path + ".lock";
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            CryptographicOperations.ZeroMemory(_sessionKey);
            throw new ArgumentException(
                "Security graph store path is invalid.",
                nameof(path));
        }

        FileStream? ownershipHandle = null;
        try
        {
            var parent = Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(parent))
            {
                throw InitializationFailure("invalid_parent");
            }

            if (!Directory.Exists(parent))
            {
                if (!_bounds.BootstrapIfMissing)
                {
                    throw InitializationFailure("store_missing");
                }

                Directory.CreateDirectory(parent);
            }

            if (!File.Exists(_path) && !_bounds.BootstrapIfMissing)
            {
                throw InitializationFailure("store_missing");
            }

            ownershipHandle = AcquireOwnership(_ownershipPath);
            if (!File.Exists(_path))
            {
                WriteStateDurably(StoreState.Healthy([], []), destinationMustExist: false);
            }

            var now = UtcNow();
            var loaded = ReadAndValidateState();
            if (HasClockRollback(loaded, now))
            {
                throw InitializationFailure("clock_rollback");
            }

            var pruned = Prune(loaded, now);
            if (!ReferenceEquals(loaded, pruned))
            {
                WriteStateDurably(pruned, destinationMustExist: true);
            }

            ownershipHandle.Flush(flushToDisk: true);
            _ownershipHandle = ownershipHandle;
            ownershipHandle = null;
            Volatile.Write(ref _state, pruned);
        }
        catch (InvalidOperationException)
        {
            ownershipHandle?.Dispose();
            CryptographicOperations.ZeroMemory(_sessionKey);
            throw;
        }
        catch (Exception exception) when (IsExpectedStorageFailure(exception))
        {
            ownershipHandle?.Dispose();
            CryptographicOperations.ZeroMemory(_sessionKey);
            throw InitializationFailure("initialization_failed");
        }
    }

    /// <inheritdoc />
    public SecurityGraphTenantSnapshot Read(TimeSpan window)
    {
        ValidateWindow(window);
        var now = UtcNow();
        if (Volatile.Read(ref _disposed) != 0)
        {
            return SecurityGraphTenantSnapshot.Indeterminate(
                _tenant,
                window,
                now,
                "store_disposed");
        }

        var state = Volatile.Read(ref _state);
        if (!state.IsHealthy)
        {
            return SecurityGraphTenantSnapshot.Indeterminate(
                _tenant,
                window,
                now,
                state.FailureCode!);
        }

        if (HasClockRollback(state, now))
        {
            return SecurityGraphTenantSnapshot.Indeterminate(
                _tenant,
                window,
                now,
                "clock_rollback");
        }

        var cutoff = now - window;
        var observations = state.Observations
            .Where(observation =>
                observation.AcceptedAtUtc >= cutoff &&
                observation.AcceptedAtUtc <= now)
            .ToArray();
        var gaps = state.CoverageGaps
            .Where(gap => gap.AtUtc >= cutoff && gap.AtUtc <= now)
            .ToArray();
        return SecurityGraphTenantSnapshot.Determinate(
            _tenant,
            window,
            now,
            observations,
            gaps);
    }

    /// <inheritdoc />
    public async ValueTask<SecurityGraphMutationResult> AppendAsync(
        SecurityGraphObservationRequest observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return Result(
                SecurityGraphMutationDisposition.Indeterminate,
                "store_disposed");
        }

        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return Result(
                    SecurityGraphMutationDisposition.Indeterminate,
                    "store_disposed");
            }

            var current = Volatile.Read(ref _state);
            if (!current.IsHealthy)
            {
                return Result(
                    SecurityGraphMutationDisposition.Indeterminate,
                    current.FailureCode!);
            }

            var now = UtcNow();
            if (HasClockRollback(current, now))
            {
                return MarkIndeterminate("clock_rollback");
            }

            var pruned = Prune(current, now);
            string digest;
            try
            {
                digest = ComputeSessionDigest(observation.SessionIdentifier);
            }
            catch (Exception exception) when (
                exception is CryptographicException or EncoderFallbackException)
            {
                return MarkIndeterminate("digest_failed");
            }

            if (pruned.EventsById.TryGetValue(observation.EventId, out var existing))
            {
                var disposition = Equivalent(existing, observation, digest)
                    ? SecurityGraphMutationDisposition.Unchanged
                    : SecurityGraphMutationDisposition.Conflict;
                var reason = disposition == SecurityGraphMutationDisposition.Unchanged
                    ? "unchanged"
                    : "event_id_conflict";
                if (!ReferenceEquals(pruned, current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryCommit(pruned))
                    {
                        return MarkIndeterminate("mutation_outcome_unknown");
                    }
                }

                return Result(disposition, reason);
            }

            if (pruned.Observations.Count >= _bounds.MaxObservations)
            {
                if (!TryAddOrCoalesceGap(
                    pruned,
                    SecurityGraphCoverageGap.Accepted(
                        now,
                        CapacityGapReason),
                    out var capacityState))
                {
                    return MarkIndeterminate("gap_capacity_exceeded");
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!TryCommit(capacityState))
                {
                    return MarkIndeterminate("mutation_outcome_unknown");
                }

                return Result(
                    SecurityGraphMutationDisposition.RejectedWithGap,
                    CapacityGapReason);
            }

            SecurityGraphObservation accepted;
            try
            {
                accepted = new SecurityGraphObservation(
                    observation.EventId,
                    now,
                    observation.Source,
                    observation.Destination,
                    observation.Signal,
                    digest,
                    observation.EvidenceReference);
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException)
            {
                return MarkIndeterminate("mutation_preparation_failed");
            }

            var observations = pruned.Observations.ToList();
            observations.Add(accepted);
            var candidate = StoreState.Healthy(
                observations,
                pruned.CoverageGaps);
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryCommit(candidate))
            {
                return MarkIndeterminate("mutation_outcome_unknown");
            }

            return Result(
                SecurityGraphMutationDisposition.Applied,
                "applied");
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<SecurityGraphMutationResult> MarkCoverageGapAsync(
        SecurityGraphCoverageGap gap,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gap);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return Result(
                SecurityGraphMutationDisposition.Indeterminate,
                "store_disposed");
        }

        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return Result(
                    SecurityGraphMutationDisposition.Indeterminate,
                    "store_disposed");
            }

            var current = Volatile.Read(ref _state);
            if (!current.IsHealthy)
            {
                return Result(
                    SecurityGraphMutationDisposition.Indeterminate,
                    current.FailureCode!);
            }

            var now = UtcNow();
            if (HasClockRollback(current, now))
            {
                return MarkIndeterminate("clock_rollback");
            }

            var pruned = Prune(current, now);
            var accepted = SecurityGraphCoverageGap.Accepted(
                now,
                gap.ReasonCode,
                gap.Count);
            if (!TryAddOrCoalesceGap(pruned, accepted, out var candidate))
            {
                return MarkIndeterminate("gap_capacity_exceeded");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!TryCommit(candidate))
            {
                return MarkIndeterminate("mutation_outcome_unknown");
            }

            return Result(
                SecurityGraphMutationDisposition.Applied,
                "gap_recorded");
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <summary>Explicitly reloads and validates the complete durable file after an indeterminate mutation.</summary>
    public async ValueTask<bool> TryReloadAsync(
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = UtcNow();
                var loaded = ReadAndValidateState();
                if (HasClockRollback(loaded, now))
                {
                    Volatile.Write(
                        ref _state,
                        StoreState.Unhealthy("clock_rollback"));
                    return false;
                }

                var pruned = Prune(loaded, now);
                if (!ReferenceEquals(loaded, pruned))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteStateDurably(pruned, destinationMustExist: true);
                }

                Volatile.Write(ref _state, pruned);
                return true;
            }
            catch (Exception exception) when (IsExpectedStorageFailure(exception))
            {
                Volatile.Write(
                    ref _state,
                    StoreState.Unhealthy("reload_failed"));
                return false;
            }
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _mutationLock.Wait();
        try
        {
            Volatile.Write(
                ref _state,
                StoreState.Unhealthy("store_disposed"));
            CryptographicOperations.ZeroMemory(_sessionKey);
            _ownershipHandle?.Dispose();
            _ownershipHandle = null;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    private StoreState ReadAndValidateState()
    {
        byte[] bytes;
        using (var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan))
        {
            if (stream.Length <= 0 ||
                stream.Length > _bounds.MaxFileBytes ||
                stream.Length > int.MaxValue)
            {
                throw InvalidStore();
            }

            bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            if (stream.Position != stream.Length)
            {
                throw InvalidStore();
            }
        }

        try
        {
            var reader = new Utf8JsonReader(
                bytes,
                new JsonReaderOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = _bounds.MaxJsonDepth,
                });
            var state = ReadRoot(ref reader);
            if (reader.Read())
            {
                throw InvalidStore();
            }

            return state;
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or ArgumentException
                or InvalidOperationException or OverflowException)
        {
            throw InvalidStore();
        }
    }

    private StoreState ReadRoot(ref Utf8JsonReader reader)
    {
        RequireRead(ref reader, JsonTokenType.StartObject);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int? version = null;
        string? tenant = null;
        string? sessionKeyId = null;
        string? sessionKeyVerifier = null;
        long? retentionSeconds = null;
        List<SecurityGraphObservation>? observations = null;
        List<SecurityGraphCoverageGap>? gaps = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var property = ReadUniqueProperty(ref reader, seen);
            RequireNext(ref reader);
            switch (property)
            {
                case "version":
                    version = ReadInt32(ref reader);
                    break;
                case "tenant":
                    tenant = ReadString(ref reader);
                    break;
                case "sessionKeyId":
                    sessionKeyId = ReadString(ref reader);
                    break;
                case "sessionKeyVerifier":
                    sessionKeyVerifier = SecurityGraphValidation.SessionDigest(
                        ReadString(ref reader),
                        "sessionKeyVerifier");
                    break;
                case "retentionSeconds":
                    retentionSeconds = ReadInt64(ref reader);
                    break;
                case "observations":
                    observations = ReadObservations(ref reader);
                    break;
                case "coverageGaps":
                    gaps = ReadCoverageGaps(ref reader);
                    break;
                default:
                    throw InvalidStore();
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject ||
            seen.Count != 7 ||
            version != StoreVersion ||
            !string.Equals(tenant, _tenant, StringComparison.Ordinal) ||
            !string.Equals(sessionKeyId, _sessionKeyId, StringComparison.Ordinal) ||
            !KeyVerifierEquals(sessionKeyVerifier) ||
            retentionSeconds != checked((long)_bounds.Retention.TotalSeconds) ||
            observations is null ||
            gaps is null)
        {
            throw InvalidStore();
        }

        return StoreState.Healthy(observations, gaps);
    }

    private List<SecurityGraphObservation> ReadObservations(
        ref Utf8JsonReader reader)
    {
        RequireToken(reader, JsonTokenType.StartArray);
        var observations = new List<SecurityGraphObservation>();
        var eventIds = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (observations.Count >= _bounds.MaxObservations)
            {
                throw InvalidStore();
            }

            var observation = ReadObservation(ref reader);
            if (!eventIds.Add(observation.EventId))
            {
                throw InvalidStore();
            }

            observations.Add(observation);
        }

        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw InvalidStore();
        }

        return observations;
    }

    private static SecurityGraphObservation ReadObservation(
        ref Utf8JsonReader reader)
    {
        RequireToken(reader, JsonTokenType.StartObject);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? eventId = null;
        DateTimeOffset? acceptedAtUtc = null;
        SecurityGraphNode? source = null;
        var sourceRead = false;
        SecurityGraphNode? destination = null;
        SecurityGraphSignalKind? signal = null;
        string? sessionDigest = null;
        string? evidenceReference = null;
        var evidenceRead = false;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var property = ReadUniqueProperty(ref reader, seen);
            RequireNext(ref reader);
            switch (property)
            {
                case "eventId":
                    eventId = ReadString(ref reader);
                    break;
                case "acceptedAtUtc":
                    acceptedAtUtc = ReadTimestamp(ref reader);
                    break;
                case "source":
                    sourceRead = true;
                    source = reader.TokenType == JsonTokenType.Null
                        ? null
                        : ReadNode(ref reader);
                    break;
                case "destination":
                    destination = ReadNode(ref reader);
                    break;
                case "signal":
                    signal = ReadExactEnum<SecurityGraphSignalKind>(
                        ref reader);
                    break;
                case "sessionDigest":
                    sessionDigest = ReadString(ref reader);
                    break;
                case "evidenceReference":
                    evidenceRead = true;
                    evidenceReference = reader.TokenType == JsonTokenType.Null
                        ? null
                        : ReadString(ref reader);
                    break;
                default:
                    throw InvalidStore();
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject ||
            seen.Count != 7 ||
            eventId is null ||
            acceptedAtUtc is null ||
            !sourceRead ||
            destination is null ||
            signal is null ||
            sessionDigest is null ||
            !evidenceRead)
        {
            throw InvalidStore();
        }

        return new SecurityGraphObservation(
            eventId,
            acceptedAtUtc.Value,
            source,
            destination,
            signal.Value,
            sessionDigest,
            evidenceReference);
    }

    private List<SecurityGraphCoverageGap> ReadCoverageGaps(
        ref Utf8JsonReader reader)
    {
        RequireToken(reader, JsonTokenType.StartArray);
        var gaps = new List<SecurityGraphCoverageGap>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (gaps.Count >= _bounds.MaxCoverageGaps)
            {
                throw InvalidStore();
            }

            gaps.Add(ReadCoverageGap(ref reader));
        }

        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw InvalidStore();
        }

        return gaps;
    }

    private static SecurityGraphCoverageGap ReadCoverageGap(
        ref Utf8JsonReader reader)
    {
        RequireToken(reader, JsonTokenType.StartObject);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset? atUtc = null;
        string? reasonCode = null;
        int? count = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var property = ReadUniqueProperty(ref reader, seen);
            RequireNext(ref reader);
            switch (property)
            {
                case "atUtc":
                    atUtc = ReadTimestamp(ref reader);
                    break;
                case "reasonCode":
                    reasonCode = ReadString(ref reader);
                    break;
                case "count":
                    count = ReadInt32(ref reader);
                    break;
                default:
                    throw InvalidStore();
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject ||
            seen.Count != 3 ||
            atUtc is null ||
            reasonCode is null ||
            count is null)
        {
            throw InvalidStore();
        }

        return SecurityGraphCoverageGap.Accepted(
            atUtc.Value,
            reasonCode,
            count.Value);
    }

    private static SecurityGraphNode ReadNode(ref Utf8JsonReader reader)
    {
        RequireToken(reader, JsonTokenType.StartObject);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        SecurityGraphNodeKind? kind = null;
        string? identifier = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var property = ReadUniqueProperty(ref reader, seen);
            RequireNext(ref reader);
            switch (property)
            {
                case "kind":
                    kind = ReadExactEnum<SecurityGraphNodeKind>(ref reader);
                    break;
                case "id":
                    identifier = ReadString(ref reader);
                    break;
                default:
                    throw InvalidStore();
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject ||
            seen.Count != 2 ||
            kind is null ||
            identifier is null)
        {
            throw InvalidStore();
        }

        return new SecurityGraphNode(kind.Value, identifier);
    }

    private bool TryCommit(StoreState candidate)
    {
        try
        {
            WriteStateDurably(candidate, destinationMustExist: true);
            Volatile.Write(ref _state, candidate);
            return true;
        }
        catch (Exception exception) when (IsExpectedStorageFailure(exception))
        {
            return false;
        }
    }

    private void WriteStateDurably(
        StoreState state,
        bool destinationMustExist)
    {
        var bytes = Serialize(state);
        if (bytes.LongLength > _bounds.MaxFileBytes)
        {
            throw new IOException(
                "Security graph serialization exceeded its configured bound.");
        }

        var tempPath = _path + "." +
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) +
            ".tmp";
        var moved = false;
        try
        {
            using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (destinationMustExist)
            {
                File.Replace(
                    tempPath,
                    _path,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, _path);
            }

            moved = true;
            using var committed = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.WriteThrough);
            committed.Flush(flushToDisk: true);
        }
        finally
        {
            if (!moved)
            {
                TryDeleteTemporaryFile(tempPath);
            }
        }
    }

    private byte[] Serialize(StoreState state)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
                MaxDepth = _bounds.MaxJsonDepth,
            }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", StoreVersion);
            writer.WriteString("tenant", _tenant);
            writer.WriteString("sessionKeyId", _sessionKeyId);
            writer.WriteString("sessionKeyVerifier", ComputeKeyVerifier());
            writer.WriteNumber(
                "retentionSeconds",
                checked((long)_bounds.Retention.TotalSeconds));
            writer.WriteStartArray("observations");
            foreach (var observation in state.Observations
                .OrderBy(value => value.AcceptedAtUtc)
                .ThenBy(value => value.EventId, StringComparer.Ordinal))
            {
                WriteObservation(writer, observation);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("coverageGaps");
            foreach (var gap in state.CoverageGaps
                .OrderBy(value => value.AtUtc)
                .ThenBy(value => value.ReasonCode, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                WriteTimestamp(writer, "atUtc", gap.AtUtc);
                writer.WriteString("reasonCode", gap.ReasonCode);
                writer.WriteNumber("count", gap.Count);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteObservation(
        Utf8JsonWriter writer,
        SecurityGraphObservation observation)
    {
        writer.WriteStartObject();
        writer.WriteString("eventId", observation.EventId);
        WriteTimestamp(
            writer,
            "acceptedAtUtc",
            observation.AcceptedAtUtc);
        if (observation.Source is { } source)
        {
            writer.WritePropertyName("source");
            WriteNode(writer, source);
        }
        else
        {
            writer.WriteNull("source");
        }

        writer.WritePropertyName("destination");
        WriteNode(writer, observation.Destination);
        writer.WriteString("signal", observation.Signal.ToString());
        writer.WriteString("sessionDigest", observation.SessionDigest);
        if (observation.EvidenceReference is { } evidenceReference)
        {
            writer.WriteString("evidenceReference", evidenceReference);
        }
        else
        {
            writer.WriteNull("evidenceReference");
        }

        writer.WriteEndObject();
    }

    private static void WriteNode(
        Utf8JsonWriter writer,
        SecurityGraphNode node)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", node.Kind.ToString());
        writer.WriteString("id", node.Identifier);
        writer.WriteEndObject();
    }

    private string ComputeSessionDigest(string sessionIdentifier)
        => ComputeKeyedDigest(
            SessionDigestDomain,
            _tenant,
            sessionIdentifier);

    private string ComputeKeyVerifier()
        => ComputeKeyedDigest(
            KeyVerifierDomain,
            _tenant,
            _sessionKeyId);

    private string ComputeKeyedDigest(
        byte[] domain,
        string firstValue,
        string secondValue)
    {
        var firstBytes = Encoding.UTF8.GetBytes(firstValue);
        var secondBytes = Encoding.UTF8.GetBytes(secondValue);
        var payload = new byte[
            domain.Length +
            sizeof(int) + firstBytes.Length +
            sizeof(int) + secondBytes.Length];
        var offset = 0;
        domain.CopyTo(payload, offset);
        offset += domain.Length;
        BinaryPrimitives.WriteInt32BigEndian(
            payload.AsSpan(offset, sizeof(int)),
            firstBytes.Length);
        offset += sizeof(int);
        firstBytes.CopyTo(payload, offset);
        offset += firstBytes.Length;
        BinaryPrimitives.WriteInt32BigEndian(
            payload.AsSpan(offset, sizeof(int)),
            secondBytes.Length);
        offset += sizeof(int);
        secondBytes.CopyTo(payload, offset);

        try
        {
            var digest = HMACSHA256.HashData(_sessionKey, payload);
            return Convert.ToBase64String(digest)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(secondBytes);
        }
    }

    private bool KeyVerifierEquals(string? durableVerifier)
    {
        if (durableVerifier is null)
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(ComputeKeyVerifier());
        var actual = Encoding.ASCII.GetBytes(durableVerifier);
        try
        {
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(actual);
        }
    }
    private bool TryAddOrCoalesceGap(
        StoreState state,
        SecurityGraphCoverageGap candidate,
        out StoreState updated)
    {
        var gaps = state.CoverageGaps.ToList();
        var bucket = MinuteBucket(candidate.AtUtc);
        var index = gaps.FindIndex(existing =>
            string.Equals(
                existing.ReasonCode,
                candidate.ReasonCode,
                StringComparison.Ordinal) &&
            MinuteBucket(existing.AtUtc) == bucket);
        if (index >= 0)
        {
            var existing = gaps[index];
            var count = existing.Count > int.MaxValue - candidate.Count
                ? int.MaxValue
                : existing.Count + candidate.Count;
            gaps[index] = SecurityGraphCoverageGap.Accepted(
                existing.AtUtc,
                existing.ReasonCode,
                count);
        }
        else
        {
            if (gaps.Count >= _bounds.MaxCoverageGaps)
            {
                updated = state;
                return false;
            }

            gaps.Add(candidate);
        }

        updated = StoreState.Healthy(state.Observations, gaps);
        return true;
    }

    private StoreState Prune(StoreState state, DateTimeOffset now)
    {
        var cutoff = now - _bounds.Retention;
        var observations = state.Observations
            .Where(value => value.AcceptedAtUtc >= cutoff)
            .ToArray();
        var gaps = state.CoverageGaps
            .Where(value => value.AtUtc >= cutoff)
            .ToArray();
        if (observations.Length == state.Observations.Count &&
            gaps.Length == state.CoverageGaps.Count)
        {
            return state;
        }

        return StoreState.Healthy(observations, gaps);
    }

    private void ValidateWindow(TimeSpan window)
    {
        if (window <= TimeSpan.Zero || window > _bounds.Retention)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }
    }

    private static bool Equivalent(
        SecurityGraphObservation existing,
        SecurityGraphObservationRequest request,
        string digest)
        => existing.Source == request.Source &&
           existing.Destination == request.Destination &&
           existing.Signal == request.Signal &&
           string.Equals(
               existing.SessionDigest,
               digest,
               StringComparison.Ordinal) &&
           string.Equals(
               existing.EvidenceReference,
               request.EvidenceReference,
               StringComparison.Ordinal);

    private static bool HasClockRollback(
        StoreState state,
        DateTimeOffset now)
        => state.LatestAcceptedAtUtc is { } latest && now < latest;

    private static DateTimeOffset MinuteBucket(DateTimeOffset value)
        => new(
            value.Year,
            value.Month,
            value.Day,
            value.Hour,
            value.Minute,
            0,
            TimeSpan.Zero);

    private SecurityGraphMutationResult MarkIndeterminate(
        string failureCode)
    {
        Volatile.Write(
            ref _state,
            StoreState.Unhealthy(failureCode));
        return Result(
            SecurityGraphMutationDisposition.Indeterminate,
            failureCode);
    }

    private static SecurityGraphMutationResult Result(
        SecurityGraphMutationDisposition disposition,
        string reasonCode)
        => new(disposition, reasonCode);

    private DateTimeOffset UtcNow()
        => _timeProvider.GetUtcNow().ToUniversalTime();

    private static FileStream AcquireOwnership(string ownershipPath)
    {
        try
        {
            return new FileStream(
                ownershipPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
        }
        catch (Exception exception) when (IsExpectedStorageFailure(exception))
        {
            throw InitializationFailure("ownership_unavailable");
        }
    }

    private static void WriteTimestamp(
        Utf8JsonWriter writer,
        string propertyName,
        DateTimeOffset value)
        => writer.WriteString(
            propertyName,
            value.ToString("O", CultureInfo.InvariantCulture));

    private static string ReadUniqueProperty(
        ref Utf8JsonReader reader,
        HashSet<string> seen)
    {
        RequireToken(reader, JsonTokenType.PropertyName);
        var property = reader.GetString() ?? throw InvalidStore();
        if (!seen.Add(property))
        {
            throw InvalidStore();
        }

        return property;
    }

    private static void RequireRead(
        ref Utf8JsonReader reader,
        JsonTokenType tokenType)
    {
        if (!reader.Read())
        {
            throw InvalidStore();
        }

        RequireToken(reader, tokenType);
    }

    private static void RequireNext(ref Utf8JsonReader reader)
    {
        if (!reader.Read())
        {
            throw InvalidStore();
        }
    }

    private static void RequireToken(
        Utf8JsonReader reader,
        JsonTokenType tokenType)
    {
        if (reader.TokenType != tokenType)
        {
            throw InvalidStore();
        }
    }

    private static string ReadString(ref Utf8JsonReader reader)
    {
        RequireToken(reader, JsonTokenType.String);
        return reader.GetString() ?? throw InvalidStore();
    }

    private static int ReadInt32(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.Number ||
            !reader.TryGetInt32(out var value))
        {
            throw InvalidStore();
        }

        return value;
    }

    private static long ReadInt64(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.Number ||
            !reader.TryGetInt64(out var value))
        {
            throw InvalidStore();
        }

        return value;
    }

    private static DateTimeOffset ReadTimestamp(
        ref Utf8JsonReader reader)
    {
        var value = ReadString(ref reader);
        if (!DateTimeOffset.TryParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var result) ||
            result.Offset != TimeSpan.Zero)
        {
            throw InvalidStore();
        }

        return result;
    }

    private static TEnum ReadExactEnum<TEnum>(
        ref Utf8JsonReader reader)
        where TEnum : struct, Enum
    {
        var value = ReadString(ref reader);
        if (!Enum.TryParse<TEnum>(
                value,
                ignoreCase: false,
                out var parsed) ||
            !Enum.IsDefined(parsed) ||
            !string.Equals(
                Enum.GetName(parsed),
                value,
                StringComparison.Ordinal))
        {
            throw InvalidStore();
        }

        return parsed;
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (IsExpectedStorageFailure(exception))
        {
            // Best-effort cleanup never touches the durable destination.
        }
    }

    private static InvalidOperationException InvalidStore()
        => new("Security graph store data is invalid.");

    private static InvalidOperationException InitializationFailure(string code)
        => new($"Security graph store initialization failed: {code}.");

    private static bool IsExpectedStorageFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException
            or SecurityException or InvalidDataException
            or InvalidOperationException or NotSupportedException
            or ArgumentException or CryptographicException
            or JsonException;

    private sealed record StoreBounds(
        bool BootstrapIfMissing,
        TimeSpan Retention,
        int MaxObservations,
        int MaxCoverageGaps,
        long MaxFileBytes,
        int MaxJsonDepth)
    {
        public static StoreBounds Resolve(
            JsonFileSecurityGraphStoreOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (options.Retention < MinimumRetention ||
                options.Retention > MaximumRetention ||
                options.Retention.Ticks % TimeSpan.TicksPerSecond != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "Retention must be a whole number of seconds between one hour and 365 days.");
            }

            if (options.MaxObservations is
                < MinimumMaxObservations or > MaximumMaxObservations)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    $"Maximum observations must be between {MinimumMaxObservations} and {MaximumMaxObservations}.");
            }

            if (options.MaxCoverageGaps is
                < MinimumMaxCoverageGaps or > MaximumMaxCoverageGaps)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    $"Maximum coverage gaps must be between {MinimumMaxCoverageGaps} and {MaximumMaxCoverageGaps}.");
            }

            if (options.MaxFileBytes is
                < MinimumMaxFileBytes or > MaximumMaxFileBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    $"Maximum file size must be between {MinimumMaxFileBytes} and {MaximumMaxFileBytes} bytes.");
            }

            if (options.MaxJsonDepth is
                < MinimumJsonDepth or > MaximumJsonDepth)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    $"Maximum JSON depth must be between {MinimumJsonDepth} and {MaximumJsonDepth}.");
            }

            return new StoreBounds(
                options.BootstrapIfMissing,
                options.Retention,
                options.MaxObservations,
                options.MaxCoverageGaps,
                options.MaxFileBytes,
                options.MaxJsonDepth);
        }
    }

    private sealed class StoreState
    {
        private StoreState(
            SecurityGraphObservation[] observations,
            SecurityGraphCoverageGap[] coverageGaps,
            FrozenDictionary<string, SecurityGraphObservation> eventsById,
            DateTimeOffset? latestAcceptedAtUtc,
            string? failureCode)
        {
            Observations = Array.AsReadOnly(observations);
            CoverageGaps = Array.AsReadOnly(coverageGaps);
            EventsById = eventsById;
            LatestAcceptedAtUtc = latestAcceptedAtUtc;
            FailureCode = failureCode;
        }

        public IReadOnlyList<SecurityGraphObservation> Observations { get; }

        public IReadOnlyList<SecurityGraphCoverageGap> CoverageGaps { get; }

        public FrozenDictionary<string, SecurityGraphObservation> EventsById { get; }

        public DateTimeOffset? LatestAcceptedAtUtc { get; }

        public string? FailureCode { get; }

        public bool IsHealthy => FailureCode is null;

        public static StoreState Healthy(
            IEnumerable<SecurityGraphObservation> observations,
            IEnumerable<SecurityGraphCoverageGap> coverageGaps)
        {
            var observationArray = observations.ToArray();
            var gapArray = coverageGaps.ToArray();
            var events = observationArray.ToFrozenDictionary(
                value => value.EventId,
                StringComparer.Ordinal);
            var latestObservation = observationArray.Length == 0
                ? (DateTimeOffset?)null
                : observationArray.Max(value => value.AcceptedAtUtc);
            var latestGap = gapArray.Length == 0
                ? (DateTimeOffset?)null
                : gapArray.Max(value => value.AtUtc);
            var latest = latestObservation is null
                ? latestGap
                : latestGap is null || latestObservation >= latestGap
                    ? latestObservation
                    : latestGap;
            return new StoreState(
                observationArray,
                gapArray,
                events,
                latest,
                failureCode: null);
        }

        public static StoreState Unhealthy(string failureCode)
            => new(
                [],
                [],
                FrozenDictionary<string, SecurityGraphObservation>.Empty,
                latestAcceptedAtUtc: null,
                ContainmentValidation.Token(
                    failureCode,
                    nameof(failureCode),
                    maxLength: 64));
    }
}
