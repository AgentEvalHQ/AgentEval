using System.Text.Json.Nodes;
using AgentEval.Results.Runs;
using AgentEval.Results.Tests.Corpus;

namespace AgentEval.Results.Tests.Runs;

/// <summary>[RES-4]: result ids.</summary>
public class AefResultIdTests
{
    [Fact]
    public void TheCorpusVectors()
    {
        var vectors = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AefCorpus.Conformance, "result-ids.json")))!.AsArray();

        Assert.NotEmpty(vectors);
        foreach (var v in vectors)
        {
            long? trial = v!["trial"] is { } t ? (long)(double)t : null;
            Assert.Equal((string)v["resultId"]!, AefResultId.Compute((string)v["runId"]!, (string)v["caseId"]!, (string)v["path"]!, trial));
        }
    }

    [Fact]
    public void ALineWithoutATrial_DiffersFromTrialZero()
    {
        Assert.NotEqual(AefResultId.Compute("r", "c", "p"), AefResultId.Compute("r", "c", "p", 0));
        Assert.Matches("^r_[0-9a-f]{32}$", AefResultId.Compute("r", "c", "p"));
    }

    [Theory]
    [InlineData("r", "c\u001Fx", "p")]
    [InlineData("r", "c", "p\n")]
    [InlineData("r", "c", "p\u007F")]
    [InlineData("r", "c\u0085", "p")]
    [InlineData("r\u001F", "c", "p")]
    public void AControlCharacter_IsRefused(string runId, string caseId, string path)
    {
        Assert.ThrowsAny<ArgumentException>(() => AefResultId.Compute(runId, caseId, path));
    }

    [Fact]
    public void AnUnpairedSurrogate_IsRefused()
    {
        // Built at run time: xunit carries a lone surrogate in InlineData as U+FFFD.
        Assert.ThrowsAny<ArgumentException>(() => AefResultId.Compute("r", "c" + (char)0xD800, "p"));
        Assert.ThrowsAny<ArgumentException>(() => AefResultId.Compute("r", "c", (char)0xDC00 + "p"));
    }

    [Fact]
    public void ANegativeTrial_IsRefused()
    {
        Assert.ThrowsAny<ArgumentException>(() => AefResultId.Compute("r", "c", "p", -1));
    }
}
