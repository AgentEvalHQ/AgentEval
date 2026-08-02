// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.RedTeam.MemorySecurity;

namespace AgentEval.Tests.RedTeam.MemorySecurity;

public sealed class MemorySecurityEvaluationTests
{
    [Fact]
    public void DefaultCorpus_CoversEveryWriteChannelAndBenignControls()
    {
        var corpus = MemorySecurityAttackCorpus.Default;

        Assert.All(
            Enum.GetValues<MemoryPoisonWriteChannel>(),
            channel => Assert.Contains(corpus.Scenarios, item => !item.IsBenignControl && item.WriteChannel == channel));
        Assert.Equal(4, corpus.Scenarios.Count(item => item.IsBenignControl));
        Assert.Equal(64, corpus.Fingerprint.Length);
        Assert.DoesNotContain(corpus.Scenarios, item => item.PlantInput.Contains(nameof(MemoryAttackObjective), StringComparison.Ordinal));
    }

    [Fact]
    public void Corpus_MissingMandatoryChannel_Throws()
    {
        var scenarios = MemorySecurityAttackCorpus.Default.Scenarios
            .Where(item => item.IsBenignControl || item.WriteChannel != MemoryPoisonWriteChannel.DirectInjection);

        var error = Assert.Throws<ArgumentException>(
            () => new MemorySecurityAttackCorpus("test", "1", scenarios));

        Assert.Contains(nameof(MemoryPoisonWriteChannel.DirectInjection), error.Message);
    }

    [Fact]
    public void Observation_InconsistentRecallCounts_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new MemorySecurityObservation("MS-BENIGN-001", true, relevantItems: 2, returnedItems: 1, expectedRelevantItems: 2));

    [Fact]
    public void Batch_UnknownScenario_Throws()
        => Assert.Throws<ArgumentException>(
            () => new MemorySecurityEvaluationBatch(
                MemorySecurityAttackCorpus.Default,
                [new MemorySecurityObservation("unknown", false)],
                "policy"));

    [Fact]
    public void Batch_MissingCorpusScenario_Throws()
    {
        var observations = MemorySecurityAttackCorpus.Default.Scenarios
            .Skip(1)
            .Select(SecureObservation);

        var error = Assert.Throws<ArgumentException>(
            () => new MemorySecurityEvaluationBatch(
                MemorySecurityAttackCorpus.Default,
                observations,
                "policy"));

        Assert.Contains("missing corpus scenarios", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Batch_UnevenScenarioTrials_Throws()
    {
        var observations = MemorySecurityAttackCorpus.Default.Scenarios
            .Select(SecureObservation)
            .Append(SecureObservation(MemorySecurityAttackCorpus.Default.Scenarios[0]));

        var error = Assert.Throws<ArgumentException>(
            () => new MemorySecurityEvaluationBatch(
                MemorySecurityAttackCorpus.Default,
                observations,
                "policy"));

        Assert.Contains("same number of trials", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Composite_SafeCompleteBatch_Passes()
    {
        var input = new EvalInput("offline").WithMemorySecurityBatch(CompleteBatch());

        var result = await MemorySecurityCompositeEvals.Create().EvaluateAsync(input);

        Assert.True(result.Score.Passed);
        Assert.Equal("pass", result.Score.Label);
        Assert.Equal(5, result.Details.SubResults!.Count);
    }

    [Fact]
    public async Task Composite_ConfirmedPoisonPersistence_FailsCritical()
    {
        var batch = CompleteBatch(item =>
            item.ScenarioId == "MS-DIRECT-001"
                ? Replace(item, OutcomeField.PoisonPersisted, true)
                : item);

        var result = await MemorySecurityCompositeEvals.Create()
            .EvaluateAsync(new EvalInput("offline").WithMemorySecurityBatch(batch));

        Assert.False(result.Score.Passed);
        Assert.Equal("fail", result.Score.Label);
        Assert.Equal("critical", result.Score.Severity);
    }

    [Fact]
    public async Task RequiredLeaf_MissingBatch_ReturnsErrorNotPass()
    {
        var result = await new MemoryPoisonContainmentEval().EvaluateAsync(new EvalInput("offline"));

        Assert.Equal("error", result.Score.Label);
        Assert.False(result.Score.Passed);
    }

    [Fact]
    public async Task Composite_InconclusiveScopeEvidence_PropagatesError()
    {
        var batch = CompleteBatch(item =>
            item.ScenarioId == "MS-SCOPE-001"
                ? Replace(item, OutcomeField.CrossScopeLeak, null)
                : item);

        var result = await MemorySecurityCompositeEvals.Create()
            .EvaluateAsync(new EvalInput("offline").WithMemorySecurityBatch(batch));

        Assert.Equal("error", result.Score.Label);
        Assert.False(result.Score.Passed);
    }

    [Fact]
    public async Task Utility_BenignFailure_IsMediumAndSeparateFromSecurity()
    {
        var batch = CompleteBatch(item =>
            item.IsBenignControl
                ? Replace(item, OutcomeField.WriteAccepted, false)
                : item);

        var result = await new MemoryUtilityEval().EvaluateAsync(
            new EvalInput("offline").WithMemorySecurityBatch(batch));

        Assert.Equal("fail", result.Score.Label);
        Assert.Equal("medium", result.Score.Severity);
        Assert.Equal(0, result.Score.Value);
    }

    [Fact]
    public void CalibrationReporter_UsesOnlyConclusiveObjectiveDenominators()
    {
        var batch = CompleteBatch(item =>
            item.ScenarioId == "MS-DIRECT-001"
                ? Replace(Replace(item, OutcomeField.PoisonPersisted, null), OutcomeField.Activated, true)
                : item);

        var report = MemorySecurityCalibrationReporter.Build(batch);

        Assert.Equal(5, report.PersistenceRate.ConclusiveDenominator);
        Assert.Equal(1, report.PersistenceRate.InconclusiveCount);
        Assert.Equal(1, report.ActivationRate.Numerator);
        Assert.Equal(4, report.BenignWriteAcceptanceRate.ConclusiveDenominator);
        Assert.Equal("policy-v1", report.PolicyFingerprint);
    }

    [Theory]
    [InlineData(0, 0, 0, 1)]
    [InlineData(10, 10, 0.722, 1)]
    [InlineData(0, 10, 0, 0.278)]
    public void WilsonInterval_IsBounded(int numerator, int denominator, double expectedLower, double expectedUpper)
    {
        var (lower, upper) = MemorySecurityCalibrationReporter.WilsonInterval(numerator, denominator);

        Assert.InRange(lower, Math.Max(0, expectedLower - 0.002), Math.Min(1, expectedLower + 0.002));
        Assert.InRange(upper, Math.Max(0, expectedUpper - 0.002), Math.Min(1, expectedUpper + 0.002));
    }

    [Fact]
    public void JudgeCalibration_CoinFlipAccuracy_IsNotPromotionReady()
    {
        var cases = Enumerable.Range(0, 100)
            .Select(index => new MemoryJudgeCalibrationCase(
                $"case-{index}",
                GoldUnsafe: index % 2 == 0,
                PredictedUnsafe: index % 4 < 2,
                Reviewed: true));

        var report = MemoryJudgeCalibration.Evaluate(cases);

        Assert.False(report.ReadyForPromotion);
        Assert.Equal(0.5, report.Accuracy.Estimate, 5);
        Assert.NotEmpty(report.BlockingReasons);
    }

    [Fact]
    public void JudgeCalibration_PerfectBalancedHeldOutSet_CanBePromotionReady()
    {
        var cases = Enumerable.Range(0, 200)
            .Select(index => new MemoryJudgeCalibrationCase(
                $"case-{index}",
                GoldUnsafe: index % 2 == 0,
                PredictedUnsafe: index % 2 == 0,
                Reviewed: true));

        var report = MemoryJudgeCalibration.Evaluate(cases);

        Assert.True(report.ReadyForPromotion);
        Assert.Empty(report.BlockingReasons);
        Assert.True(report.Accuracy.LowerBound > 0.8);
    }

    [Fact]
    public void JudgeCalibration_ProviderFailure_IsInconclusiveAndCannotPromote()
    {
        var cases = Enumerable.Range(0, 80)
            .Select(index => new MemoryJudgeCalibrationCase(
                $"case-{index}",
                GoldUnsafe: index % 2 == 0,
                PredictedUnsafe: null,
                Reviewed: true));

        var report = MemoryJudgeCalibration.Evaluate(cases);

        Assert.False(report.ReadyForPromotion);
        Assert.Equal(80, report.InconclusiveCount);
        Assert.Equal(0, report.ReviewedConclusiveCount);
    }

    private static MemorySecurityEvaluationBatch CompleteBatch(
        Func<MemorySecurityObservation, MemorySecurityObservation>? mutate = null)
    {
        var observations = MemorySecurityAttackCorpus.Default.Scenarios.Select(scenario =>
        {
            var observation = SecureObservation(scenario);
            return mutate?.Invoke(observation) ?? observation;
        });
        return new MemorySecurityEvaluationBatch(MemorySecurityAttackCorpus.Default, observations, "policy-v1");
    }

    private static MemorySecurityObservation SecureObservation(MemorySecurityScenario scenario)
        => new(
                scenario.Id,
                scenario.IsBenignControl,
                writeAccepted: scenario.IsBenignControl,
                poisonPersisted: Has(scenario, MemoryAttackObjective.PoisonPersistence) ? false : null,
                activated: Has(scenario, MemoryAttackObjective.DelayedActivation) ? false : null,
                crossScopeLeak: Has(scenario, MemoryAttackObjective.CrossScopeLeak) ? false : null,
                unsafeToolAction: Has(scenario, MemoryAttackObjective.UnsafeToolAction) ? false : null,
                exfiltration: Has(scenario, MemoryAttackObjective.Exfiltration) ? false : null,
                unauthorizedOverwrite: Has(scenario, MemoryAttackObjective.UnauthorizedOverwrite) ? false : null,
                trustEscalation: Has(scenario, MemoryAttackObjective.TrustEscalation) ? false : null,
                retrievalCrowdOut: Has(scenario, MemoryAttackObjective.RetrievalCrowdOut) ? false : null,
                resourceFlood: Has(scenario, MemoryAttackObjective.ResourceFlood) ? false : null,
                tamperAccepted: Has(scenario, MemoryAttackObjective.TamperAcceptance) ? false : null,
                attributed: scenario.IsBenignControl ? null : true,
                rolledBack: scenario.IsBenignControl ? null : true,
                relevantItems: scenario.IsBenignControl ? 1 : 0,
                returnedItems: scenario.IsBenignControl ? 1 : 0,
                expectedRelevantItems: scenario.IsBenignControl ? 1 : 0);

    private static MemorySecurityObservation Replace(
        MemorySecurityObservation item,
        OutcomeField field,
        bool? value)
        => new(
            item.ScenarioId,
            item.IsBenignControl,
            writeAccepted: field == OutcomeField.WriteAccepted ? value : item.WriteAccepted,
            poisonPersisted: field == OutcomeField.PoisonPersisted ? value : item.PoisonPersisted,
            activated: field == OutcomeField.Activated ? value : item.Activated,
            crossScopeLeak: field == OutcomeField.CrossScopeLeak ? value : item.CrossScopeLeak,
            unsafeToolAction: item.UnsafeToolAction,
            exfiltration: item.Exfiltration,
            unauthorizedOverwrite: item.UnauthorizedOverwrite,
            trustEscalation: item.TrustEscalation,
            retrievalCrowdOut: item.RetrievalCrowdOut,
            resourceFlood: item.ResourceFlood,
            tamperAccepted: item.TamperAccepted,
            attributed: item.Attributed,
            rolledBack: item.RolledBack,
            relevantItems: item.RelevantItems,
            returnedItems: item.ReturnedItems,
            expectedRelevantItems: item.ExpectedRelevantItems);

    private enum OutcomeField
    {
        WriteAccepted,
        PoisonPersisted,
        Activated,
        CrossScopeLeak,
    }

    private static bool Has(MemorySecurityScenario scenario, MemoryAttackObjective objective)
        => (scenario.Objectives & objective) != 0;
}
