using System.Text.Json.Nodes;

namespace AgentEval.Results.Tests.Corpus;

/// <summary>
/// The <c>link</c> generate step (spec 09 §9.2.1, round 7): <c>{"link": [PATH, TARGET]}</c> makes PATH a symbolic link
/// to TARGET, both relative to the vector's folder, the link holding the relative path between them; a platform that
/// cannot make one skips the vector.
/// </summary>
public class GeneratedVectorsTests
{
    [Fact]
    public void ALinkStep_MakesARelativeSymbolicLink_OrSkipsTheVector()
    {
        var vector = Path.Combine(AefCorpus.Conformance, "chain-vectors", "withheld-blob-link-under-overlays");
        var steps = JsonNode.Parse(File.ReadAllBytes(Path.Combine(vector, "expected.json")))!["generate"]!.AsArray();

        var folder = GeneratedVectors.Of("test:" + nameof(ALinkStep_MakesARelativeSymbolicLink_OrSkipsTheVector), vector, steps);
        if (folder is null)
        {
            return;   // this system does not let the process make links: the vector is skipped, never failed
        }

        var link = new FileInfo(Path.Combine(folder, "run", "overlays", "extra.json"));
        Assert.Equal(Path.Combine("..", "seal.json"), link.LinkTarget);   // relative: from run/overlays/ to run/seal.json
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(folder, "run", "seal.json")),
            File.ReadAllBytes(link.FullName));                                   // and it resolves to the target
    }

    [Fact]
    public void ALinkToAFolder_IsAFolderLink()
    {
        var vector = Path.Combine(AefCorpus.Conformance, "limits", "overlays-is-a-link");
        var steps = JsonNode.Parse(File.ReadAllBytes(Path.Combine(vector, "expected.json")))!["generate"]!.AsArray();

        var folder = GeneratedVectors.Of("test:" + nameof(ALinkToAFolder_IsAFolderLink), vector, steps);
        if (folder is null)
        {
            return;
        }

        var link = new DirectoryInfo(Path.Combine(folder, "run", "overlays"));
        Assert.Equal(Path.Combine("..", "elsewhere"), link.LinkTarget);
        Assert.True(File.Exists(Path.Combine(link.FullName, "events.ndjson")));   // the copied overlays, behind the link
    }

    [Fact]
    public void ALinkPathThatWouldLeaveTheVector_IsRefused()
    {
        var vector = Path.Combine(AefCorpus.Conformance, "limits", "overlays-is-a-link");
        var steps = JsonNode.Parse("""[{"link": ["run/overlays", "../outside"]}]""")!.AsArray();

        Assert.Throws<InvalidOperationException>(() => GeneratedVectors.Of("test:" + nameof(ALinkPathThatWouldLeaveTheVector_IsRefused), vector, steps));
    }
}
