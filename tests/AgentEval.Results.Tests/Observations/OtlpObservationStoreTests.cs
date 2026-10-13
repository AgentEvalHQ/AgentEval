// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Results.Observations;
using AgentEval.Results.Tests.Corpus;

namespace AgentEval.Results.Tests.Observations;

public sealed class OtlpObservationStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-observations-" + Guid.NewGuid().ToString("N"));
    private OtlpObservationStore Open(OtlpContentCapture capture = OtlpContentCapture.Off, int records = 4096,
        int recordBytes = 16 << 20, long storedBytes = 512L << 20) => new(new()
        { Root = _root, Source = "receiver", Capture = capture, MaxRecords = records, MaxRecordBytes = recordBytes, MaxStoredBytes = storedBytes },
        new OtlpContentPolicy(), new OtlpSessionGrouper(new OtlpTraceNormalizer()));
    private static OtlpTraceBatch Batch(string model = "model") => new OtlpJsonTraceDecoder().Decode(Encoding.UTF8.GetBytes("""
        {"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"service"}}]},
        "scopeSpans":[{"spans":[{"traceId":"0123456789abcdef0123456789abcdef","spanId":"0123456789abcdef",
        "name":"private-prompt","attributes":[{"key":"gen_ai.request.model","value":{"stringValue":"MODEL"}},
        {"key":"session.id","value":{"stringValue":"session"}},{"key":"private","value":{"stringValue":"private-payload"}}]}]}]}]}
        """.Replace("MODEL", model)));

    [Fact]
    public void Accept_DefaultOff_PersistsOnlyFilteredEvidenceAndGrouping()
    {
        using var store = Open();
        var receipt = store.Accept(Batch());
        Assert.False(receipt.AlreadyAccepted);
        Assert.Equal(1, receipt.ReportedSpans);
        Assert.Equal(receipt.Bytes, Assert.Single(store.List()).Bytes);
        var path = Path.Combine(_root, "records", receipt.Id + ".json");
        var bytes = File.ReadAllText(path);
        Assert.DoesNotContain("private-prompt", bytes);
        Assert.DoesNotContain("private-payload", bytes);
        var record = store.Read(receipt.Id);
        Assert.Equal("receiver", record.Source);
        Assert.Equal(OtlpContentCapture.Off, record.Content.Capture);
        Assert.Equal(1, record.Content.Removals.Attributes);
        Assert.Equal(1, record.Content.Removals.SpanNames);
        Assert.Equal(OtlpGroupingBasis.Session, Assert.Single(Assert.Single(record.Sessions).Members).Basis);
        Assert.False(File.Exists(Path.Combine(_root, "incoming.tmp")));
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("wire-scalars")]
    [InlineData("merge-and-unknown")]
    public void Accept_ExplicitOn_CompleteIndependentWireFixtureRoundTrips(string name)
    {
        var input = new OtlpProtobufTraceDecoder().Decode(File.ReadAllBytes(Path.Combine(AefCorpus.RepoRoot,
            "tests", "AgentEval.Results.Tests", "Observations", "Fixtures", name + ".pb")));
        using var store = Open(OtlpContentCapture.On);
        var receipt = store.Accept(input);
        Assert.Equivalent(input, store.Read(receipt.Id).Content.Batch, strict: true);
    }

    [Fact]
    public void Accept_ExactRetryAtCapacityAndAfterRestart_VerifiesWithoutDuplicating()
    {
        OtlpObservationReceipt receipt;
        using (var store = Open(records: 1))
        {
            receipt = store.Accept(Batch());
            Assert.True(store.Accept(Batch()).AlreadyAccepted);
            Assert.Equal("storage", Assert.Throws<OtlpIngestionException>(() => store.Accept(Batch("other"))).Code);
            Assert.Single(store.List());
        }
        using var reopened = Open(records: 1);
        var retry = reopened.Accept(Batch());
        Assert.True(retry.AlreadyAccepted);
        Assert.Equal(receipt.Id, retry.Id);
        Assert.Single(reopened.List());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Accept_RecordOrTotalByteLimit_RefusesWithoutAcceptedOrScratchEvidence(bool perRecord)
    {
        using var store = Open(recordBytes: perRecord ? 20 : 16 << 20, storedBytes: perRecord ? 512L << 20 : 20);
        Assert.Equal(perRecord ? "limit" : "storage", Assert.Throws<OtlpIngestionException>(() => store.Accept(Batch())).Code);
        Assert.Empty(store.List());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "records")));
        Assert.False(File.Exists(Path.Combine(_root, "incoming.tmp")));
    }

    [Fact]
    public void ReadOrRetry_CorruptedAcceptedBytes_RefusesWithoutOverwriting()
    {
        using var store = Open();
        var receipt = store.Accept(Batch());
        var path = Path.Combine(_root, "records", receipt.Id + ".json");
        var contents = File.ReadAllBytes(path);
        contents[0] = (byte)'[';
        File.WriteAllBytes(path, contents);
        Assert.Throws<OtlpIngestionException>(() => store.Read(receipt.Id));
        Assert.Throws<OtlpIngestionException>(() => store.Accept(Batch()));
        Assert.Equal(contents, File.ReadAllBytes(path));
    }

    [Fact]
    public void Constructor_InterruptedScratch_CleansOnlyScratchAndRetainsAcceptedRecord()
    {
        string id;
        using (var store = Open()) id = store.Accept(Batch()).Id;
        File.WriteAllText(Path.Combine(_root, "incoming.tmp"), "partial");
        using var reopened = Open();
        Assert.False(File.Exists(Path.Combine(_root, "incoming.tmp")));
        Assert.Equal(id, Assert.Single(reopened.List()).Id);
        Assert.NotNull(reopened.Read(id));
    }

    [Fact]
    public void Constructor_SecondOwner_RefusesUntilDisposed()
    {
        using (var first = Open()) Assert.Throws<IOException>(() => Open());
        using var second = Open();
        Assert.Empty(second.List());
    }

    [Fact]
    public void Constructor_UnrecognizedRoot_PreservesExistingContent()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "owner-file");
        File.WriteAllText(path, "keep");
        Assert.Throws<OtlpIngestionException>(() => Open());
        Assert.Equal("keep", File.ReadAllText(path));
    }

    [Fact]
    public void Constructor_UnexpectedRecordOrOversizedScratch_RefusesWithoutDeleting()
    {
        using (Open()) { }
        var scratch = Path.Combine(_root, "incoming.tmp");
        File.WriteAllText(scratch, new string('x', 21));
        Assert.Throws<OtlpIngestionException>(() => Open(recordBytes: 20));
        Assert.True(File.Exists(scratch));
        File.Delete(scratch);
        var unknown = Path.Combine(_root, "records", "unknown.json");
        File.WriteAllText(unknown, "keep");
        Assert.Throws<OtlpIngestionException>(() => Open());
        Assert.Equal("keep", File.ReadAllText(unknown));
    }

    [Fact]
    public void Operations_CancelledDisposedOrInvalidId_Refuse()
    {
        using var store = Open();
        Assert.Throws<OperationCanceledException>(() => store.Accept(Batch(), new(true)));
        Assert.Throws<OperationCanceledException>(() => store.List(new(true)));
        Assert.Throws<OtlpIngestionException>(() => store.Read("../anything"));
        Assert.Empty(store.List());
        store.Dispose();
        Assert.Throws<ObjectDisposedException>(() => store.Accept(Batch()));
    }

    [Fact]
    public async Task Accept_ConcurrentWriter_RefusesWithoutQueueing()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var store = new OtlpObservationStore(new() { Root = _root, Source = "receiver" },
            new BlockingPolicy(entered, release), new OtlpSessionGrouper(new OtlpTraceNormalizer()));
        var first = Task.Run(() => store.Accept(Batch()));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal("busy", Assert.Throws<OtlpIngestionException>(() => store.Accept(Batch())).Code);
        }
        finally { release.Set(); }
        await first;
        Assert.Single(store.List());
    }

    [Fact]
    public void Constructor_InvalidConfiguration_DoesNotCreateRoot()
    {
        Assert.Throws<ArgumentException>(() => Open(recordBytes: 0));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Constructor_LinkedScratch_RefusesWithoutDeletingTarget()
    {
        if (OperatingSystem.IsWindows()) return; // Unix test does not require symlink privileges.
        using (Open()) { }
        var target = Path.Combine(_root, ".observations-version");
        var scratch = Path.Combine(_root, "incoming.tmp");
        File.CreateSymbolicLink(scratch, target);
        Assert.Throws<OtlpIngestionException>(() => Open());
        Assert.Equal("1\n", File.ReadAllText(target));
        Assert.NotNull(new FileInfo(scratch).LinkTarget);
    }

    [Fact]
    public void Accept_ExactTotalByteBoundary_AllowsRecordAndRetry()
    {
        long bytes;
        using (var first = Open()) bytes = first.Accept(Batch()).Bytes;
        using var reopened = Open(storedBytes: bytes);
        Assert.True(reopened.Accept(Batch()).AlreadyAccepted);
        Assert.Equal("storage", Assert.Throws<OtlpIngestionException>(() => reopened.Accept(Batch("another"))).Code);
    }

    private sealed class BlockingPolicy(ManualResetEventSlim entered, ManualResetEventSlim release) : IOtlpContentPolicy
    {
        public OtlpContentSnapshot Apply(OtlpTraceBatch batch, OtlpContentCapture capture = OtlpContentCapture.Off,
            CancellationToken cancellationToken = default)
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10), cancellationToken)) throw new TimeoutException();
            return new OtlpContentPolicy().Apply(batch, capture, cancellationToken);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
