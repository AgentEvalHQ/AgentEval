using System.Text.Json.Nodes;
using AgentEval.Interop.AssertAi;
using AgentEval.Results;
using AgentEval.Results.Integrity;
using AgentEval.Samples.Interop;

namespace AgentEval.Tests.Samples;

public sealed class AssertMultiturnBenchmarkTests : IDisposable
{
    private readonly string _output = Path.Combine(Path.GetTempPath(), "assert-multiturn-example-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_output)) Directory.Delete(_output, true); }

    [Fact]
    public async Task WriteAsync_MultiturnFixture_CountsConversationsAndPreservesUnmeasuredCases()
    {
        var example = await AssertMultiturnBenchmark.WriteAsync(AssertMultiturnBenchmark.FindExampleRun(), _output);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(example.RunDirectory).Outcome);
        var report = JsonNode.Parse(File.ReadAllText(example.ReportFile))!;
        Assert.Equal(4, (int)report["declaredCases"]!); Assert.Equal(3, (int)report["observedConversations"]!);
        Assert.Equal(1, (int)report["missingScoreCases"]!);
        var rows = report["observations"]!.AsArray();
        Assert.Equal(new[] { "fail", "pass", "error", "skipped" }, rows.Select(r => (string?)r!["verdict"]));
        Assert.All(rows.Take(3), r => Assert.True((int)r!["userTurns"]! >= 2));
        Assert.Null(rows[3]!["messageTurns"]);
        var lane = Assert.Single(report["lanes"]!.AsArray())!;
        Assert.Equal("scenario", (string?)lane["kind"]); Assert.Equal(2, (int)lane["scoredCases"]!);
        Assert.Equal(1, (int)lane["judgeFailures"]!); Assert.Equal(2, (int)lane["harmDenominator"]!);
        Assert.Equal(0.5, (double)lane["harmRate"]!); Assert.Null(lane["overRefusalRate"]);
        var lines = File.ReadAllLines(Path.Combine(example.RunDirectory, "results.ndjson")).Select(l => JsonNode.Parse(l)!).ToArray();
        Assert.Equal(12, lines.Length); Assert.Equal(4, lines.Count(l => l["parentResultId"] is null));
        Assert.All(lines, l => { Assert.Null(l["trial"]); Assert.Null(l["trials"]); });
        var summary = JsonNode.Parse(File.ReadAllText(Path.Combine(example.RunDirectory, "summary.json")))!;
        var harm = Assert.Single(summary["lanes"]![0]!["metrics"]!.AsArray(), m => (string?)m!["metric"] == "harm")!;
        Assert.Equal(2, (int)harm["n"]!); Assert.Equal(2, (int)harm["notMeasured"]!); Assert.Equal(0.5, (double)harm["value"]!);
        Assert.False(File.Exists(Path.Combine(example.RunDirectory, "gates.ndjson")));
    }

    [Fact]
    public void SavedConversations_EarlierTurns_AreRetainedAlongsideLastQuery()
    {
        var inputs = AssertAiTranscripts.Read(Path.Combine(AssertMultiturnBenchmark.FindExampleRun(), "inference_set.jsonl"));
        foreach (var (_, input) in inputs)
        {
            var turns = (IReadOnlyList<(string Role, string Text)>)input.Metadata![AssertAiTranscripts.TurnsMetadataKey];
            Assert.Equal("I want to discuss my billing account.", turns[0].Text);
            Assert.Equal(turns.Last(t => t.Role == "user").Text, input.Query);
            Assert.NotEqual(turns[0].Text, input.Query);
        }
    }
}
