using System.Text;
using AgentEval.Results.Ingestion;
using AgentEval.Results.Integrity;
using AgentEval.Results.Interop;
using AgentEval.Results.Json;
using AgentEval.Results.Tests.Integrity;
using AgentEval.Results.Tests.Writing;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Tests.Interop;

public class EvalPortRunContextReaderTests
{
    private const string Extension = "ext/" + EvalPortRunContext.ExtensionFile;
    private static EvalPortRunContext Context() => EvalPortRunContext.FromResultSet(
        """{"isolation":"fresh","group":{"group_id":"g","sequence":7}}"""u8);

    [Fact]
    public void Read_SealedContext_ReturnsBoundIdentityAndDetachedDeclaration()
    {
        using var run = new TestRun(); run.Files[Extension] = Context().ToBytes(); run.Write().Seal();
        var expected = run.Verify().RunHash!.Value.Value;
        var result = new EvalPortRunContextReader().Read(run.Dir, expected);
        Assert.Equal(TestRun.RunId, result.RunId); Assert.Equal(expected, result.RunHash);
        Assert.Equal(AefOutcome.Intact, result.Evidence.Outcome);
        Assert.Equal("fresh", result.Context!.Isolation);
        File.AppendAllText(Path.Combine(run.Dir, Extension), " ");
        Assert.Equal("fresh", result.Context.Isolation);
        Assert.Throws<AefInputException>(() => new EvalPortRunContextReader().Read(run.Dir));
    }

    [Fact]
    public void WriteTo_OpenWriter_SealsAndReadsContextWithoutChangingResultOutcome()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        writer.AddResult(WriterRun.Leaf("case", state: AefState.Failed));
        Context().WriteTo(writer);
        writer.Close(AefRunStatus.Completed, WriterRun.Start.AddSeconds(1));
        var seal = AefSealer.Seal(run.Dir);
        var verified = new EvalPortRunContextReader().Read(run.Dir, seal.RunHash);
        Assert.Equal(Context().ToBytes(), verified.Context!.ToBytes());
        Assert.Throws<AefInputException>(() => Context().WriteTo(writer));
    }

    [Fact]
    public void Read_NoExtension_ReturnsExplicitAbsenceWithoutAssumingIsolation()
    {
        using var run = new TestRun().Write().Seal();
        Assert.Null(new EvalPortRunContextReader().Read(run.Dir).Context);
    }

    [Theory]
    [InlineData("unsealed")]
    [InlineData("wrong-hash")]
    [InlineData("invalid-hash")]
    [InlineData("malformed")]
    public void Read_InvalidSelectionOrContext_Refuses(string mode)
    {
        using var run = new TestRun();
        run.Files[Extension] = mode == "malformed" ? "{\"schemaVersion\":\"2.0\"}"u8.ToArray() : Context().ToBytes();
        run.Write(); if (mode != "unsealed") run.Seal();
        var expected = mode == "wrong-hash" ? new string('0', 64) : mode == "invalid-hash" ? "abc" : null;
        Assert.Throws<AefInputException>(() => new EvalPortRunContextReader().Read(run.Dir, expected));
    }

    [Fact]
    public void Read_OversizedSealedExtension_RefusesInsteadOfTruncating()
    {
        using var run = new TestRun();
        run.Files[Extension] = Encoding.UTF8.GetBytes("{\"schemaVersion\":\"1.0\"}" + new string(' ', EvalPortRunContext.MaxBytes));
        run.Write().Seal();
        Assert.Throws<AefLimitException>(() => new EvalPortRunContextReader().Read(run.Dir));
    }

    [Fact]
    public void Read_BudgetAndCancellation_RefusesWithoutContext()
    {
        using var run = new TestRun().Write().Seal();
        var reader = new EvalPortRunContextReader(new() { MaxBytes = 1, MaxFileBytes = 1 });
        Assert.Throws<AefStagingException>(() => reader.Read(run.Dir));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => reader.Read("missing", cancellationToken: canceled.Token));
    }
}
