using System.Text.Json.Nodes;
using AgentEval.Results;
using AgentEval.Results.Integrity;
using AgentEval.Samples.Interop;

namespace AgentEval.Tests.Samples;

public sealed class AssertEvidenceSampleTests : IDisposable
{
    private readonly string _output = Path.Combine(Path.GetTempPath(), "assert-evidence-example-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_output)) Directory.Delete(_output, true); }

    [Fact]
    public async Task WriteAsync_OfflineFixture_SealsSimpleAndCompositeEvidenceWithoutPromotingAbsence()
    {
        var result = await AssertEvidence.WriteAsync(AssertInteropRun.FindExampleRun(), _output);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(result.ImportedRun).Outcome);
        Assert.Equal("replayed", (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(result.ImportedRun, "run.json")))!["execution"]!["targetMode"]);
        Assert.Equal(18, File.ReadAllLines(Path.Combine(result.ImportedRun, "results.ndjson")).Length);
        Assert.Equal(new[] { "fail", "pass", "pass", "warn", "skipped", "error" }, result.Composites.Select(r => r.Label));
        foreach (var row in result.Composites)
        {
            Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(row.Directory).Outcome);
            var lines = File.ReadAllLines(Path.Combine(row.Directory, "results.ndjson")).Select(l => JsonNode.Parse(l)!).ToArray();
            var root = Assert.Single(lines, l => l["parentResultId"] is null);
            Assert.Equal(row.Label switch { "pass" => "passed", "fail" => "failed", _ => row.Label }, (string?)root["state"]);
            Assert.Equal(row.CaseId, (string?)root["ext"]!["assert.sourceCase"]);
            Assert.Equal("Min", (string?)root["aggregation"]!["strategy"]);
            Assert.All(lines.Where(l => (string?)l["parentResultId"] == (string?)root["resultId"]),
                l => { Assert.True((bool)l["component"]!["required"]!); Assert.Equal(1d, (double)l["component"]!["weight"]!); });
            Assert.Contains(lines, l => ((string?)l["path"])?.Contains("assert_ai_verdict", StringComparison.Ordinal) == true);
            Assert.Contains(lines, l => ((string?)l["path"])?.Contains(NoUnconfirmedPayoutChange.KeyName, StringComparison.Ordinal) == true);
            if (row.Label is "skipped" or "error") Assert.Null(root["scores"]);
            Assert.False(File.Exists(Path.Combine(row.Directory, "gates.ndjson")));
        }
    }

    [Fact]
    public async Task WriteAsync_ExistingOutputOrCancellation_DoesNotOverwrite()
    {
        Directory.CreateDirectory(_output); var sentinel = Path.Combine(_output, "keep"); File.WriteAllText(sentinel, "original");
        await Assert.ThrowsAsync<ArgumentException>(() => AssertEvidence.WriteAsync("unused", _output));
        Assert.Equal("original", File.ReadAllText(sentinel));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AssertEvidence.WriteAsync("unused", _output, new(true)));
    }
}
