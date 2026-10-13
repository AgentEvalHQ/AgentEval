using System.Text;
using AgentEval.Cli.Commands;
using AgentEval.DataLoaders;
using AgentEval.Results.Suites;
using AgentEval.Results.Writing;

namespace AgentEval.Tests.Cli.Aef;

public sealed class DatasetSuiteDefinitionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-cli-definition-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private const string Source = """[{"id":"one","input":"secret question","passingScore":80}]""";

    [Fact]
    public async Task AefOptions_FileChangesAfterLoading_BindsCapturedBytesNotRereadFile()
    {
        Directory.CreateDirectory(_root);
        var file = new FileInfo(Path.Combine(_root, "cases.json"));
        File.WriteAllText(file.FullName, "changed after capture");
        var snapshot = Encoding.UTF8.GetBytes(Source);
        var cases = await DatasetLoaderFactory.LoadSnapshotAsync(snapshot, ".json");
        var options = new EvalOptions { Dataset = file, Format = "json", Model = "model-one" };
        var bound = EvalCommand.AefOptions(options, "model-one", AefTargetMode.Mocked, null, cases, snapshot);
        Assert.Equal(snapshot, bound.DatasetBytes);
        Assert.Equal(SuiteDefinitionManifest.Hash(snapshot), (string?)bound.Definition!.ToJson()["inputs"]!["dataset"]);
        Assert.DoesNotContain("secret question", bound.Definition.ToJson().ToJsonString());
        Assert.Equal(new[] { "one" }, bound.Definition.CaseIds);
        // A different subject model is a comparison candidate, not a different evaluation instrument.
        var otherSubject = EvalCommand.AefOptions(new EvalOptions { Dataset = file, Format = "json", Model = "model-two" }, "model-two", AefTargetMode.Mocked, null, cases, snapshot);
        Assert.Equal(bound.Definition.Digest, otherSubject.Definition!.Digest);
    }

    [Fact]
    public async Task AefOptions_ChangedThresholdJudgeOrTrialPolicy_ChangesDefinition()
    {
        var file = new FileInfo(Path.Combine(_root, "cases.json"));
        var snapshot = Encoding.UTF8.GetBytes(Source);
        var cases = await DatasetLoaderFactory.LoadSnapshotAsync(snapshot, ".json");
        var options = new EvalOptions { Dataset = file, Format = "json" };
        string Digest(EvalOptions opts) => EvalCommand.AefOptions(opts, "subject", AefTargetMode.Mocked, null, cases, snapshot).Definition!.Digest;
        var original = Digest(options);
        Assert.NotEqual(original, Digest(new EvalOptions { Dataset = file, Format = "json", Runs = 3, SuccessThreshold = 0.8 }));
        Assert.NotEqual(original, Digest(new EvalOptions { Dataset = file, Format = "json", JudgeEndpoint = "http://localhost:1234", JudgeModel = "judge" }));
        var changed = Encoding.UTF8.GetBytes(Source.Replace("80", "90"));
        var changedCases = await DatasetLoaderFactory.LoadSnapshotAsync(changed, ".json");
        Assert.NotEqual(original, EvalCommand.AefOptions(options, "subject", AefTargetMode.Mocked, null, changedCases, changed).Definition!.Digest);
    }
}
