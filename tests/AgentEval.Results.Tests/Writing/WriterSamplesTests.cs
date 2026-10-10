using System.Text.Json.Nodes;
using AgentEval.Results.Conformance.Ops;

namespace AgentEval.Results.Tests.Writing;

/// <summary>
/// The sample runs of the driver's <c>write-samples</c> (rich, content-off, aborted, unsealed, overlays), checked with
/// the .NET verifier in process: each verifier output has what the writer meant. writer-crosscheck.sh (in
/// tests/AgentEval.Results.Conformance) runs the same samples through the reference verifier, tools/aef_verify.py, in the
/// Linux container, and requires the two to agree.
/// </summary>
public class WriterSamplesTests
{
    [Fact]
    public void EverySample_VerifiesAsTheWriterMeant()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aef-samples-{Guid.NewGuid():N}");
        try
        {
            var manifest = WriterSamples.Write(root);

            var checks = manifest["checks"]!.AsArray();
            Assert.Equal(13, checks.Count);
            foreach (var check in checks.Select(c => c!.AsObject()))
            {
                var output = check["dotnet"]!.AsObject();
                foreach (var (name, want) in check["want"]!.AsObject())
                {
                    Assert.True(JsonNode.DeepEquals(want, output[name]), $"{check["args"]!.ToJsonString()}: {name} is {output[name]?.ToJsonString()}, not {want?.ToJsonString()}");
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WriteSamples_IsADriverOperation_ThatRefusesAFolderThatIsNotEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aef-samples-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "x"), "x");
        try
        {
            var (stdout, stderr) = (new StringWriter(), new StringWriter());

            Assert.Equal(2, Conformance.Program.Dispatch(["write-samples", root], stdout, stderr));
            Assert.Equal(2, Conformance.Program.Dispatch(["write-samples"], stdout, stderr));
            Assert.Empty(stdout.ToString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
