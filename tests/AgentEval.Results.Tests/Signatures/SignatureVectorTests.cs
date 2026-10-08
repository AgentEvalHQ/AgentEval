using System.Text.Json.Nodes;
using AgentEval.Results.Conformance;
using AgentEval.Results.Tests.Corpus;

namespace AgentEval.Results.Tests.Signatures;

/// <summary>
/// Every vector of <c>conformance/signature-vectors</c>, through the driver's <c>signature</c> operation in-process
/// (<see cref="Program.Dispatch"/>), compared as <c>tools/aef_conformance.py</c> compares them: <c>envelopeResult</c>,
/// <c>signatures</c> and <c>verifiesFor</c>. Fast feedback only: a conformance claim rests on the Python runner.
/// </summary>
public class SignatureVectorTests
{
    public static TheoryData<string> Vectors() => [.. VectorNames()];

    /// <summary>The vectors' folder names: every folder of signature-vectors with an expected.json (not keys/).</summary>
    private static List<string> VectorNames() =>
    [
        .. Directory.GetDirectories(SignatureCorpus.Root)
            .Where(dir => File.Exists(Path.Combine(dir, "expected.json")))
            .Select(dir => Path.GetFileName(dir))
            .Order(StringComparer.Ordinal),
    ];

    [Theory]
    [MemberData(nameof(Vectors))]
    public void TheVector_GivesItsExpectedResult(string vector)
    {
        var dir = Path.Combine(SignatureCorpus.Root, vector);
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "expected.json")))!;
        List<string> args =
        [
            "signature",
            Path.Combine(dir, (string)expected["envelope"]!),
            Path.Combine(dir, (string)expected["file"]!),
            Path.Combine(dir, (string)expected["policy"]!),
        ];
        if ((string?)expected["payloadType"] is { } payloadType)
        {
            args.AddRange(["--payload-type", payloadType]);
        }

        if ((bool?)expected["policyRefused"] == true)
        {
            // [SIG-3]: a policy with a key that cannot be used is refused as a whole: an input error, exit status 2.
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            Assert.Equal(2, Program.Dispatch([.. args], stdout, stderr));
            Assert.Equal("", stdout.ToString());
            return;
        }

        var actual = Run([.. args]);

        AssertSame(expected["envelopeResult"], actual["envelopeResult"], vector, "envelopeResult");
        AssertSame(expected["signatures"], actual["signatures"], vector, "signatures");
        AssertSame(expected["verifiesFor"], actual["verifiesFor"], vector, "verifiesFor");
    }

    [Fact]
    public void EverySignatureVectorOfTheIndex_IsRun()
    {
        var index = JsonNode.Parse(File.ReadAllText(Path.Combine(AefCorpus.Conformance, "index.json")))!;
        var listed = index["vectors"]!.AsArray()
            .Where(v => (string?)v!["kind"] == "signature")
            .Select(v => ((string)v!["path"]!)["signature-vectors/".Length..])
            .Order(StringComparer.Ordinal);
        Assert.Equal(listed, VectorNames());
        Assert.True(VectorNames().Count >= 20);
    }

    [Fact]
    public void WithoutPayloadType_TheOperationIsAUsageError()
    {
        // Spec 09 §9.3: --payload-type (the type [SIG-1] gives the file) is required.
        var dir = Path.Combine(SignatureCorpus.Root, "ecdsa-valid");
        var stderr = new StringWriter();
        var stdout = new StringWriter();
        Assert.Equal(2, Program.Dispatch(
            ["signature", Path.Combine(dir, "envelope.dsse.json"), Path.Combine(dir, "seal.json"), Path.Combine(dir, "policy.json")], stdout, stderr));
        Assert.Equal("", stdout.ToString());
        Assert.Contains("--payload-type", stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData]
    [InlineData("only-one")]
    [InlineData("a", "b")]
    [InlineData("a", "b", "c")]
    [InlineData("a", "b", "c", "d", "--payload-type", "t")]
    [InlineData("a", "b", "c", "--payload-type")]
    [InlineData("a", "b", "c", "--unknown", "x")]
    [InlineData("a", "b", "c", "--payload-type", "t", "--payload-type", "t")]
    public void WrongArguments_AreAUsageError(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Assert.Equal(2, Program.Dispatch(["signature", .. args], stdout, stderr));
        Assert.Equal("", stdout.ToString());
        Assert.NotEqual("", stderr.ToString());
    }

    [Fact]
    public void AMissingFileOrABrokenPolicy_IsAnInputError()
    {
        var dir = Path.Combine(SignatureCorpus.Root, "ecdsa-valid");
        var envelope = Path.Combine(dir, "envelope.dsse.json");
        var seal = Path.Combine(dir, "seal.json");

        // A seal is not a trust policy; a missing file cannot be read.
        foreach (var args in new[] { new[] { envelope, seal, seal }, [envelope, seal, Path.Combine(dir, "no-such-file.json")] })
        {
            var stderr = new StringWriter();
            Assert.Equal(2, Program.Dispatch(["signature", .. args, "--payload-type", "application/vnd.in-toto+json"], new StringWriter(), stderr));
            Assert.NotEqual("", stderr.ToString());
        }
    }

    private static JsonNode Run(string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Assert.True(Program.Dispatch(args, stdout, stderr) == 0, stderr.ToString());
        return JsonNode.Parse(stdout.ToString())!;
    }

    private static void AssertSame(JsonNode? expected, JsonNode? actual, string vector, string field) =>
        Assert.True(JsonNode.DeepEquals(expected, actual),
            $"{vector}: {field}: expected {expected?.ToJsonString() ?? "null"}, got {actual?.ToJsonString() ?? "null"}");
}
