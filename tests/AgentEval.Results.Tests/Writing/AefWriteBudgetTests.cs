using AgentEval.Results.Signatures;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Tests.Writing;

public sealed class AefWriteBudgetTests
{
    [Fact]
    public void Create_InsufficientBudget_DoesNotCreatePartialRun()
    {
        using var run = new WriterRun();
        Assert.Throws<InvalidDataException>(() => AefRunWriter.Create(run.Dir, WriterRun.Header(), new(1)));
        Assert.False(Directory.Exists(run.Dir));
    }

    [Theory]
    [InlineData("append")]
    [InlineData("rewrite")]
    [InlineData("blob")]
    [InlineData("stream")]
    [InlineData("close")]
    public void Write_InsufficientBudget_DoesNotWriteAnyBytes(string operation)
    {
        using var run = new WriterRun();
        var budget = new AefWriteBudget(100_000);
        var writer = AefRunWriter.Create(run.Dir, WriterRun.Header(), budget);
        var before = Snapshot(run);
        budget.Charge(budget.Remaining - 1);
        Assert.Throws<InvalidDataException>(() =>
        {
            switch (operation)
            {
                case "append": writer.AddResult(WriterRun.Leaf("case", m: null)); break;
                case "rewrite": writer.SetMetrics([WriterRun.Metric()]); break;
                case "blob": writer.PutBlob(new byte[] { 1, 2 }); break;
                case "stream": writer.PutBlob(new MemoryStream([1, 2])); break;
                case "close": writer.Close(AefRunStatus.Completed, WriterRun.Start.AddSeconds(1)); break;
            }
        });
        Assert.Equal(before, Snapshot(run));
        Assert.Equal(AefRunStatus.Running, writer.Status);
    }

    [Fact]
    public void Seal_InsufficientBudget_DoesNotWriteSeal()
    {
        using var run = new WriterRun();
        run.WriteSmall();
        var before = Snapshot(run);
        Assert.Throws<InvalidDataException>(() => AefSealer.Seal(run.Dir, new() { WriteBudget = new(1) }));
        Assert.Equal(before, Snapshot(run));
    }

    [Fact]
    public void Attestation_InsufficientBudget_RemovesSealAndAttestation()
    {
        using var control = new WriterRun(); control.WriteSmall();
        AefSealer.Seal(control.Dir, new() { SealedAt = "2026-10-01T11:00:00Z" });
        var sealBytes = control.Read("seal.json").Length;
        using var run = new WriterRun(); run.WriteSmall();
        using var signer = EcdsaP256Signer.Generate();
        var before = Snapshot(run);
        Assert.Throws<InvalidDataException>(() => AefSealer.Seal(run.Dir, new()
        {
            SealedAt = "2026-10-01T11:00:00Z", Signer = signer, WriteBudget = new(sealBytes),
        }));
        Assert.Equal(before, Snapshot(run));
    }

    [Fact]
    public void Budget_RewritesAndTemporaryCopies_ChargeCumulativeWork()
    {
        using var run = new WriterRun();
        long charged = 0;
        var budget = new AefWriteBudget(100_000, chargeParent: bytes => charged += bytes);
        var writer = AefRunWriter.Create(run.Dir, WriterRun.Header(), budget);
        var before = charged;
        writer.PutBlob(new byte[] { 1, 2 });
        writer.PutBlob(new byte[] { 1, 2 }); // Span deduplication avoids another write.
        Assert.Equal(before + 2, charged);
        writer.PutBlob(new MemoryStream([1, 2])); // The discarded temporary copy still used disk.
        Assert.Equal(before + 4, charged);
        var beforeRewrite = charged;
        writer.SetMetrics([WriterRun.Metric()]);
        Assert.Equal(beforeRewrite + run.Read("metrics.json").Length, charged);
        Assert.Equal(100_000 - charged, budget.Remaining);
    }

    [Fact]
    public void Create_CancelledBudget_DoesNotCreateRun()
    {
        using var run = new WriterRun();
        Assert.Throws<OperationCanceledException>(() => AefRunWriter.Create(run.Dir, WriterRun.Header(), new(100_000, new(true))));
        Assert.False(Directory.Exists(run.Dir));
    }

    private static string Snapshot(WriterRun run) => string.Join("\n", run.Snapshot().OrderBy(p => p.Key, StringComparer.Ordinal)
        .Select(p => p.Key + ":" + Convert.ToBase64String(p.Value)));
}
