// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AgentEval.Results.Runs;

namespace AgentEval.Results.Observations;

/// <summary>
/// Exclusive-process, bounded filesystem observation store. Files are flushed before atomic publication;
/// directory-entry durability across power loss depends on the filesystem. Trusted local ownership is required.
/// </summary>
public sealed class OtlpObservationStore : IOtlpObservationStore
{
    private const string Version = "otlp-observation/1";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly object _gate = new();
    private readonly OtlpObservationStoreOptions _options;
    private readonly IOtlpContentPolicy _policy;
    private readonly IOtlpSessionGrouper _grouper;
    private readonly string _root, _records, _scratch;
    private readonly FileStream _lease;
    private readonly Dictionary<string, long> _catalogue = new(StringComparer.Ordinal);
    private long _bytes;
    private bool _disposed;

    /// <summary>Opens an owned root, inventories finite storage and removes only known unaccepted scratch left by interruption.</summary>
    public OtlpObservationStore(OtlpObservationStoreOptions options, IOtlpContentPolicy policy,
        IOtlpSessionGrouper grouper, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _grouper = grouper ?? throw new ArgumentNullException(nameof(grouper));
        if (string.IsNullOrWhiteSpace(options.Root) || !Path.IsPathFullyQualified(options.Root)
            || options.MaxRecords is < 1 or > 100_000 || options.MaxRecordBytes is < 1 or > (64 << 20)
            || options.MaxStoredBytes is < 1 or > (64L << 30)
            || options.Capture is not (OtlpContentCapture.Off or OtlpContentCapture.On))
            throw new ArgumentException("Expected: an absolute dedicated root and valid finite retention limits. Actual: invalid options. " +
                "Suggestions: use documented storage defaults. Because: observations require bounded independent storage.", nameof(options));
        // Validate the configured source before creating filesystem state.
        _grouper.Group(options.Source, new([]), cancellationToken);
        _options = options;
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Root));
        if (_root == Path.GetPathRoot(_root)) throw Storage();
        if (!OperatingSystem.IsWindows() && !AefFolder.NativeInUse) throw Storage();
        CheckAncestors(_root);
        Directory.CreateDirectory(_root);
        CheckAncestors(_root);
        _records = Path.Combine(_root, "records");
        _scratch = Path.Combine(_root, "incoming.tmp");
        var leasePath = Path.Combine(_root, ".observations.lock");
        CheckFileIfPresent(leasePath);
        _lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var marker = Path.Combine(_root, ".observations-version");
            CheckFileIfPresent(marker);
            if (!File.Exists(marker))
            {
                if (Directory.EnumerateFileSystemEntries(_root).Any(p => p != leasePath)) throw Storage();
                using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write("1\n"u8);
                stream.Flush(true);
            }
            else
            {
                using var stream = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length != 2 || stream.ReadByte() != '1' || stream.ReadByte() != '\n') throw Storage();
            }
            foreach (var path in Directory.EnumerateFileSystemEntries(_root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (path != marker && path != leasePath && path != _records && path != _scratch) throw Storage();
            }
            CheckAncestors(_records);
            Directory.CreateDirectory(_records);
            foreach (var path in Directory.EnumerateFileSystemEntries(_records))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_catalogue.Count >= options.MaxRecords) throw Storage();
                var file = Path.GetFileName(path);
                if (!file.EndsWith(".json", StringComparison.Ordinal)) throw Storage();
                var id = file[..^5];
                ValidateId(id);
                RequireFile(path);
                var length = new FileInfo(path).Length;
                if (length < 1 || length > options.MaxRecordBytes || length > options.MaxStoredBytes - _bytes) throw Storage();
                _catalogue.Add(id, length);
                _bytes += length;
            }
            // The exclusive lease proves no live writer owns this single bounded scratch file.
            ClearScratch();
        }
        catch { _lease.Dispose(); throw; }
    }

    /// <inheritdoc />
    public OtlpObservationReceipt Accept(OtlpTraceBatch batch, CancellationToken cancellationToken = default)
    {
        Enter(cancellationToken);
        try
        {
            var content = _policy.Apply(batch, _options.Capture, cancellationToken);
            var sessions = _grouper.Group(_options.Source, content.Batch, cancellationToken);
            var record = new OtlpObservationRecord(Version, _options.Source, content, sessions);
            CheckAncestors(_root);
            CheckAncestors(_records);
            ClearScratch();
            long length;
            using (var file = new FileStream(_scratch, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var bounded = new BoundedWriteStream(file, _options.MaxRecordBytes, cancellationToken);
                JsonSerializer.Serialize(bounded, record, JsonOptions);
                cancellationToken.ThrowIfCancellationRequested();
                file.Flush(true);
                length = file.Length;
            }
            string id;
            using (var file = new FileStream(_scratch, FileMode.Open, FileAccess.Read, FileShare.Read))
                id = "batch-" + Hash(file, cancellationToken);
            var spans = sessions.Sum(s => s.Members.Length);
            if (_catalogue.TryGetValue(id, out var existing))
            {
                // Verify accepted bytes, even when capacity is exhausted. Never trust a filename as proof.
                if (existing != length) throw Storage();
                ReadCore(id, cancellationToken);
                return new(id, length, spans, true);
            }
            if (_catalogue.Count >= _options.MaxRecords || length > _options.MaxStoredBytes - _bytes)
                throw new OtlpIngestionException("storage", "observation retention capacity reached");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(_scratch, Path.Combine(_records, id + ".json"), overwrite: false);
            // Publication is the commit point: do not throw cancellation after accepted evidence exists.
            _catalogue.Add(id, length);
            _bytes += length;
            return new(id, length, spans, false);
        }
        finally
        {
            // Failure to remove scratch is recoverable on the next call/startup and cannot negate acceptance.
            try { ClearScratch(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Monitor.Exit(_gate);
        }
    }

    /// <inheritdoc />
    public ImmutableArray<OtlpObservationEntry> List(CancellationToken cancellationToken = default)
    {
        Enter(cancellationToken);
        try { return _catalogue.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new OtlpObservationEntry(p.Key, p.Value)).ToImmutableArray(); }
        finally { Monitor.Exit(_gate); }
    }

    /// <inheritdoc />
    public OtlpObservationRecord Read(string id, CancellationToken cancellationToken = default)
    {
        Enter(cancellationToken);
        try { return ReadCore(id, cancellationToken); }
        finally { Monitor.Exit(_gate); }
    }

    private OtlpObservationRecord ReadCore(string id, CancellationToken cancellationToken)
    {
        ValidateId(id);
        if (!_catalogue.TryGetValue(id, out var length)) throw new OtlpIngestionException("storage", "observation record not found");
        var path = Path.Combine(_records, id + ".json");
        CheckAncestors(_records);
        RequireFile(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != length || length > _options.MaxRecordBytes) throw Storage();
        var bytes = new byte[checked((int)length)];
        file.ReadExactly(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        if ("batch-" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != id) throw Storage();
        OtlpObservationRecord? record;
        try { record = JsonSerializer.Deserialize<OtlpObservationRecord>(bytes, JsonOptions); }
        catch (JsonException) { throw Storage(); }
        if (record is null || record.Version != Version || record.Content is null || record.Sessions.IsDefault) throw Storage();
        cancellationToken.ThrowIfCancellationRequested();
        return record;
    }

    private void Enter(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Monitor.TryEnter(_gate)) throw new OtlpIngestionException("busy", "observation store is busy");
        if (_disposed) { Monitor.Exit(_gate); throw new ObjectDisposedException(nameof(OtlpObservationStore)); }
    }

    private void ClearScratch()
    {
        CheckFileIfPresent(_scratch);
        if (!File.Exists(_scratch)) return;
        if (new FileInfo(_scratch).Length > _options.MaxRecordBytes) throw Storage();
        File.Delete(_scratch);
    }

    private static void CheckAncestors(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0) throw Storage();
        }
    }
    private static void CheckFileIfPresent(string path)
    {
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        RequireFile(path);
    }
    private static void RequireFile(string path)
    {
        if (AefFolder.KindOf(path) != AefEntryKind.File) throw Storage();
    }
    private static void ValidateId(string id)
    {
        if (id is null || id.Length != 70 || !id.StartsWith("batch-", StringComparison.Ordinal)
            || !id.AsSpan(6).ToArray().All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) throw Storage();
    }
    private static string Hash(Stream stream, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 << 10];
        int read;
        while ((read = stream.Read(buffer)) != 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    private static OtlpIngestionException Storage() => new("storage", "invalid, changed or unsupported observation storage");

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _lease.Dispose(); }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(OtlpValue)) return;
            info.PolymorphismOptions = new JsonPolymorphismOptions { TypeDiscriminatorPropertyName = "$kind" };
            foreach (var (type, name) in new (Type, string)[] {
                (typeof(OtlpValue.Empty), "empty"), (typeof(OtlpValue.Text), "text"),
                (typeof(OtlpValue.Boolean), "boolean"), (typeof(OtlpValue.Integer), "integer"),
                (typeof(OtlpValue.Real), "real"), (typeof(OtlpValue.Bytes), "bytes"),
                (typeof(OtlpValue.Array), "array"), (typeof(OtlpValue.KeyValues), "keyvalues") })
                info.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(type, name));
        });
        var options = new JsonSerializerOptions { TypeInfoResolver = resolver, MaxDepth = 256, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        options.Converters.Add(new FieldsConverter());
        return options;
    }

    // ImmutableDictionary enumeration depends on process-randomized string hashes. Sort on disk so an
    // exact retry remains exact after process restart, independent of dictionary construction order.
    private sealed class FieldsConverter : JsonConverter<ImmutableDictionary<string, OtlpNormalizedField>>
    {
        public override ImmutableDictionary<string, OtlpNormalizedField> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            (JsonSerializer.Deserialize<Dictionary<string, OtlpNormalizedField>>(ref reader, options) ?? throw new JsonException())
            .ToImmutableDictionary(StringComparer.Ordinal);
        public override void Write(Utf8JsonWriter writer, ImmutableDictionary<string, OtlpNormalizedField> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var pair in value.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(pair.Key);
                JsonSerializer.Serialize(writer, pair.Value, options);
            }
            writer.WriteEndObject();
        }
    }

    private sealed class BoundedWriteStream(Stream target, long maximum, CancellationToken token) : Stream
    {
        private long _written;
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            if (buffer.Length > maximum - _written) throw new OtlpIngestionException("limit", "serialized observation record exceeds byte budget");
            target.Write(buffer); _written += buffer.Length;
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() => target.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
