// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Calibration;
using AgentEval.Evals.Agentic.Conversation;
using AgentEval.Evals.Agentic.Quality;
using Xunit;

namespace AgentEval.Tests.Agentic;

/// <summary>
/// #203, B12a: the reference answer reaches the judge. <c>AtomicLlmEval</c> sends the judge the query, the response and the
/// context only, so similarity and response completeness never saw <see cref="EvalInput.GroundTruth"/>: the judge improvised
/// a comparison (a fabricated 0.98), and once it was sent its rubric, the missing-reference rule failed every input.
/// </summary>
public sealed class GroundTruthWiringTests
{
    private const string Reference = "REF-7731: Paris is the capital of France.";

    /// <summary>A judge that records what it was given.</summary>
    private sealed class CapturingJudge : IEvaluator
    {
        public List<string> Inputs { get; } = [];

        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
        {
            Inputs.Add(input);
            return Task.FromResult(new EvaluationResult
            {
                OverallScore = 90,
                Summary = "captured",
                CriteriaResults = criteria.Select(c => new CriterionResult { Criterion = c, Met = true, Explanation = "ok" }).ToList(),
            });
        }
    }

    private static string KeyOf(string rubricFile) =>
        Path.GetFileName(rubricFile).Split('.')[0].Replace('-', '_');

    /// <summary>Every rubric that lists <c>ground_truth</c> among its inputs.</summary>
    public static IEnumerable<object[]> RubricsThatReadAReference()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentEval.sln")))
            dir = dir.Parent;
        var prompts = Path.Combine(dir!.FullName, "src", "AgentEval.Evals.Agentic", "Resources", "Prompts");
        return Directory.EnumerateFiles(prompts, "*.md", SearchOption.AllDirectories)
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"^- `ground_truth`", RegexOptions.Multiline))
            .Select(f => new object[] { KeyOf(f) })
            .OrderBy(k => (string)k[0], StringComparer.Ordinal)
            .ToList();
    }

    private static IEval Build(string key, IEvaluator judge) => key switch
    {
        "confidence_calibration" => new ConfidenceCalibrationEval(judge),
        _ => AgenticEvaluatorFixture.BuildEvaluator(key, judge),
    };

    [Theory]
    [MemberData(nameof(RubricsThatReadAReference))]
    public async Task AnEvaluatorWhoseRubricReadsAReference_SendsItToTheJudge(string key)
    {
        var judge = new CapturingJudge();

        var result = await Build(key, judge).EvaluateAsync(new EvalInput(Query: "What is the capital of France?",
            Response: "Paris.", GroundTruth: Reference));

        Assert.NotEqual("skipped", result.Score.Label);
        Assert.Contains(judge.Inputs, i => i.Contains("REF-7731", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCensus_FindsTheRubricsThatReadAReference()
        => Assert.Equal(["confidence_calibration", "response_completeness", "similarity"],
            RubricsThatReadAReference().Select(r => (string)r[0]).ToArray());

    [Fact]
    public async Task WithoutAReference_SimilarityAndF1_AreNotMeasured_NotFailed()
    {
        var judge = new CapturingJudge();
        var input = new EvalInput(Query: "What is the capital of France?", Response: "Paris.");

        var similarity = await new SimilarityEval(judge).EvaluateAsync(input);
        var f1 = await new F1ScoreEval().EvaluateAsync(input);

        Assert.Equal("skipped", similarity.Score.Label);
        Assert.Empty(judge.Inputs);   // no judge call spent on what cannot be measured
        Assert.Equal("skipped", f1.Score.Label);
        Assert.Equal("1.2.0", similarity.Metric.Version);
        Assert.Equal("1.1.0", f1.Metric.Version);
    }

    [Fact]
    public async Task WithoutAReference_TheQaComposite_WithholdsItsPass_NamingWhatWasNotMeasured()
    {
        var composite = AgenticEvaluatorFixture.BuildEvaluator("qa_composite", new FixedScoreEvaluator(95));

        var result = await composite.EvaluateAsync(new EvalInput(Query: "What is the capital of France?",
            Response: "Paris is the capital of France.", Context: "Paris is the capital of France."));

        Assert.NotEqual("pass", result.Score.Label);
        var said = string.Join(" ", new[] { result.Details.Summary }.Concat(result.Details.Recommendations ?? []));
        Assert.Contains("similarity", said, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheJudgeInput_KeepsTheContext_WhenTheReferenceIsFolded()
    {
        // ConfidenceCalibrationEval and SelfCorrectionQualityEval built a new EvalInput of four fields, dropping the context
        // and every other field the caller set (B12a); they now use a with copy.
        var judge = new CapturingJudge();
        await new ConfidenceCalibrationEval(judge).EvaluateAsync(new EvalInput(Query: "q", Response: "r",
            Context: "CTX-5512", GroundTruth: Reference));
        await new SelfCorrectionQualityEval(judge).EvaluateAsync(new EvalInput(Query: "q", Response: "r", Context: "CTX-5513",
            Metadata: new Dictionary<string, object>
            {
                [SelfCorrectionQualityEval.MetadataKey] = new ConversationTurn("user", "That is wrong, check again."),
            }));

        Assert.Contains(judge.Inputs, i => i.Contains("CTX-5512", StringComparison.Ordinal));
        Assert.Contains(judge.Inputs, i => i.Contains("CTX-5513", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EveryGoldenOfAnEvaluatorThatNeedsAReference_CarriesOne()
    {
        // The agentic goldens carried no reference, so similarity was calibrated on a judge that improvised one.
        var loader = new CalibrationDatasetLoader();
        var entries = (await loader.LoadAllFromAssemblyAsync(typeof(GroundTruthWiringTests).Assembly))
            .SelectMany(d => d.Entries).Where(e => e.EvaluatorKey is "similarity").ToList();

        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.GroundTruth), $"{e.ScenarioId} has no groundTruth"));
    }
}
