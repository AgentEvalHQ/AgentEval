// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Cli.Commands;
using AgentEval.Core;
using Xunit;

namespace AgentEval.Tests.Cli;

[Collection("EnvVarTests")]
public sealed class BenchSummaryNotMeasuredTests
{
    private sealed class NoVerdictJudge : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
            => Task.FromResult(new EvaluationResult { OverallScore = 0, EvaluationFailed = true, Summary = "offline judge failure" });
    }

    [Theory]
    [InlineData("gdpr", "gdpr-evidence.json")]
    [InlineData("eu", "eu-ai-act-evidence.json")]
    [InlineData("agentic", "agentic-result.json")]
    public async Task Bench_NothingMeasured_PrintsNoScoreAndPersistsNull(string kind, string reportName)
    {
        var root = Path.Combine(Path.GetTempPath(), $"summary-cli-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, ".agenteval");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "solution.json"), JsonSerializer.Serialize(new { schemaVersion = "1.0", id = Guid.NewGuid(), name = "summary" }));
        var original = Console.Out;
        using var captured = new StringWriter();
        try
        {
            Console.SetOut(captured);
            var judge = new NoVerdictJudge();
            var exit = kind switch
            {
                "gdpr" => await BenchCommand.RunGdprAsync("smoke", "Summary", root, "q", evaluatorOverride: judge, responseText: "a", noAef: true),
                "eu" => await BenchEuAiActCommand.RunAsync("smoke", "Summary", root, "q", evaluatorOverride: judge, responseText: "a", noAef: true),
                _ => await BenchAgenticCommand.RunAsync("glass-box-diagnostics", "Summary", root, "q", evaluatorOverride: judge, responseText: "a", budgetTier: null, noAef: true),
            };
            Assert.True(exit is 9 or 10 or 11, $"Expected unmeasured verdict, got {exit}: {captured}");
            Assert.Matches(@"Overall result: (ERROR|SKIPPED) \(score n/a", captured.ToString());
            Assert.DoesNotMatch(@"score 0\s?%", captured.ToString());
            var path = Assert.Single(Directory.GetFiles(root, reportName, SearchOption.AllDirectories));
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("summary").GetProperty("overallScore").ValueKind);
        }
        finally
        {
            Console.SetOut(original);
            Directory.Delete(root, recursive: true);
        }
    }
}
