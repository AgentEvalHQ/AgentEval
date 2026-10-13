// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;

namespace AgentEval.Indexing.Tests;

public sealed class SchemaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-index-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_root, "index.sqlite");
    private EvidenceIndexSchemaInfo Ensure(long maximum = 512L << 20) => new SqliteEvidenceIndexSchema().EnsureCurrent(new()
    { DatabasePath = Database, MaxDatabaseBytes = maximum });
    private SqliteConnection Open() => SqliteEvidenceIndexSchema.Open(Database, 1);
    private static void Sql(SqliteConnection connection, string sql) => SqliteEvidenceIndexSchema.Execute(connection, null, sql);

    [Fact]
    public void Ensure_EmptyDatabase_InitializesAndReopensWithoutChangingIdentity()
    {
        var first = Ensure();
        Assert.Equal(1, first.Version);
        Assert.Equal(0, first.Generation);
        Assert.True(Guid.TryParseExact(first.InstanceId, "N", out _));
        Assert.Equal(first, Ensure());
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        Assert.Equal(10L, command.ExecuteScalar());
    }

    [Theory]
    [InlineData("PRAGMA user_version=2", "unsupported-schema")]
    [InlineData("PRAGMA application_id=123", "unsupported-schema")]
    [InlineData("ALTER TABLE sources ADD COLUMN unexpected TEXT", "schema-drift")]
    [InlineData("DROP INDEX ix_spans_group", "schema-drift")]
    [InlineData("UPDATE schema_migrations SET script_sha256=replace(script_sha256,'a','z') || ''", "migration-drift")]
    [InlineData("DELETE FROM index_metadata", "missing-metadata")]
    public void Ensure_IncompatibleOrAlteredDatabase_RefusesWithoutRepairing(string alteration, string code)
    {
        Ensure();
        using (var connection = Open())
        {
            if (code == "migration-drift") alteration = "UPDATE schema_migrations SET script_sha256='" + new string('0', 64) + "'";
            Sql(connection, alteration);
        }
        var before = File.ReadAllBytes(Database);
        Assert.Equal(code, Assert.Throws<EvidenceIndexException>(() => Ensure()).Code);
        Assert.Equal(before, File.ReadAllBytes(Database));
    }

    [Fact]
    public void Ensure_UnrelatedSqliteDatabase_DoesNotAdoptOrModifyIt()
    {
        Directory.CreateDirectory(_root);
        using (var connection = Open()) Sql(connection, "CREATE TABLE owner_data(value TEXT); INSERT INTO owner_data VALUES ('keep');");
        var before = File.ReadAllBytes(Database);
        Assert.Equal("unsupported-schema", Assert.Throws<EvidenceIndexException>(() => Ensure()).Code);
        Assert.Equal(before, File.ReadAllBytes(Database));
    }

    [Fact]
    public void Ensure_CorruptFile_RefusesWithoutDeletingIt()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Database, "not a database");
        Assert.Equal("invalid-database", Assert.Throws<EvidenceIndexException>(() => Ensure()).Code);
        Assert.Equal("not a database", File.ReadAllText(Database));
    }

    [Fact]
    public void Schema_ObservedSource_CannotBePromotedToAnEvaluationRun()
    {
        Ensure();
        using var connection = Open();
        Sql(connection, "INSERT INTO sources VALUES ('observation', 'root', 'batch.json', 'observation', 'digest', 0, 'unverified');");
        Assert.Throws<SqliteException>(() => Sql(connection,
            "INSERT INTO evaluation_runs(source_id,source_kind,run_id,completeness) VALUES ('observation','aef','run','unknown');"));
        Assert.Throws<SqliteException>(() => Sql(connection, "UPDATE sources SET verification='intact' WHERE source_id='observation';"));
    }

    [Fact]
    public void Schema_SourceDelete_CascadesResultsAndMetrics()
    {
        Ensure();
        using var connection = Open();
        Sql(connection, """
            INSERT INTO sources VALUES ('a', 'root', 'run', 'aef', 'digest', 0, 'intact');
            INSERT INTO evaluation_runs(source_id,source_kind,run_id,completeness) VALUES ('a','aef','run','unknown');
            INSERT INTO evaluation_results(source_id,result_id,outcome) VALUES ('a','result','passed');
            INSERT INTO metric_values(source_id,result_id,metric_id,ordinal,score,provenance) VALUES ('a','result','metric',0,0.5,'reported');
            DELETE FROM sources WHERE source_id='a';
            """);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT count(*) FROM evaluation_runs)+(SELECT count(*) FROM evaluation_results)+(SELECT count(*) FROM metric_values)";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void Ensure_SizeBudgetDuringInitialization_RollsBackAllSchemaChanges()
    {
        Assert.Equal("size-limit", Assert.Throws<EvidenceIndexException>(() => Ensure(64 << 10)).Code);
        using (var connection = Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%'";
            Assert.Equal(0L, command.ExecuteScalar());
        }
        Assert.Equal(1, Ensure().Version);
    }

    [Fact]
    public void Ensure_CancelledOrRelativePath_DoesNotCreateStorage()
    {
        var schema = new SqliteEvidenceIndexSchema();
        Assert.Throws<OperationCanceledException>(() => schema.EnsureCurrent(new() { DatabasePath = Database }, new(true)));
        Assert.Throws<ArgumentException>(() => schema.EnsureCurrent(new() { DatabasePath = "relative.sqlite" }));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Ensure_AnotherOwnerHoldsLease_Refuses()
    {
        Ensure();
        using var lease = new FileStream(Database + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => Ensure());
    }

    [Fact]
    public void Schema_ObservedVariantsAndLargeValues_RetainsReportsWithoutRoundingOrConflictConsensus()
    {
        Ensure();
        using var connection = Open();
        Sql(connection, """
            INSERT INTO sources VALUES ('o','root','batch','observation','digest',0,'unverified');
            INSERT INTO observation_batches VALUES ('o','observation','receiver','Off','content/1','{}');
            INSERT INTO observed_spans VALUES ('o',0,'group','group/1','Session','map/1','trace','span','','18446744073709551615','18446744073709551615',0,0,0);
            INSERT INTO observed_spans SELECT source_id,1,group_id,grouping_revision,grouping_basis,mapping_revision,trace_id,span_id,parent_span_id,start_nanos,end_nanos,resource_index,scope_index,1 FROM observed_spans WHERE report_ordinal=0;
            INSERT INTO observed_fields VALUES ('o',0,'tokens.input','Present',NULL,9007199254740993,'[]');
            INSERT INTO observed_fields VALUES ('o',1,'tokens.input','Conflict',NULL,NULL,'[]');
            """);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT integer_value FROM observed_fields WHERE report_ordinal=0";
        Assert.Equal(9007199254740993L, command.ExecuteScalar());
        command.CommandText = "SELECT count(*) FROM observed_spans WHERE start_nanos='18446744073709551615'";
        Assert.Equal(2L, command.ExecuteScalar());
        Assert.Throws<SqliteException>(() => Sql(connection, "UPDATE observed_fields SET integer_value=12 WHERE state='Conflict'"));
    }

    [Fact]
    public void Ensure_DanglingForeignKey_RefusesInsteadOfDroppingEvidence()
    {
        Ensure();
        using (var connection = Open()) Sql(connection, "PRAGMA foreign_keys=OFF; INSERT INTO evaluation_results VALUES ('missing','r',NULL,NULL,NULL);");
        Assert.Equal("broken-references", Assert.Throws<EvidenceIndexException>(() => Ensure()).Code);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
