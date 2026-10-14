using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AgentEval.Cli.Commands;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Signatures;
using AgentEval.Results.Writing;
using Xunit;

namespace AgentEval.Tests.Cli.Aef;

[Collection("ConsoleTests")]
public sealed class ArtifactCompanionCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-companion-cli-" + Guid.NewGuid().ToString("N"));
    private static readonly string Digest = new('a', 64);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private string Run()
    {
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        return AefProbeRunWriter.Write(_root, [new() { CaseId = "a", State = AefState.Failed }],
            new AefProbeRunOptions { SubjectRef = "agent:test", TargetMode = AefTargetMode.Mocked,
                SuiteRef = "redteam:test", Seal = true }, start, start.AddSeconds(1)).Directory;
    }

    [Fact]
    public void CreateAndVerify_SignedBundle_ReportsTrustAndRequiresExactIdentity()
    {
        var run = Run(); var output = Path.Combine(_root, "companion");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyPath = Path.Combine(_root, "key.pem"); File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        using var signer = new EcdsaP256Signer(key);
        var policy = Path.Combine(_root, "policy.json");
        File.WriteAllText(policy, new TrustPolicy([new TrustedKey("producer", signer.PublicKey)]).ToJson().ToJsonString());
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        Assert.Equal(0, AefCommand.RunCompanionCreate(run, Digest, "agent", output, keyPath, null, true, stdout, stderr));
        Assert.True((bool)JsonNode.Parse(stdout.ToString())!["signed"]!);
        var doc = Path.Combine(output, "artifact-evidence.json"); var env = Path.Combine(output, "artifact-evidence.dsse.json");
        stdout.GetStringBuilder().Clear();
        Assert.Equal(0, AefCommand.RunCompanionVerify(run, Digest, doc, env, policy, "producer", true, stdout, stderr));
        var report = JsonNode.Parse(stdout.ToString())!;
        Assert.True((bool)report["intactBinding"]!);
        Assert.Equal("verified", (string?)report["signatureStatus"]);
        Assert.Equal(1, AefCommand.RunCompanionVerify(run, Digest, doc, env, policy, "someone-else", true, TextWriter.Null, stderr));
        Assert.Equal(1, AefCommand.RunCompanionVerify(run, new string('b', 64), doc, env, policy, null, true, TextWriter.Null, stderr));
        Assert.Equal(2, AefCommand.RunCompanionVerify(run, Digest, doc, env, null, "producer", true, TextWriter.Null, TextWriter.Null));
        Assert.Empty(stderr.ToString());
    }

    [Theory]
    [InlineData("inside")]
    [InlineData("existing")]
    [InlineData("key")]
    public void Create_InvalidDestinationOrKey_DoesNotPublishPartialBundle(string mode)
    {
        var run = Run(); var output = mode == "inside" ? Path.Combine(run, "new") : Path.Combine(_root, "bundle");
        if (mode == "existing") Directory.CreateDirectory(output);
        Assert.Equal(2, AefCommand.RunCompanionCreate(run, Digest, "agent", output,
            mode == "key" ? Path.Combine(_root, "missing.pem") : null, null, true, TextWriter.Null, TextWriter.Null));
        Assert.Equal(mode == "existing", Directory.Exists(output));
        Assert.Empty(Directory.GetDirectories(_root, ".ae-companion-*"));
    }

    [Fact]
    public async Task Commands_UnsignedRoundtrip_BindsArgumentsAndReportsUnsigned()
    {
        var run = Run(); var output = Path.Combine(_root, "bundle");
        var command = AefCommand.Create();
        var previous = Console.Out; using var stdout = new StringWriter(); Console.SetOut(stdout);
        try
        {
            var create = command.Parse(["companion", "create", run, "--artifact-sha256", Digest, "--output", output, "--json"]);
            Assert.Empty(create.Errors); Assert.Equal(0, await create.InvokeAsync());
            stdout.GetStringBuilder().Clear();
            var verify = command.Parse(["companion", "verify", run, "--artifact-sha256", Digest, "--document", Path.Combine(output, "artifact-evidence.json"), "--json"]);
            Assert.Empty(verify.Errors); Assert.Equal(0, await verify.InvokeAsync());
            Assert.Equal("unsigned", (string?)JsonNode.Parse(stdout.ToString())!["signatureStatus"]);
        }
        finally { Console.SetOut(previous); }
    }

    [Fact]
    public void Verify_MalformedEnvelope_ReturnsFailedCheckAndNoTrustedIdentity()
    {
        var run = Run(); var output = Path.Combine(_root, "bundle");
        Assert.Equal(0, AefCommand.RunCompanionCreate(run, Digest, "agent", output, null, null, true, TextWriter.Null, TextWriter.Null));
        var envelope = Path.Combine(_root, "bad.json"); File.WriteAllText(envelope, "{}");
        using var stdout = new StringWriter();
        Assert.Equal(1, AefCommand.RunCompanionVerify(run, Digest, Path.Combine(output, "artifact-evidence.json"), envelope,
            null, null, true, stdout, TextWriter.Null));
        var report = JsonNode.Parse(stdout.ToString())!;
        Assert.True((bool)report["intactBinding"]!);
        Assert.Equal("Malformed", (string?)report["signatureStatus"]);
        Assert.Empty(report["verifiedSigners"]!.AsArray());
    }

    [Fact]
    public void Create_Cancelled_DoesNotPublish()
    {
        var run = Run(); var output = Path.Combine(_root, "bundle");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => AefCommand.RunCompanionCreate(run, Digest, "agent", output,
            null, null, true, TextWriter.Null, TextWriter.Null, cancellation.Token));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Verify_OversizedInput_RefusesBeforeParsing()
    {
        var run = Run(); var doc = Path.Combine(_root, "oversized.json");
        File.WriteAllBytes(doc, new byte[65537]);
        using var stdout = new StringWriter();
        Assert.Equal(2, AefCommand.RunCompanionVerify(run, Digest, doc, null, null, null, true, stdout, TextWriter.Null));
        Assert.Empty(stdout.ToString());
    }
}

