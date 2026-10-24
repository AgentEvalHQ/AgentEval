// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Memory.Engine;
using AgentEval.Memory.Extensions;
using AgentEval.Memory.Metrics;
using AgentEval.Memory.Models;
using AgentEval.Memory.Temporal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentEval.Memory.Tests.Temporal;

public class TemporalMemoryAbsenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunTemporalScenarioAsync_OnlyGeneralQueryMeasured_DoesNotInventTemporalScore(bool temporalMeasured)
    {
        var temporal = new MemoryQuery { Question = "when?", ExpectedFacts = [], QueryTime = DateTimeOffset.UnixEpoch };
        var general = new MemoryQuery { Question = "what?", ExpectedFacts = [] };
        var result = new MemoryEvaluationResult
        {
            OverallScore = 100, ScenarioName = "mixed", Duration = TimeSpan.Zero,
            FoundFacts = [], MissingFacts = [], ForbiddenFound = [],
            QueryResults = [Answer(general, true, 100), Answer(temporal, temporalMeasured, 0)],
            Metadata = new() { ["TemporalScore"] = 100.0, ["TemporalAccuracy"] = 100.0 },
        };
        var runner = new TemporalMemoryRunner(new StubRunner(result), NullLogger<TemporalMemoryRunner>.Instance);
        var enhanced = await runner.RunTemporalScenarioAsync(new UnusedAgent(), new MemoryTestScenario
        {
            Name = "mixed", Steps = [], Queries = [general, temporal],
        });
        Assert.Equal(100, enhanced.OverallScore);
        var metric = await new MemoryTemporalMetric(NullLogger<MemoryTemporalMetric>.Instance)
            .EvaluateAsync(enhanced.ToEvaluationContext());
        Assert.Equal(temporalMeasured, metric.Measured);
        if (temporalMeasured)
        {
            Assert.Equal(0.0, enhanced.Metadata!["TemporalScore"]);
            Assert.Equal(0.0, enhanced.Metadata["TemporalAccuracy"]);
            Assert.Equal(0, metric.Score);
        }
        else
        {
            Assert.False(enhanced.Metadata!.ContainsKey("TemporalScore"));
            Assert.False(enhanced.Metadata.ContainsKey("TemporalAccuracy"));
        }
    }

    private static MemoryQueryResult Answer(MemoryQuery query, bool measured, double score) => new()
    {
        Query = query, Response = "a", Measured = measured, Score = score,
        FoundFacts = [], MissingFacts = [], ForbiddenFound = [],
    };

    private sealed class StubRunner(MemoryEvaluationResult result) : IMemoryTestRunner
    {
        public Task<MemoryEvaluationResult> RunAsync(IEvaluableAgent agent, MemoryTestScenario scenario, CancellationToken cancellationToken = default) => Task.FromResult(result);
        public Task<MemoryEvaluationResult> RunMemoryTestAsync(IEvaluableAgent agent, MemoryTestScenario scenario, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MemoryEvaluationResult> RunMemoryQueriesAsync(IEvaluableAgent agent, IEnumerable<MemoryQuery> queries, string scenarioName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnusedAgent : IEvaluableAgent
    {
        public string Name => "offline";
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
