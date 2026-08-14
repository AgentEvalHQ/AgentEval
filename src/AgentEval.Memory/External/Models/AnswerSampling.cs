// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.External.Models;

/// <summary>
/// How far a requested answer-sampling parameter actually got, for one question.
/// </summary>
/// <remarks>
/// The values are ordered by how much they let a report claim. Nothing here ever asserts that a
/// provider <i>used</i> a value, because a provider that ignores a seed returns an ordinary
/// response and there is no observation that separates it from one that honoured it. The closest
/// available evidence is the provider echoing the value back, and that is a distinct value here
/// rather than being folded into "applied".
/// </remarks>
public enum AnswerSamplingDisposition
{
    /// <summary>The caller did not ask for this parameter.</summary>
    NotRequested = 0,

    /// <summary>
    /// The caller asked for it and the agent under test does not implement
    /// <see cref="AgentEval.Core.IAnswerSamplingConfigurableAgent"/>, so the value never left
    /// AgentEval. The run is <i>not</i> pinned, and this is the honest way to say so.
    /// </summary>
    NotSupportedByAgent = 1,

    /// <summary>
    /// The agent accepted the value but declined this particular parameter — an adapter that can
    /// express a temperature but not a seed reports the seed here.
    /// </summary>
    DeclinedByAgent = 2,

    /// <summary>
    /// The agent reported attaching the value to its outbound call, and the provider did not reject
    /// it. Two limits, both deliberate: the attachment is the agent's own claim — AgentEval cannot
    /// see inside an adapter it does not own — and a provider that ignores a parameter answers
    /// exactly like one that used it. This is "sent", not "honoured".
    /// </summary>
    SentUnverified = 3,

    /// <summary>
    /// The value was sent and the provider echoed the same value back — the strongest confirmation
    /// available that it was received rather than dropped.
    /// </summary>
    SentAndEchoed = 4,

    /// <summary>
    /// The value was sent and the provider echoed back a <i>different</i> value. The run is not
    /// reproducible on this parameter, and reporting it as sent would hide that.
    /// </summary>
    EchoedDifferentValue = 5,

    /// <summary>
    /// The provider rejected the call because of this parameter. AgentEval does not retry without
    /// it: a silent downgrade would produce a run that looks pinned and is not.
    /// </summary>
    RejectedByProvider = 6
}

/// <summary>
/// What happened to the requested answer-sampling parameters on one question.
/// </summary>
public sealed class AnswerSamplingOutcome
{
    /// <summary>Temperature the caller asked for, or null when none was requested.</summary>
    public double? RequestedTemperature { get; init; }

    /// <summary>Seed the caller asked for, or null when none was requested.</summary>
    public int? RequestedSeed { get; init; }

    /// <summary>How far the requested temperature got.</summary>
    public required AnswerSamplingDisposition TemperatureDisposition { get; init; }

    /// <summary>How far the requested seed got.</summary>
    public required AnswerSamplingDisposition SeedDisposition { get; init; }

    /// <summary>Temperature the provider echoed back, when it echoed one.</summary>
    public double? EchoedTemperature { get; init; }

    /// <summary>Seed the provider echoed back, when it echoed one.</summary>
    public int? EchoedSeed { get; init; }

    /// <summary>
    /// True when the provider refused the call because of a parameter AgentEval sent. The question's
    /// <see cref="QuestionResult.SafeFailureCode"/> is derived from this, so a failure code and a
    /// disposition can never tell two different stories about the same question.
    /// </summary>
    public bool WasRejectedByProvider =>
        TemperatureDisposition == AnswerSamplingDisposition.RejectedByProvider ||
        SeedDisposition == AnswerSamplingDisposition.RejectedByProvider;
}

/// <summary>
/// Run-level rollup of one answer-sampling parameter, counted over the questions that ran.
/// </summary>
public sealed class AnswerSamplingParameterReport
{
    /// <summary>Whether the caller asked for this parameter at all.</summary>
    public required bool Requested { get; init; }

    /// <summary>Questions where the parameter was not requested.</summary>
    public required int NotRequestedQuestions { get; init; }

    /// <summary>Questions where the agent could not receive sampling parameters at all.</summary>
    public required int NotSupportedByAgentQuestions { get; init; }

    /// <summary>Questions where the agent received the request and declined this parameter.</summary>
    public required int DeclinedByAgentQuestions { get; init; }

    /// <summary>Questions where the value was sent and neither echoed nor rejected.</summary>
    public required int SentUnverifiedQuestions { get; init; }

    /// <summary>Questions where the provider echoed the requested value back.</summary>
    public required int SentAndEchoedQuestions { get; init; }

    /// <summary>Questions where the provider echoed a different value back.</summary>
    public required int EchoedDifferentValueQuestions { get; init; }

    /// <summary>Questions where the provider rejected the call because of this parameter.</summary>
    public required int RejectedByProviderQuestions { get; init; }

    /// <summary>Questions where the value reached the provider in any form.</summary>
    public int ReachedProviderQuestions =>
        SentUnverifiedQuestions + SentAndEchoedQuestions + EchoedDifferentValueQuestions;

    /// <summary>
    /// True when every question that ran carried the requested value to the provider. This is the
    /// weaker of the two claims: it says the value was sent, not that it was used.
    /// </summary>
    public bool CarriedByEveryQuestion { get; init; }

    /// <summary>
    /// True when every question that ran had the requested value echoed back unchanged. This is the
    /// strongest claim the wire supports, and it is still not proof that sampling was deterministic.
    /// </summary>
    public bool ConfirmedByEveryQuestion { get; init; }
}

/// <summary>
/// What a run did with <see cref="ExternalBenchmarkOptions.AnswerTemperature"/> and
/// <see cref="ExternalBenchmarkOptions.AnswerSeed"/>, counted from the questions that ran.
/// </summary>
/// <remarks>
/// <para>
/// The answer model's disagreement with itself is the floor beneath which no memory improvement is
/// detectable. Pinning the answer call is how that floor is lowered — and a request that never
/// reached the provider lowers nothing while looking identical in the configuration. So the request
/// and its fate are both recorded here, per parameter, counted over the same
/// <see cref="ExternalBenchmarkResult.QuestionResults"/> the accuracy denominators come from.
/// </para>
/// <para>
/// Null on a result means the caller requested no answer sampling, which is the historical
/// behaviour and leaves the answer call exactly as it was.
/// </para>
/// </remarks>
public sealed class AnswerSamplingReport
{
    /// <summary>Temperature the caller asked for, or null.</summary>
    public double? RequestedTemperature { get; init; }

    /// <summary>Seed the caller asked for, or null.</summary>
    public int? RequestedSeed { get; init; }

    /// <summary>Questions counted into this report.</summary>
    public required int Questions { get; init; }

    /// <summary>Temperature rollup.</summary>
    public required AnswerSamplingParameterReport Temperature { get; init; }

    /// <summary>Seed rollup.</summary>
    public required AnswerSamplingParameterReport Seed { get; init; }

    /// <summary>
    /// Builds the rollup from per-question outcomes. Questions that never reached the answer call
    /// contribute nothing, because nothing was sent for them.
    /// </summary>
    internal static AnswerSamplingReport? From(
        ExternalBenchmarkOptions options,
        IReadOnlyList<AnswerSamplingOutcome?> outcomes)
    {
        if (options.AnswerTemperature is null && options.AnswerSeed is null)
            return null;

        var observed = outcomes.Where(outcome => outcome is not null).Select(o => o!).ToList();

        return new AnswerSamplingReport
        {
            RequestedTemperature = options.AnswerTemperature,
            RequestedSeed = options.AnswerSeed,
            Questions = observed.Count,
            Temperature = Rollup(
                options.AnswerTemperature.HasValue,
                observed.Select(o => o.TemperatureDisposition).ToList()),
            Seed = Rollup(
                options.AnswerSeed.HasValue,
                observed.Select(o => o.SeedDisposition).ToList())
        };
    }

    private static AnswerSamplingParameterReport Rollup(
        bool requested,
        IReadOnlyList<AnswerSamplingDisposition> dispositions)
    {
        int Count(AnswerSamplingDisposition disposition) =>
            dispositions.Count(d => d == disposition);

        var sentUnverified = Count(AnswerSamplingDisposition.SentUnverified);
        var echoed = Count(AnswerSamplingDisposition.SentAndEchoed);
        var echoedDifferent = Count(AnswerSamplingDisposition.EchoedDifferentValue);
        var reached = sentUnverified + echoed + echoedDifferent;

        return new AnswerSamplingParameterReport
        {
            Requested = requested,
            NotRequestedQuestions = Count(AnswerSamplingDisposition.NotRequested),
            NotSupportedByAgentQuestions = Count(AnswerSamplingDisposition.NotSupportedByAgent),
            DeclinedByAgentQuestions = Count(AnswerSamplingDisposition.DeclinedByAgent),
            SentUnverifiedQuestions = sentUnverified,
            SentAndEchoedQuestions = echoed,
            EchoedDifferentValueQuestions = echoedDifferent,
            RejectedByProviderQuestions = Count(AnswerSamplingDisposition.RejectedByProvider),
            // An empty run carried nothing, so neither claim holds — reporting true for zero
            // questions would let "nothing ran" read as "everything was pinned".
            CarriedByEveryQuestion = requested && dispositions.Count > 0 && reached == dispositions.Count,
            ConfirmedByEveryQuestion = requested && dispositions.Count > 0 && echoed == dispositions.Count
        };
    }
}
