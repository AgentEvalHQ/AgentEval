// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.External.Models;

namespace AgentEval.Memory.External;

/// <summary>
/// Runs a judge repeatedly over identical inputs and reports how often it disagrees with itself.
/// </summary>
/// <remarks>
/// <para>Separates two things a paired benchmark comparison otherwise confounds: the system under test
/// changing, and the judge being noisy. Without this number, a small score movement between runs cannot
/// be attributed — the judge's own variance is unmeasured, and an unmeasured quantity is a coverage gap
/// rather than zero.</para>
/// <para>The harness measures the judge as configured. Running it against
/// <see cref="ExternalBenchmarkOptions.JudgeTemperature"/> left null measures the provider's default
/// sampling; running it at temperature 0 measures whatever non-determinism survives that. Both are
/// useful, and they are different measurements — the report records which one was taken.</para>
/// </remarks>
public sealed class JudgeAgreementHarness
{
    private readonly IExternalBenchmarkJudge _judge;

    /// <summary>Creates a harness over the judge whose self-agreement is to be measured.</summary>
    public JudgeAgreementHarness(IExternalBenchmarkJudge judge)
        => _judge = judge ?? throw new ArgumentNullException(nameof(judge));

    /// <summary>Fewest repeats that can reveal a disagreement.</summary>
    public const int MinimumRepeats = 2;

    /// <summary>
    /// Judges every case <paramref name="repeats"/> times and reports per-case and overall agreement.
    /// </summary>
    /// <param name="cases">Response/question pairs to re-judge. Identical on every repeat.</param>
    /// <param name="options">Judge options, applied unchanged to every repeat.</param>
    /// <param name="repeats">Times to judge each case. Minimum 2.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<JudgeAgreementReport> MeasureAsync(
        IReadOnlyList<JudgeAgreementCase> cases,
        ExternalBenchmarkOptions? options = null,
        int repeats = MinimumRepeats,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cases);
        if (repeats < MinimumRepeats)
        {
            throw new ArgumentOutOfRangeException(
                nameof(repeats), repeats, $"repeats must be at least {MinimumRepeats}.");
        }

        var effectiveOptions = options ?? new ExternalBenchmarkOptions();
        effectiveOptions.Validate();

        var caseReports = new List<JudgeAgreementCaseReport>(cases.Count);
        var totalJudgeCalls = 0;

        foreach (var judgeCase in cases)
        {
            var statuses = new List<JudgeOutcomeStatus>(repeats);
            for (var repeat = 0; repeat < repeats; repeat++)
            {
                ct.ThrowIfCancellationRequested();
                var judgment = await _judge
                    .JudgeAsync(judgeCase.AgentResponse, judgeCase.Question, effectiveOptions, ct)
                    .ConfigureAwait(false);

                statuses.Add(judgment.Status);
                totalJudgeCalls += judgment.LlmCallCount;
            }

            caseReports.Add(new JudgeAgreementCaseReport
            {
                QuestionId = judgeCase.Question.QuestionId,
                Statuses = statuses,
                Agreed = statuses.Distinct().Count() == 1
            });
        }

        return new JudgeAgreementReport
        {
            Repeats = repeats,
            CaseReports = caseReports,
            TotalJudgeProviderCalls = totalJudgeCalls,
            JudgeTemperature = effectiveOptions.JudgeTemperature,
            JudgeVerdictProtocol = effectiveOptions.JudgeVerdictProtocol
        };
    }
}

/// <summary>One response/question pair to be re-judged.</summary>
public sealed class JudgeAgreementCase
{
    /// <summary>The agent response held constant across repeats.</summary>
    public required string AgentResponse { get; init; }

    /// <summary>The question held constant across repeats.</summary>
    public required ExternalBenchmarkQuestion Question { get; init; }
}

/// <summary>Per-question agreement outcome across repeats.</summary>
public sealed class JudgeAgreementCaseReport
{
    /// <summary>Question identifier.</summary>
    public required string QuestionId { get; init; }

    /// <summary>Status observed on each repeat, in order.</summary>
    public required IReadOnlyList<JudgeOutcomeStatus> Statuses { get; init; }

    /// <summary>Whether every repeat produced the same status.</summary>
    public required bool Agreed { get; init; }
}

/// <summary>Aggregate judge self-agreement over a set of cases.</summary>
public sealed class JudgeAgreementReport
{
    /// <summary>Times each case was judged.</summary>
    public required int Repeats { get; init; }

    /// <summary>Per-question outcomes.</summary>
    public required IReadOnlyList<JudgeAgreementCaseReport> CaseReports { get; init; }

    /// <summary>Total judge provider calls spent producing this report.</summary>
    public required int TotalJudgeProviderCalls { get; init; }

    /// <summary>
    /// The temperature the measurement was taken at. Null means the provider default was used, which is
    /// the historical judge configuration — recorded rather than assumed, because a disagreement rate is
    /// only interpretable alongside it.
    /// </summary>
    public double? JudgeTemperature { get; init; }

    /// <summary>The verdict protocol the measurement was taken under.</summary>
    public JudgeVerdictProtocol JudgeVerdictProtocol { get; init; }

    /// <summary>Cases measured.</summary>
    public int CaseCount => CaseReports.Count;

    /// <summary>Cases where at least two repeats disagreed.</summary>
    public int DisagreementCount => CaseReports.Count(c => !c.Agreed);

    /// <summary>
    /// Fraction of cases where the judge contradicted itself, 0-1. Null when no cases were measured —
    /// an empty run has no rate, and reporting 0 would claim agreement that was never observed.
    /// </summary>
    public double? DisagreementRate => CaseCount == 0
        ? null
        : (double)DisagreementCount / CaseCount;
}
