// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Security;
using System.Text.Json;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Durable, fail-closed, deliberately single-process JSON containment store. An exclusive adjacent sidecar lock
/// is held for the lifetime of the instance; distributed deployments must provide another
/// <see cref="IContainmentStore"/> implementation.
/// </summary>
public sealed class JsonFileContainmentStore : IContainmentStore
{
    private const int StoreSchemaVersion = 1;
    private const int MaxJsonDepth = 8;
    private const int MinMaxRecords = 1;
    private const int MaxMaxRecords = 100_000;
    private const int MinMaxNonces = 1;
    private const int MaxMaxNonces = 100_000;
    private const long MinMaxFileBytes = 1024;
    private const long MaxMaxFileBytes = 64L * 1024 * 1024;

    private readonly string _path;
    private readonly string _ownershipPath;
    private readonly IContainmentReleaseAuthorizationVerifier _releaseVerifier;
    private readonly TimeProvider _timeProvider;
    private readonly StoreBounds _bounds;
    private readonly SemaphoreSlim _mutationLock = new(1, 1);
    private FileStream? _ownershipHandle;
    private StoreState _state = StoreState.Unhealthy("store_initializing");
    private int _disposed;

    /// <summary>
    /// Opens, exclusively owns, and fully validates a versioned store. Missing storage is created only when
    /// <see cref="JsonFileContainmentStoreOptions.BootstrapIfMissing"/> is explicitly enabled.
    /// </summary>
    public JsonFileContainmentStore(
        string path,
        IContainmentReleaseAuthorizationVerifier releaseVerifier,
        JsonFileContainmentStoreOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _releaseVerifier = releaseVerifier ?? throw new ArgumentNullException(nameof(releaseVerifier));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _bounds = StoreBounds.Resolve(options ?? new JsonFileContainmentStoreOptions());

        try
        {
            _path = Path.GetFullPath(path);
            _ownershipPath = _path + ".lock";
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("Containment store path is invalid.", nameof(path));
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

            var loaded = ReadAndValidateState();
            ownershipHandle.Flush(flushToDisk: true);
            _ownershipHandle = ownershipHandle;
            ownershipHandle = null;
            Volatile.Write(ref _state, loaded);
        }
        catch (InvalidOperationException)
        {
            ownershipHandle?.Dispose();
            throw;
        }
        catch (Exception exception) when (IsExpectedStorageFailure(exception))
        {
            ownershipHandle?.Dispose();
            throw InitializationFailure("initialization_failed");
        }
    }

    /// <inheritdoc/>
    public ContainmentSnapshot GetCurrent(ContainmentTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return ContainmentSnapshot.Indeterminate(target, "store_disposed");
        }

        return SnapshotFor(Volatile.Read(ref _state), target);
    }

    /// <inheritdoc/>
    public async ValueTask<ContainmentMutationResult> ContainAsync(
        ContainmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return IndeterminateResult(request.Target, "store_disposed");
        }

        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return IndeterminateResult(request.Target, "store_disposed");
            }

            var currentState = Volatile.Read(ref _state);
            if (!currentState.IsHealthy)
            {
                return IndeterminateResult(request.Target, currentState.FailureCode!);
            }

            if (currentState.Records.TryGetValue(request.Target, out var current)
                && current.Status == ContainmentStatus.Active)
            {
                return ContainmentMutationResult.Unchanged(ContainmentSnapshot.FromRecord(current));
            }

            if (current is null && currentState.Records.Count >= _bounds.MaxRecords)
            {
                return MarkIndeterminate(request.Target, "record_capacity_exceeded");
            }

            var now = UtcNow();
            var version = current is null ? 1 : TryIncrementVersion(current.Version);
            if (version is null)
            {
                return MarkIndeterminate(request.Target, "record_version_exhausted");
            }

            ContainmentRecord candidateRecord;
            try
            {
                candidateRecord = new ContainmentRecord(
                    request.Target,
                    ContainmentStatus.Active,
                    now,
                    releasedAtUtc: null,
                    request.ReasonCode,
                    request.EvidenceReference,
                    request.Issuer,
                    reviewer: null,
                    version.Value,
                    NewETag());
            }
            catch (Exception exception) when (IsExpectedStorageFailure(exception))
            {
                return MarkIndeterminate(request.Target, "mutation_preparation_failed");
            }

            var records = new Dictionary<ContainmentTarget, ContainmentRecord>(currentState.Records)
            {
                [request.Target] = candidateRecord,
            };
            var candidateState = StoreState.Healthy(records, currentState.LiveReleaseNonces);

            cancellationToken.ThrowIfCancellationRequested();
            if (!TryCommit(candidateState))
            {
                return MarkIndeterminate(request.Target, "mutation_outcome_unknown");
            }

            return ContainmentMutationResult.Applied(ContainmentSnapshot.FromRecord(candidateRecord));
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask<ContainmentMutationResult> ReleaseAsync(
        ContainmentReleaseAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return IndeterminateResult(authorization.Target, "store_disposed");
        }

        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return IndeterminateResult(authorization.Target, "store_disposed");
            }

            var currentState = Volatile.Read(ref _state);
            if (!currentState.IsHealthy)
            {
                return IndeterminateResult(authorization.Target, currentState.FailureCode!);
            }

            var currentSnapshot = SnapshotFor(currentState, authorization.Target);
            if (!currentState.Records.TryGetValue(authorization.Target, out var current)
                || current.Status != ContainmentStatus.Active
                || current.Version != authorization.ExpectedVersion)
            {
                return ContainmentMutationResult.Conflict(currentSnapshot);
            }

            var now = UtcNow();
            if (authorization.IssuedAtUtc > now || authorization.ExpiresAtUtc <= now)
            {
                return ContainmentMutationResult.Conflict(currentSnapshot);
            }

            if (currentState.LiveReleaseNonces.ContainsKey(authorization.Nonce))
            {
                return ContainmentMutationResult.Conflict(currentSnapshot);
            }

            bool signatureAccepted;
            try
            {
                var payload = ContainmentReleaseAuthorizationCanonicalizer.CreatePayload(authorization);
                signatureAccepted = _releaseVerifier.Verify(authorization, payload);
            }
            catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
            {
                signatureAccepted = false;
            }

            if (!signatureAccepted)
            {
                return ContainmentMutationResult.Conflict(currentSnapshot);
            }

            var nextVersion = TryIncrementVersion(current.Version);
            if (nextVersion is null)
            {
                return MarkIndeterminate(authorization.Target, "record_version_exhausted");
            }

            var liveNonces = currentState.LiveReleaseNonces
                .Where(pair => pair.Value > now)
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
            if (liveNonces.Count >= _bounds.MaxLiveReleaseNonces)
            {
                return ContainmentMutationResult.Conflict(currentSnapshot);
            }

            liveNonces.Add(authorization.Nonce, authorization.ExpiresAtUtc);

            ContainmentRecord released;
            try
            {
                released = new ContainmentRecord(
                    current.Target,
                    ContainmentStatus.Released,
                    current.ContainedAtUtc,
                    now,
                    current.ReasonCode,
                    current.EvidenceReference,
                    current.Issuer,
                    authorization.OperatorId,
                    nextVersion.Value,
                    NewETag());
            }
            catch (Exception exception) when (IsExpectedStorageFailure(exception))
            {
                return MarkIndeterminate(authorization.Target, "mutation_preparation_failed");
            }

            var records = new Dictionary<ContainmentTarget, ContainmentRecord>(currentState.Records)
            {
                [authorization.Target] = released,
            };
            var candidateState = StoreState.Healthy(records, liveNonces);

            cancellationToken.ThrowIfCancellationRequested();
            if (!TryCommit(candidateState))
            {
                return MarkIndeterminate(authorization.Target, "mutation_outcome_unknown");
            }

            return ContainmentMutationResult.Applied(ContainmentSnapshot.FromRecord(released));
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <summary>
    /// Explicitly reloads and validates the complete durable file. This is the only operation that can restore a
    /// store made indeterminate by a failed or ambiguous mutation.
    /// </summary>
    /// <returns>True only when a complete healthy state was published.</returns>
    public async ValueTask<bool> TryReloadAsync(CancellationToken cancellationToken = default)
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
                var loaded = ReadAndValidateState();
                Volatile.Write(ref _state, loaded);
                return true;
            }
            catch (Exception exception) when (IsExpectedStorageFailure(exception))
            {
                Volatile.Write(ref _state, StoreState.Unhealthy("reload_failed"));
                return false;
            }
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _mutationLock.Wait();
        try
        {
            Volatile.Write(ref _state, StoreState.Unhealthy("store_disposed"));
            _ownershipHandle?.Dispose();
            _ownershipHandle = null;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

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
            if (stream.Length <= 0 || stream.Length > _bounds.MaxFileBytes || stream.Length > int.MaxValue)
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
            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaxJsonDepth,
                });

            var root = StrictObject(
                document.RootElement,
                ["schemaVersion", "records", "liveReleaseNonces"]);
            if (ReadInt32(root["schemaVersion"]) != StoreSchemaVersion)
            {
                throw InvalidStore();
            }

            var recordsElement = RequireArray(root["records"]);
            if (recordsElement.GetArrayLength() > _bounds.MaxRecords)
            {
                throw InvalidStore();
            }

            var records = new Dictionary<ContainmentTarget, ContainmentRecord>();
            foreach (var element in recordsElement.EnumerateArray())
            {
                var record = ReadRecord(element);
                if (!records.TryAdd(record.Target, record))
                {
                    throw InvalidStore();
                }
            }

            var noncesElement = RequireArray(root["liveReleaseNonces"]);
            if (noncesElement.GetArrayLength() > _bounds.MaxLiveReleaseNonces)
            {
                throw InvalidStore();
            }

            var nonces = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            foreach (var element in noncesElement.EnumerateArray())
            {
                var properties = StrictObject(element, ["nonce", "expiresAtUtc"]);
                var nonce = ContainmentValidation.Token(
                    ReadString(properties["nonce"]),
                    "nonce",
                    ContainmentValidation.MaxNonceLength,
                    minLength: 16);
                var expiresAtUtc = ContainmentValidation.Utc(
                    ReadDateTimeOffset(properties["expiresAtUtc"]),
                    "expiresAtUtc");
                if (!nonces.TryAdd(nonce, expiresAtUtc))
                {
                    throw InvalidStore();
                }
            }

            return StoreState.Healthy(records, nonces);
        }
        catch (Exception exception) when (exception is JsonException
            or FormatException
            or ArgumentException
            or InvalidOperationException
            or OverflowException)
        {
            throw InvalidStore();
        }
    }

    private ContainmentRecord ReadRecord(JsonElement element)
    {
        var properties = StrictObject(
            element,
            [
                "schemaVersion",
                "target",
                "status",
                "containedAtUtc",
                "releasedAtUtc",
                "reasonCode",
                "evidenceReference",
                "issuer",
                "reviewer",
                "version",
                "etag",
            ]);
        if (ReadInt32(properties["schemaVersion"]) != ContainmentRecord.CurrentSchemaVersion)
        {
            throw InvalidStore();
        }

        var target = ReadTarget(properties["target"]);
        var statusValue = ReadInt32(properties["status"]);
        if (!Enum.IsDefined(typeof(ContainmentStatus), statusValue))
        {
            throw InvalidStore();
        }

        return new ContainmentRecord(
            target,
            (ContainmentStatus)statusValue,
            ReadDateTimeOffset(properties["containedAtUtc"]),
            ReadNullableDateTimeOffset(properties["releasedAtUtc"]),
            ReadString(properties["reasonCode"]),
            ReadString(properties["evidenceReference"]),
            ReadString(properties["issuer"]),
            ReadNullableString(properties["reviewer"]),
            ReadInt64(properties["version"]),
            ReadString(properties["etag"]));
    }

    private static ContainmentTarget ReadTarget(JsonElement element)
    {
        var properties = StrictObject(element, ["tenant", "kind", "identifier"]);
        var tenant = ReadString(properties["tenant"]);
        var identifier = ReadString(properties["identifier"]);
        var kindValue = ReadInt32(properties["kind"]);
        if (!Enum.IsDefined(typeof(ContainmentTargetKind), kindValue))
        {
            throw InvalidStore();
        }

        return (ContainmentTargetKind)kindValue switch
        {
            ContainmentTargetKind.Session => new ContainmentTarget.Session(tenant, identifier),
            ContainmentTargetKind.McpServer => new ContainmentTarget.McpServer(tenant, identifier),
            ContainmentTargetKind.AgentEndpoint => new ContainmentTarget.AgentEndpoint(tenant, identifier),
            _ => throw InvalidStore(),
        };
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

    private void WriteStateDurably(StoreState state, bool destinationMustExist)
    {
        var bytes = Serialize(state);
        if (bytes.LongLength > _bounds.MaxFileBytes)
        {
            throw new IOException("Containment store serialization exceeded its configured bound.");
        }

        var tempPath = _path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
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
                File.Replace(tempPath, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
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

    private static byte[] Serialize(StoreState state)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", StoreSchemaVersion);
            writer.WriteStartArray("records");
            foreach (var record in state.Records.Values
                .OrderBy(static value => value.Target.Tenant, StringComparer.Ordinal)
                .ThenBy(static value => value.Target.Kind)
                .ThenBy(static value => value.Target.Identifier, StringComparer.Ordinal))
            {
                WriteRecord(writer, record);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("liveReleaseNonces");
            foreach (var nonce in state.LiveReleaseNonces.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("nonce", nonce.Key);
                WriteTimestamp(writer, "expiresAtUtc", nonce.Value);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteRecord(Utf8JsonWriter writer, ContainmentRecord record)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", record.SchemaVersion);
        writer.WriteStartObject("target");
        writer.WriteString("tenant", record.Target.Tenant);
        writer.WriteNumber("kind", (int)record.Target.Kind);
        writer.WriteString("identifier", record.Target.Identifier);
        writer.WriteEndObject();
        writer.WriteNumber("status", (int)record.Status);
        WriteTimestamp(writer, "containedAtUtc", record.ContainedAtUtc);
        if (record.ReleasedAtUtc is { } releasedAtUtc)
        {
            WriteTimestamp(writer, "releasedAtUtc", releasedAtUtc);
        }
        else
        {
            writer.WriteNull("releasedAtUtc");
        }

        writer.WriteString("reasonCode", record.ReasonCode);
        writer.WriteString("evidenceReference", record.EvidenceReference);
        writer.WriteString("issuer", record.Issuer);
        if (record.Reviewer is { } reviewer)
        {
            writer.WriteString("reviewer", reviewer);
        }
        else
        {
            writer.WriteNull("reviewer");
        }

        writer.WriteNumber("version", record.Version);
        writer.WriteString("etag", record.ETag);
        writer.WriteEndObject();
    }

    private static void WriteTimestamp(Utf8JsonWriter writer, string propertyName, DateTimeOffset value)
        => writer.WriteString(propertyName, value.ToString("O", CultureInfo.InvariantCulture));

    private static Dictionary<string, JsonElement> StrictObject(
        JsonElement element,
        IReadOnlyCollection<string> expectedProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw InvalidStore();
        }

        var expected = expectedProperties.ToHashSet(StringComparer.Ordinal);
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !properties.TryAdd(property.Name, property.Value))
            {
                throw InvalidStore();
            }
        }

        if (properties.Count != expected.Count)
        {
            throw InvalidStore();
        }

        return properties;
    }

    private static JsonElement RequireArray(JsonElement element)
        => element.ValueKind == JsonValueKind.Array ? element : throw InvalidStore();

    private static string ReadString(JsonElement element)
        => element.ValueKind == JsonValueKind.String && element.GetString() is { } value
            ? value
            : throw InvalidStore();

    private static string? ReadNullableString(JsonElement element)
        => element.ValueKind == JsonValueKind.Null ? null : ReadString(element);

    private static int ReadInt32(JsonElement element)
        => element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : throw InvalidStore();

    private static long ReadInt64(JsonElement element)
        => element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value)
            ? value
            : throw InvalidStore();

    private static DateTimeOffset ReadDateTimeOffset(JsonElement element)
    {
        var value = ReadString(element);
        if (!DateTimeOffset.TryParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var result))
        {
            throw InvalidStore();
        }

        return result;
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(JsonElement element)
        => element.ValueKind == JsonValueKind.Null ? null : ReadDateTimeOffset(element);

    private ContainmentMutationResult MarkIndeterminate(ContainmentTarget target, string failureCode)
    {
        Volatile.Write(ref _state, StoreState.Unhealthy(failureCode));
        return IndeterminateResult(target, failureCode);
    }

    private static ContainmentMutationResult IndeterminateResult(
        ContainmentTarget target,
        string failureCode)
        => ContainmentMutationResult.Indeterminate(ContainmentSnapshot.Indeterminate(target, failureCode));

    private static ContainmentSnapshot SnapshotFor(StoreState state, ContainmentTarget target)
    {
        if (!state.IsHealthy)
        {
            return ContainmentSnapshot.Indeterminate(target, state.FailureCode!);
        }

        return state.Records.TryGetValue(target, out var record)
            ? ContainmentSnapshot.FromRecord(record)
            : ContainmentSnapshot.NotContained(target);
    }

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();

    private static long? TryIncrementVersion(long value) => value == long.MaxValue ? null : value + 1;

    private static string NewETag()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (IsExpectedStorageFailure(exception))
        {
            // The durable destination is never touched during best-effort temporary cleanup.
        }
    }

    private static InvalidOperationException InvalidStore()
        => new("Containment store data is invalid.");

    private static InvalidOperationException InitializationFailure(string code)
        => new($"Containment store initialization failed: {code}.");

    private static bool IsExpectedStorageFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException
            or ArgumentException
            or CryptographicException;

    private sealed record StoreBounds(
        bool BootstrapIfMissing,
        int MaxRecords,
        int MaxLiveReleaseNonces,
        long MaxFileBytes)
    {
        public static StoreBounds Resolve(JsonFileContainmentStoreOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (options.MaxRecords is < MinMaxRecords or > MaxMaxRecords)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    $"Maximum record count must be between {MinMaxRecords} and {MaxMaxRecords}.");
            }

            if (options.MaxLiveReleaseNonces is < MinMaxNonces or > MaxMaxNonces)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    $"Maximum live release nonce count must be between {MinMaxNonces} and {MaxMaxNonces}.");
            }

            if (options.MaxFileBytes is < MinMaxFileBytes or > MaxMaxFileBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    $"Maximum file size must be between {MinMaxFileBytes} and {MaxMaxFileBytes} bytes.");
            }

            return new StoreBounds(
                options.BootstrapIfMissing,
                options.MaxRecords,
                options.MaxLiveReleaseNonces,
                options.MaxFileBytes);
        }
    }

    private sealed class StoreState
    {
        private StoreState(
            FrozenDictionary<ContainmentTarget, ContainmentRecord> records,
            FrozenDictionary<string, DateTimeOffset> liveReleaseNonces,
            string? failureCode)
        {
            Records = records;
            LiveReleaseNonces = liveReleaseNonces;
            FailureCode = failureCode;
        }

        public FrozenDictionary<ContainmentTarget, ContainmentRecord> Records { get; }

        public FrozenDictionary<string, DateTimeOffset> LiveReleaseNonces { get; }

        public string? FailureCode { get; }

        public bool IsHealthy => FailureCode is null;

        public static StoreState Healthy(
            IEnumerable<KeyValuePair<ContainmentTarget, ContainmentRecord>> records,
            IEnumerable<KeyValuePair<string, DateTimeOffset>> liveReleaseNonces)
            => new(
                records.ToFrozenDictionary(),
                liveReleaseNonces.ToFrozenDictionary(StringComparer.Ordinal),
                failureCode: null);

        public static StoreState Unhealthy(string failureCode)
            => new(
                FrozenDictionary<ContainmentTarget, ContainmentRecord>.Empty,
                FrozenDictionary<string, DateTimeOffset>.Empty,
                ContainmentValidation.Token(failureCode, nameof(failureCode), maxLength: 64));
    }
}
