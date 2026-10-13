// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Indexing;
using AgentEval.Results.Suites;

namespace AgentEval.MissionControl.Core.Tests;

public sealed class SuiteQueryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-suite-query-" + Guid.NewGuid().ToString("N"));
    private EvidenceIndexOptions Options => new() { DatabasePath = Path.Combine(_root, "index.sqlite") };
    private EvidenceIndexRoot Root => new("root", Path.Combine(_root, "evidence"), IndexedEvidenceKind.Aef);
    public SuiteQueryServiceTests() => Directory.CreateDirectory(Root.Directory);
    private SqliteEvidenceIndexWriter Writer() => new(Options, new SqliteEvidenceIndexSchema());
    private SuiteQueryService Service(params EvidenceIndexRoot[] roots) => new(new SqliteEvidenceSourceLookup(Options),
        new SuiteIdentityReader(), new SuiteCompatibilityEvaluator(), roots);

    private string Copy(string name)
    {
        string? fixture = null;
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
        {
            var candidate = Path.Combine(path.FullName, "contracts/aef/1/interop/examples/otel-aef/run");
            if (Directory.Exists(candidate)) { fixture = candidate; break; }
        }
        Assert.NotNull(fixture); var target = Path.Combine(Root.Directory, name);
        foreach (var source in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(fixture, source));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination);
        }
        return target;
    }
    private string Import(SqliteEvidenceIndexWriter writer, string name) =>
        writer.Upsert(new AefIndexSourceReader(new SuiteIdentityReader()).Read("root", name, Copy(name))).SourceId;

    [Fact]
    public void IdentityAndCompare_SealedFixtures_VerifySelectedHashesWithoutInventingCompatibility()
    {
        using var writer = Writer(); writer.BindRoots([Root]);
        var first = Import(writer, "first"); var second = Import(writer, "second"); var service = Service(Root);
        var identity = service.Identity(first);
        Assert.Equal(QueryState.Available, identity.State); Assert.Equal("otel-import-0001", identity.Data!.Identity.RunId);
        Assert.Equal(64, identity.Data.Identity.RunHash.Length);
        var compared = service.Compare(first, second);
        Assert.Equal(QueryState.Available, compared.State);
        Assert.False(compared.Data!.Compatibility.Compatible);
        Assert.Equal("definition-required", compared.Data.Compatibility.Reason);
        Assert.Equal(compared.Data.Baseline.IndexGeneration, compared.Data.Candidate.IndexGeneration);
    }

    [Fact]
    public void Identity_UnconfiguredOrUnboundOrReboundRoot_DoesNotReadEvidence()
    {
        using var writer = Writer(); var id = Import(writer, "first");
        Assert.Equal(QueryState.NotFound, Service().Identity(id).State);
        Assert.Equal("root-binding-mismatch", Service(Root).Identity(id).Code);
        // A separate index with a persisted root cannot be redirected by a different runtime configuration.
        var options = new EvidenceIndexOptions { DatabasePath = Path.Combine(_root, "bound.sqlite") };
        using var boundWriter = new SqliteEvidenceIndexWriter(options, new SqliteEvidenceIndexSchema());
        boundWriter.BindRoots([Root]);
        var boundId = boundWriter.Upsert(new AefIndexSourceReader(new SuiteIdentityReader()).Read("root", "first", Path.Combine(Root.Directory, "first"))).SourceId;
        var other = Directory.CreateDirectory(Path.Combine(_root, "other")).FullName;
        var service = new SuiteQueryService(new SqliteEvidenceSourceLookup(options), new SuiteIdentityReader(),
            new SuiteCompatibilityEvaluator(), [Root with { Directory = other }]);
        Assert.Equal("root-binding-mismatch", service.Identity(boundId).Code);
    }

    [Fact]
    public void Identity_SourceChangesAfterIndexing_RefusesWithoutReturningOldIdentity()
    {
        using var writer = Writer(); writer.BindRoots([Root]); var id = Import(writer, "first");
        File.AppendAllText(Path.Combine(Root.Directory, "first", "run.json"), " ");
        var result = Service(Root).Identity(id);
        Assert.Equal(QueryState.Unavailable, result.State); Assert.Null(result.Data); Assert.Equal("invalid-sealed-evidence", result.Code);
    }

    [Fact]
    public void Compare_IndexRevisionChangesBetweenSelections_RequiresReselection()
    {
        using var writer = Writer(); writer.BindRoots([Root]); var first = Import(writer, "first"); var second = Import(writer, "second");
        var lookup = new ChangingLookup(new SqliteEvidenceSourceLookup(Options));
        var service = new SuiteQueryService(lookup, new SuiteIdentityReader(), new SuiteCompatibilityEvaluator(), [Root]);
        var result = service.Compare(first, second);
        Assert.Equal(QueryState.StaleCursor, result.State); Assert.Equal("selection-changed", result.Code); Assert.Null(result.Data);
    }

    [Fact]
    public void Identity_InvalidMissingOrCancelledRequest_RemainsExplicit()
    {
        using var writer = Writer(); var service = Service(Root);
        Assert.Equal(QueryState.InvalidRequest, service.Identity("../run").State);
        Assert.Equal(QueryState.NotFound, service.Identity(new string('0', 64)).State);
        Assert.Throws<OperationCanceledException>(() => service.Identity(new string('0', 64), new(true)));
    }

    private sealed class ChangingLookup(IEvidenceSourceLookup inner) : IEvidenceSourceLookup
    {
        private int _count;
        public EvidenceSourceSelection? Find(string sourceId, CancellationToken cancellationToken = default)
        {
            var selected = inner.Find(sourceId, cancellationToken);
            return selected is null ? null : selected with { Generation = selected.Generation + _count++ };
        }
    }
    public void Dispose() => Directory.Delete(_root, recursive: true);
}
