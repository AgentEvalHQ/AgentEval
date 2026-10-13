// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using AgentEval.Results.Runs;
using Microsoft.Data.Sqlite;

namespace AgentEval.Indexing;

/// <summary>Explicit derived database location and startup resource limits.</summary>
public sealed class EvidenceIndexOptions
{
    /// <summary>Absolute path to a dedicated derived SQLite file, never an authoritative evidence file.</summary>
    public required string DatabasePath { get; init; }
    /// <summary>Maximum database bytes checked before opening; default 512 MiB, range 64 KiB..64 GiB.</summary>
    public long MaxDatabaseBytes { get; init; } = 512L << 20;
    /// <summary>SQLite lock wait in seconds; default 2, range 1..30.</summary>
    public int BusyTimeoutSeconds { get; init; } = 2;
}
/// <summary>Schema identity and current committed generation, not an evidence completeness claim.</summary>
public sealed record EvidenceIndexSchemaInfo(int Version, string InstanceId, long Generation);
/// <summary>Creates or migrates a derived index transactionally, refusing unknown or altered schemas.</summary>
public interface IEvidenceIndexSchema
{
    /// <summary>Initializes an empty database or verifies a supported one. Does not discover or index source evidence.</summary>
    EvidenceIndexSchemaInfo EnsureCurrent(EvidenceIndexOptions options, CancellationToken cancellationToken = default);
}
/// <summary>A path-free derived-index refusal. Source evidence is never modified to repair the index.</summary>
public sealed class EvidenceIndexException(string code) : IOException(
    $"Expected: a supported intact derived evidence index. Actual: {code}. Suggestions: inspect configuration or rebuild into a separate index. Because: source files remain authoritative.")
{
    /// <summary>Stable refusal category.</summary>
    public string Code { get; } = code;
}

/// <summary>Transactional schema initialization/migration and strict schema provenance checks.</summary>
public sealed class SqliteEvidenceIndexSchema : IEvidenceIndexSchema
{
    /// <summary>Current derived schema version, independent of AEF and OTLP versions.</summary>
    public const int CurrentVersion = 2;
    internal const int ApplicationId = 0x41454958;
    private static readonly string Script = LoadScript("EvidenceIndexSchema.sql");
    private static readonly string ScriptDigest = Digest(Script);
    private static readonly string ScriptV2 = LoadScript("EvidenceIndexSchemaV2.sql");
    private static readonly string ScriptV2Digest = Digest(ScriptV2);
    private static readonly Lazy<string> ExpectedSchemaV1 = new(() => CreateExpectedSchema(1));
    private static readonly Lazy<string> ExpectedSchema = new(() => CreateExpectedSchema(CurrentVersion));

    /// <inheritdoc />
    public EvidenceIndexSchemaInfo EnsureCurrent(EvidenceIndexOptions options, CancellationToken cancellationToken = default)
        => EnsureCore(options, leaseHeld: false, allowInitialize: true, cancellationToken);

    // Recovery holds the same .lock lease and must never turn an empty/corrupt candidate into a new index.
    internal EvidenceIndexSchemaInfo InspectWhileOwned(EvidenceIndexOptions options, CancellationToken cancellationToken)
        => EnsureCore(options, leaseHeld: true, allowInitialize: false, cancellationToken);

    internal EvidenceIndexSchemaInfo InspectReadOnlyWhileOwned(EvidenceIndexOptions options, CancellationToken cancellationToken)
        => EnsureCore(options, leaseHeld: true, allowInitialize: false, cancellationToken, readOnly: true);

    private EvidenceIndexSchemaInfo EnsureCore(EvidenceIndexOptions options, bool leaseHeld, bool allowInitialize, CancellationToken cancellationToken, bool readOnly = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(options.DatabasePath) || !Path.IsPathFullyQualified(options.DatabasePath)
            || options.MaxDatabaseBytes is < (64 << 10) or > (64L << 30) || options.BusyTimeoutSeconds is < 1 or > 30)
            throw new ArgumentException("Expected: absolute database path and bounded size/timeout options. Actual: invalid index options. " +
                "Suggestions: configure a dedicated derived index. Because: indexing must not infer authoritative storage.", nameof(options));
        var path = Path.GetFullPath(options.DatabasePath);
        if (!OperatingSystem.IsWindows() && !AefFolder.NativeInUse) throw new EvidenceIndexException("unsupported-file-classifier");
        var parent = Path.GetDirectoryName(path)!;
        CheckDirectories(parent);
        CheckFile(path);
        CheckFile(path + ".lock");
        CheckFile(path + ".writer.lock");
        foreach (var suffix in new[] { "-journal", "-wal", "-shm" }) CheckFile(path + suffix);
        if (File.Exists(path) && new FileInfo(path).Length > options.MaxDatabaseBytes) throw new EvidenceIndexException("size-limit");
        Directory.CreateDirectory(parent);
        CheckDirectories(parent);
        using var ownership = leaseHeld ? null : new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            using var connection = Open(path, options.BusyTimeoutSeconds, readOnly);
            using var transaction = connection.BeginTransaction(deferred: readOnly);
            var application = ScalarLong(connection, transaction, "PRAGMA application_id");
            var version = ScalarLong(connection, transaction, "PRAGMA user_version");
            var initialized = false;
            if (application == 0 && version == 0 && ScalarLong(connection, transaction,
                "SELECT count(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%'") == 0)
            {
                if (!allowInitialize) throw new EvidenceIndexException("empty-rebuild-candidate");
                Execute(connection, transaction, Script);
                using var migration = connection.CreateCommand();
                migration.Transaction = transaction;
                migration.CommandText = "INSERT INTO schema_migrations VALUES (1, $digest); INSERT INTO index_metadata VALUES (1, 0, $instance);";
                migration.Parameters.AddWithValue("$digest", ScriptDigest);
                migration.Parameters.AddWithValue("$instance", Guid.NewGuid().ToString("N"));
                migration.ExecuteNonQuery();
                Execute(connection, transaction, $"PRAGMA application_id={ApplicationId}; PRAGMA user_version=1;");
                version = 1; initialized = true;
            }
            else if (application != ApplicationId || version is < 1 or > CurrentVersion)
                throw new EvidenceIndexException("unsupported-schema");
            if (version == 1)
            {
                if (!allowInitialize) throw new EvidenceIndexException("rebuild-schema-upgrade-required");
                if (SchemaDigest(connection, transaction) != ExpectedSchemaV1.Value) throw new EvidenceIndexException("schema-drift");
                VerifyMigrations(connection, transaction, 1);
                Execute(connection, transaction, ScriptV2);
                using var migration = connection.CreateCommand(); migration.Transaction = transaction;
                migration.CommandText = "INSERT INTO schema_migrations VALUES (2, $digest)";
                migration.Parameters.AddWithValue("$digest", ScriptV2Digest); migration.ExecuteNonQuery();
                if (!initialized)
                {
                    if (ScalarLong(connection, transaction, "SELECT generation FROM index_metadata WHERE singleton=1") == long.MaxValue)
                        throw new EvidenceIndexException("generation-limit");
                    Execute(connection, transaction, "UPDATE index_metadata SET generation=generation+1 WHERE singleton=1");
                }
                Execute(connection, transaction, "PRAGMA user_version=2");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (SchemaDigest(connection, transaction) != ExpectedSchema.Value) throw new EvidenceIndexException("schema-drift");
            VerifyMigrations(connection, transaction, CurrentVersion);
            using (var check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = "PRAGMA quick_check(1)";
                if (!Equals(check.ExecuteScalar(), "ok")) throw new EvidenceIndexException("corrupt-index");
                check.CommandText = "PRAGMA foreign_key_check";
                using var reader = check.ExecuteReader();
                if (reader.Read()) throw new EvidenceIndexException("broken-references");
            }
            EvidenceIndexSchemaInfo info;
            using (var metadata = connection.CreateCommand())
            {
                metadata.Transaction = transaction;
                metadata.CommandText = "SELECT instance_id, generation FROM index_metadata WHERE singleton=1";
                using var reader = metadata.ExecuteReader();
                if (!reader.Read()) throw new EvidenceIndexException("missing-metadata");
                info = new(CurrentVersion, reader.GetString(0), reader.GetInt64(1));
                if (!Guid.TryParseExact(info.InstanceId, "N", out _) || info.Generation < 0) throw new EvidenceIndexException("invalid-metadata");
            }
            var pageSize = ScalarLong(connection, transaction, "PRAGMA page_size");
            if (ScalarLong(connection, transaction, "PRAGMA page_count") > options.MaxDatabaseBytes / pageSize)
                throw new EvidenceIndexException("size-limit");
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return info;
        }
        catch (SqliteException error) { throw new EvidenceIndexException(error.SqliteErrorCode is 5 or 6 ? "busy" : "invalid-database"); }
    }

    internal static SqliteConnection Open(string path, int timeout, bool readOnly = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = false,
            ForeignKeys = true, DefaultTimeout = timeout
        }.ToString());
        try { connection.Open(); Execute(connection, null, "PRAGMA trusted_schema=OFF;"); return connection; }
        catch { connection.Dispose(); throw; }
    }
    internal static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; command.ExecuteNonQuery();
    }
    private static long ScalarLong(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
    private static string SchemaDigest(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT type, name, sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type COLLATE BINARY, name COLLATE BINARY";
        using var reader = command.ExecuteReader();
        var text = new StringBuilder(); var count = 0;
        while (reader.Read())
        {
            if (++count > 64) throw new EvidenceIndexException("schema-drift");
            for (var i = 0; i < 3; i++)
            {
                var value = reader.IsDBNull(i) ? "" : reader.GetString(i);
                if (value.Length > 128 * 1024) throw new EvidenceIndexException("schema-drift");
                text.Append(value.Length).Append(':').Append(value);
            }
        }
        return Digest(text.ToString());
    }
    private static void VerifyMigrations(SqliteConnection connection, SqliteTransaction transaction, int version)
    {
        using var migration = connection.CreateCommand(); migration.Transaction = transaction;
        migration.CommandText = "SELECT version, script_sha256 FROM schema_migrations ORDER BY version";
        using var reader = migration.ExecuteReader();
        for (var expected = 1; expected <= version; expected++)
            if (!reader.Read() || reader.GetInt64(0) != expected || reader.GetString(1) != (expected == 1 ? ScriptDigest : ScriptV2Digest))
                throw new EvidenceIndexException("migration-drift");
        if (reader.Read()) throw new EvidenceIndexException("migration-drift");
    }
    private static string CreateExpectedSchema(int version)
    {
        using var connection = Open(":memory:", 2);
        Execute(connection, null, Script);
        if (version >= 2) Execute(connection, null, ScriptV2);
        return SchemaDigest(connection, null);
    }
    private static string LoadScript(string name)
    {
        using var stream = typeof(SqliteEvidenceIndexSchema).Assembly.GetManifestResourceStream("AgentEval.Indexing." + name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static void CheckDirectories(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                throw new EvidenceIndexException("unsafe-path");
        }
    }
    private static void CheckFile(string path)
    {
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if (AefFolder.KindOf(path) != AefEntryKind.File) throw new EvidenceIndexException("unsafe-path");
    }
}
